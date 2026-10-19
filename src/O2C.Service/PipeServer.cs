using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using O2C.Core;
using O2C.Core.Ipc;

namespace O2C.Service;

/// <summary>
/// Канал управления для GUI: \\.\pipe\O2C.Control. Доступ — локальным вошедшим пользователям,
/// из сети — запрещён.
/// </summary>
internal sealed class PipeServer : BackgroundService
{
    private const int MaxClients = 8;

    private readonly ConnectionManager _connection;
    private readonly ExclusionManager _exclusions;
    private readonly SettingsStore _settings;
    private readonly LogBuffer _log;
    private readonly ILogger<PipeServer> _logger;

    public PipeServer(ConnectionManager connection, ExclusionManager exclusions, SettingsStore settings, LogBuffer log, ILogger<PipeServer> logger)
    {
        _connection = connection;
        _exclusions = exclusions;
        _settings = settings;
        _log = log;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var security = CreatePipeSecurity();
        bool first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                pipe = NamedPipeServerStreamAcl.Create(IpcProtocol.PipeName, PipeDirection.InOut, MaxClients,
                    PipeTransmissionMode.Byte, options, 0, 0, security);
                first = false;
            }
            catch (IOException ex)
            {
                // Все экземпляры заняты — подождём.
                _logger.LogWarning(ex, "Не удалось создать экземпляр канала");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }

            _ = HandleClientAsync(pipe, stoppingToken);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var reader = new StreamReader(pipe, utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var writer = new StreamWriter(pipe, utf8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var writeLock = new SemaphoreSlim(1, 1);

        async Task SendAsync(IpcMessage message)
        {
            await writeLock.WaitAsync(cts.Token);
            try
            {
                await writer.WriteLineAsync(IpcProtocol.Serialize(message).AsMemory(), cts.Token);
            }
            finally
            {
                writeLock.Release();
            }
        }

        void Fire(IpcMessage message) => _ = SendAsync(message).ContinueWith(t =>
        {
            // Запись в канал не удалась — клиент ушёл; завершаем обработку.
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        void OnStatus(ConnectionStatus status) => Fire(new IpcMessage { Event = IpcEvents.Status, Status = status });
        void OnLog(string line) => Fire(new IpcMessage { Event = IpcEvents.Log, LogLines = [line] });
        void OnExclusions(Core.Exclusions.ExclusionReport report) => Fire(new IpcMessage { Event = IpcEvents.Exclusions, ExclusionReport = report });

        _connection.StatusChanged += OnStatus;
        _log.LineAdded += OnLog;
        _exclusions.ReportChanged += OnExclusions;
        try
        {
            await SendAsync(new IpcMessage { Event = IpcEvents.Status, Status = _connection.Status });
            await SendAsync(new IpcMessage { Event = IpcEvents.Log, LogLines = _log.Snapshot() });
            await SendAsync(new IpcMessage { Event = IpcEvents.Exclusions, ExclusionReport = _exclusions.Report });

            while (!cts.IsCancellationRequested)
            {
                var line = await IpcProtocol.ReadFrameAsync(reader, cts.Token);
                if (line is null)
                    break;
                if (line.Length == 0)
                    continue;

                IpcMessage? request;
                try
                {
                    request = IpcProtocol.Deserialize(line);
                }
                catch (System.Text.Json.JsonException)
                {
                    await SendAsync(new IpcMessage { Ok = false, Error = "Некорректный запрос." });
                    continue;
                }
                if (request is null)
                    continue;

                await SendAsync(await HandleRequestAsync(request));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException)
        {
            // Клиент отключился или прислал мусор.
        }
        finally
        {
            _connection.StatusChanged -= OnStatus;
            _log.LineAdded -= OnLog;
            _exclusions.ReportChanged -= OnExclusions;
            await cts.CancelAsync();
            // writeLock не освобождаем явно: событие, пришедшее в момент отписки, ещё может его ждать.
            await pipe.DisposeAsync();
        }
    }

    private async Task<IpcMessage> HandleRequestAsync(IpcMessage request)
    {
        try
        {
            switch (request.Command)
            {
                case IpcCommands.GetStatus:
                    return new IpcMessage { Id = request.Id, Ok = true, Status = _connection.Status };

                case IpcCommands.Connect:
                    var error = await _connection.ConnectAsync();
                    return new IpcMessage { Id = request.Id, Ok = error is null, Error = error };

                case IpcCommands.Disconnect:
                    // Не ждём завершения: отключение может занять до 15 с, статус придёт событием.
                    _ = _connection.DisconnectAsync();
                    return new IpcMessage { Id = request.Id, Ok = true };

                case IpcCommands.GetSettings:
                    return new IpcMessage { Id = request.Id, Ok = true, Settings = _settings.GetView() };

                case IpcCommands.SaveSettings:
                    if (request.SettingsUpdate is not { } update)
                        return new IpcMessage { Id = request.Id, Ok = false, Error = "Нет данных настроек." };
                    var validation = update.Settings.Validate();
                    if (validation is not null)
                        return new IpcMessage { Id = request.Id, Ok = false, Error = validation };
                    _settings.Save(update);
                    return new IpcMessage { Id = request.Id, Ok = true, Settings = _settings.GetView() };

                case IpcCommands.GetExclusions:
                    return new IpcMessage
                    {
                        Id = request.Id,
                        Ok = true,
                        ExclusionsText = _settings.GetExclusionsText(),
                        ExclusionReport = _exclusions.Report,
                    };

                case IpcCommands.SaveExclusions:
                    // Нераспознанные строки сохраняются как есть и попадают в отчёт с ошибкой.
                    _settings.SaveExclusions(request.ExclusionsText ?? "");
                    return new IpcMessage { Id = request.Id, Ok = true, ExclusionsText = _settings.GetExclusionsText() };

                default:
                    return new IpcMessage { Id = request.Id, Ok = false, Error = $"Неизвестная команда: {request.Command}" };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogError(ex, "Ошибка выполнения команды {Command}", request.Command);
            return new IpcMessage { Id = request.Id, Ok = false, Error = ex.Message };
        }
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }
}

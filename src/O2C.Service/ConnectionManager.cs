using O2C.Core;
using O2C.Core.OpenConnect;

namespace O2C.Service;

/// <summary>
/// Держит желаемое состояние (подключён/нет) и приводит к нему процесс openconnect:
/// запускает, переподключает с нарастающей задержкой, останавливает.
/// </summary>
internal sealed class ConnectionManager : IHostedService, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly LogBuffer _log;
    private readonly ILogger<ConnectionManager> _logger;

    /// <summary>Сериализует операции подключения/отключения/обработки завершения процесса.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ReconnectBackoff _backoff = new();
    private readonly Lock _statusLock = new();

    private bool _desired;

    /// <summary>Причина, по которой отключение должно закончиться ошибкой, а не обычным «Отключено».</summary>
    private string? _stopError;
    private volatile OpenConnectProcess? _current;
    private CancellationTokenSource? _retryCts;
    private DateTimeOffset? _connectedAt;

    /// <summary>
    /// Помощник сообщил, что сеть туннеля настроена. openconnect печатает «Configured as» раньше,
    /// чем создаёт адаптер, поэтому «Подключено» выставляем только после этого сигнала.
    /// </summary>
    private bool _networkReady;
    private Timer? _readyFallback;

    /// <summary>Если сигнал от помощника не пришёл (например, журнал недоступен) — считаем сеть готовой через это время.</summary>
    private static readonly TimeSpan NetworkReadyFallback = TimeSpan.FromSeconds(20);
    private ConnectionStatus _status = ConnectionStatus.Disconnected;

    public ConnectionManager(SettingsStore settings, LogBuffer log, ILogger<ConnectionManager> logger)
    {
        _settings = settings;
        _log = log;
        _logger = logger;
    }

    public event Action<ConnectionStatus>? StatusChanged;

    public ConnectionStatus Status
    {
        get
        {
            lock (_statusLock)
                return _status;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Служба запущена");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Служба останавливается, отключаемся");
        await DisconnectAsync();
    }

    /// <summary>Возвращает текст ошибки или null при успехе.</summary>
    public async Task<string?> ConnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Status.State == ConnectionState.Disconnecting)
                return "Дождитесь завершения отключения.";
            if (_desired)
                return null;

            var settings = _settings.Settings;
            var error = settings.Validate();
            var password = _settings.GetPassword();
            if (error is null && string.IsNullOrEmpty(password))
                error = "Не задан пароль.";
            if (error is null && !File.Exists(ServicePaths.OpenConnectExe))
                error = $"Не найден {ServicePaths.OpenConnectExe}. Переустановите программу.";
            if (error is null && !File.Exists(ServicePaths.ConnectScript))
                error = $"Не найден {ServicePaths.ConnectScript}. Переустановите программу.";
            if (error is not null)
            {
                SetStatus(new ConnectionStatus { State = ConnectionState.Error, Message = error });
                return error;
            }

            _desired = true;
            _backoff.Reset();
            StartProcess(settings, password!, reconnecting: false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _desired = false;
            _logger.LogError(ex, "Не удалось запустить openconnect");
            SetStatus(new ConnectionStatus { State = ConnectionState.Error, Message = $"Не удалось запустить openconnect: {ex.Message}" });
            return ex.Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <param name="error">Если задано — итоговый статус «Ошибка» с этим текстом (например, не настроилась сеть).</param>
    public async Task DisconnectAsync(string? error = null)
    {
        OpenConnectProcess? process;
        await _gate.WaitAsync();
        try
        {
            _desired = false;
            _stopError = error;
            CancelRetry();
            process = _current;
            if (process is null)
            {
                SetStatus(error is null ? ConnectionStatus.Disconnected : new ConnectionStatus { State = ConnectionState.Error, Message = error });
                _stopError = null;
                return;
            }
            SetStatus(new ConnectionStatus { State = ConnectionState.Disconnecting, Message = "Отключение..." });
        }
        finally
        {
            _gate.Release();
        }

        // Вне _gate: обработчик завершения процесса сам возьмёт _gate и выставит итоговый статус.
        await process.StopAsync();
    }

    private void StartProcess(ConnectionSettings settings, string password, bool reconnecting)
    {
        lock (_statusLock)
        {
            _connectedAt = null;
            _networkReady = false;
            _readyFallback?.Dispose();
            _readyFallback = null;
        }
        SetStatus(new ConnectionStatus
        {
            State = reconnecting ? ConnectionState.Reconnecting : ConnectionState.Connecting,
            Message = reconnecting ? "Переподключение..." : "Подключение...",
        });
        _log.Add($"--- Подключение к {settings.Server} ({settings.Protocol}), пользователь {settings.Username} ---");

        var process = OpenConnectProcess.Start(settings, password, OnOutput);
        _current = process;
        _logger.LogInformation("openconnect запущен, PID {Pid}", process.ProcessId);
        // Вывод читается с момента старта; если туннель поднялся раньше присваивания _current — учтём здесь.
        MarkConnectedIfNeeded(process);
        _ = process.Exited.ContinueWith(t => OnExitedAsync(process, t.Result), TaskScheduler.Default).Unwrap();
    }

    private void OnOutput(OpenConnectProcess process, string line)
    {
        // Шум (например, повторяемые попытки DTLS) — только в файл, не в окно.
        if (LogNoise.IsNoise(line))
            _log.AddFileOnly(line);
        else
            _log.Add(line);
        if (process.Parser.IsConnected && ReferenceEquals(process, _current))
            StartReadyFallback(process);
        MarkConnectedIfNeeded(process);
    }

    /// <summary>Вызывается, когда помощник настройки сети закончил работу (маркер READY в его журнале).</summary>
    public void OnNetworkReady()
    {
        lock (_statusLock)
            _networkReady = true;
        if (_current is { } process)
            MarkConnectedIfNeeded(process);
    }

    private void StartReadyFallback(OpenConnectProcess process)
    {
        lock (_statusLock)
        {
            if (_readyFallback is not null || _networkReady)
                return;
            _readyFallback = new Timer(_ =>
            {
                bool notify;
                lock (_statusLock)
                {
                    notify = !_networkReady && ReferenceEquals(process, _current);
                    _networkReady = true;
                }
                if (notify)
                    _log.Add("Сигнал о настройке сети не получен — считаем подключение установленным.");
                MarkConnectedIfNeeded(process);
            }, null, NetworkReadyFallback, Timeout.InfiniteTimeSpan);
        }
    }

    private void MarkConnectedIfNeeded(OpenConnectProcess process)
    {
        lock (_statusLock)
        {
            if (!process.Parser.IsConnected || !_networkReady || _connectedAt is not null || !ReferenceEquals(process, _current))
                return;
            _connectedAt = DateTimeOffset.Now;
            _readyFallback?.Dispose();
            _readyFallback = null;
        }
        _logger.LogInformation("Туннель поднят, адрес {Address}", process.Parser.AssignedAddress);
        SetStatus(new ConnectionStatus
        {
            State = ConnectionState.Connected,
            AssignedIp = process.Parser.AssignedAddress,
            ConnectedSince = _connectedAt,
        });
    }

    private async Task OnExitedAsync(OpenConnectProcess process, int exitCode)
    {
        await _gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(process, _current))
                return;
            _current = null;
            bool wasStable;
            lock (_statusLock)
                wasStable = _connectedAt is { } since && DateTimeOffset.Now - since >= ReconnectBackoff.StableAfter;
            var failure = process.Parser.ClassifyExit();
            var rejectedPin = process.Parser.RejectedCertPin;
            process.Dispose();

            _log.Add($"--- openconnect завершился (код {exitCode}, причина: {failure}) ---");
            _logger.LogInformation("openconnect завершился: код {Code}, причина {Failure}", exitCode, failure);

            if (!_desired)
            {
                SetStatus(_stopError is null ? ConnectionStatus.Disconnected : new ConnectionStatus { State = ConnectionState.Error, Message = _stopError });
                _stopError = null;
                return;
            }

            if (failure == FailureKind.Authentication)
            {
                // Повторять нельзя: несколько неверных попыток подряд могут заблокировать учётную запись.
                _desired = false;
                SetStatus(new ConnectionStatus { State = ConnectionState.Error, Message = "Ошибка авторизации: проверьте логин, пароль и группу." });
                return;
            }

            if (failure == FailureKind.Certificate)
            {
                _desired = false;
                SetStatus(new ConnectionStatus
                {
                    State = ConnectionState.Error,
                    Message = rejectedPin is null
                        ? "Сертификат сервера не прошёл проверку."
                        : "Сертификат сервера не доверенный. Если вы уверены в сервере, укажите отпечаток в настройках.",
                    RejectedCertPin = rejectedPin,
                });
                return;
            }

            if (!_settings.Settings.AutoReconnect)
            {
                _desired = false;
                SetStatus(new ConnectionStatus { State = ConnectionState.Error, Message = $"Соединение прервано (код {exitCode})." });
                return;
            }

            if (wasStable)
                _backoff.Reset();
            ScheduleReconnect(_backoff.NextDelay());
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ScheduleReconnect(TimeSpan delay)
    {
        CancelRetry();
        var cts = new CancellationTokenSource();
        _retryCts = cts;
        SetStatus(new ConnectionStatus { State = ConnectionState.Reconnecting, Message = $"Соединение потеряно, повтор через {delay.TotalSeconds:0} с." });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await _gate.WaitAsync();
            try
            {
                if (cts.IsCancellationRequested || !_desired || _current is not null)
                    return;
                var password = _settings.GetPassword();
                if (string.IsNullOrEmpty(password))
                {
                    _desired = false;
                    SetStatus(new ConnectionStatus { State = ConnectionState.Error, Message = "Не задан пароль." });
                    return;
                }
                StartProcess(_settings.Settings, password, reconnecting: true);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                _logger.LogError(ex, "Переподключение не удалось");
                ScheduleReconnect(_backoff.NextDelay());
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private void CancelRetry()
    {
        _retryCts?.Cancel();
        _retryCts?.Dispose();
        _retryCts = null;
    }

    private void SetStatus(ConnectionStatus status)
    {
        lock (_statusLock)
            _status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        CancelRetry();
        _readyFallback?.Dispose();
        _current?.Dispose();
        _gate.Dispose();
    }
}

using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using O2C.Core;
using O2C.Core.Ipc;

namespace O2C.App;

/// <summary>
/// Подключение к службе по именованному каналу. Само переподключается, если служба перезапущена.
/// События вызываются из фонового потока.
/// </summary>
internal sealed partial class PipeClient : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private StreamWriter? _writer;
    private int _nextId;
    private Task? _loop;

    public event Action<bool>? ConnectionChanged;
    public event Action<ConnectionStatus>? StatusReceived;
    public event Action<string[]>? LogReceived;
    public event Action<Core.Exclusions.ExclusionReport>? ExclusionsReceived;

    public bool IsConnected => _writer is not null;

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    public async Task<IpcMessage> RequestAsync(IpcMessage request)
    {
        var writer = _writer ?? throw new InvalidOperationException("Служба OpenToConnect недоступна.");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await _writeLock.WaitAsync(_cts.Token);
            try
            {
                await writer.WriteLineAsync(IpcProtocol.Serialize(request with { Id = id }));
            }
            finally
            {
                _writeLock.Release();
            }
            return await tcs.Task.WaitAsync(RequestTimeout, _cts.Token);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Связь со службой OpenToConnect потеряна.", ex);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException("Служба OpenToConnect не ответила вовремя.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", IpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(TimeSpan.FromSeconds(2), ct);
                EnsureServerIsService(pipe);

                var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                using var reader = new StreamReader(pipe, utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                _writer = new StreamWriter(pipe, utf8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                ConnectionChanged?.Invoke(true);

                while (!ct.IsCancellationRequested)
                {
                    var line = await IpcProtocol.ReadFrameAsync(reader, ct);
                    if (line is null)
                        break;
                    if (line.Length > 0 && IpcProtocol.Deserialize(line) is { } message)
                        Dispatch(message);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                // Служба не запущена или канал оборвался — попробуем снова.
            }

            var wasConnected = _writer is not null;
            _writer = null;
            foreach (var pending in _pending.Values)
                pending.TrySetException(new IOException("Связь со службой потеряна."));
            if (wasConnected)
                ConnectionChanged?.Invoke(false);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Dispatch(IpcMessage message)
    {
        if (message.Id is { } id)
        {
            if (_pending.TryGetValue(id, out var tcs))
                tcs.TrySetResult(message);
            return;
        }

        switch (message.Event)
        {
            case IpcEvents.Status when message.Status is not null:
                StatusReceived?.Invoke(message.Status);
                break;
            case IpcEvents.Log when message.LogLines is not null:
                LogReceived?.Invoke(message.LogLines);
                break;
            case IpcEvents.Exclusions when message.ExclusionReport is not null:
                ExclusionsReceived?.Invoke(message.ExclusionReport);
                break;
        }
    }

    /// <summary>
    /// Пароль уходит в канал, поэтому проверяем, что на том конце служба, а не программа пользователя,
    /// занявшая имя канала раньше службы. Обе проверки работают без прав администратора:
    /// сервер канала — в сессии 0 (там работают только службы), владелец канала — SYSTEM или администраторы.
    /// </summary>
    private static void EnsureServerIsService(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerSessionId(pipe.SafePipeHandle.DangerousGetHandle(), out var session))
            throw new IOException($"Не удалось проверить канал службы (ошибка {Marshal.GetLastPInvokeError()}).");
        if (session != 0)
            throw new UnauthorizedAccessException("Канал O2C открыт не службой.");

        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (owner is not SecurityIdentifier sid ||
            !(sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
        {
            throw new UnauthorizedAccessException("Канал O2C принадлежит не службе.");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerSessionId(IntPtr pipe, out uint serverSessionId);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}

// Помощник настройки сети. openconnect запускает его на каждом этапе соединения
// (pre-init, connect, attempt-reconnect, reconnect, disconnect) с теми же переменными окружения,
// что и vpnc-script. Работает от SYSTEM (дочерний процесс службы), без окна.
using System.Collections;
using System.Globalization;
using System.Text.Json;
using O2C.Core;
using O2C.Core.NetConfig;
using O2C.Network;

var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
    env[(string)e.Key] = e.Value as string ?? "";

var plan = TunnelPlan.FromEnvironment(env);
using var log = new NetConfigLog(ProductPaths.NetConfigLogFile, plan.Reason);

try
{
    foreach (var warning in plan.Warnings)
        log.Write($"Предупреждение: {warning}");

    var configurator = new TunnelConfigurator(log.Write);
    switch (plan.Reason)
    {
        case "pre-init":
            break;

        case "connect":
        case "reconnect":
            SaveState(configurator.Connect(plan, LoadState()));
            log.Write($"{ProductPaths.ReadyMarker} сеть настроена.");
            break;

        case "attempt-reconnect":
            SaveState(configurator.RefreshBypass(plan, LoadState()));
            break;

        case "disconnect":
            configurator.Disconnect(plan, LoadState());
            File.Delete(ProductPaths.NetConfigStateFile);
            break;

        default:
            log.Write($"Неизвестный этап «{plan.Reason}» — пропущен.");
            break;
    }
    return 0;
}
catch (Exception ex)
{
    // openconnect не прерывает соединение по коду возврата — службе сообщает маркер в журнале.
    log.Write($"{ProductPaths.FatalMarker} не удалось настроить сеть ({plan.Reason}): {ex.Message}");
    return 1;
}

static TunnelState? LoadState()
{
    try
    {
        return File.Exists(ProductPaths.NetConfigStateFile)
            ? JsonSerializer.Deserialize<TunnelState>(File.ReadAllText(ProductPaths.NetConfigStateFile))
            : null;
    }
    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
    {
        return null;
    }
}

static void SaveState(TunnelState state)
{
    var tmp = ProductPaths.NetConfigStateFile + ".tmp";
    File.WriteAllText(tmp, JsonSerializer.Serialize(state));
    File.Move(tmp, ProductPaths.NetConfigStateFile, overwrite: true);
}

/// <summary>Журнал помощника: дописывается в файл, служба читает его и показывает в окне.</summary>
internal sealed class NetConfigLog : IDisposable
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly StreamWriter? _writer;
    private readonly string _reason;

    public NetConfigLog(string path, string reason)
    {
        _reason = reason;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (new FileInfo(path) is { Exists: true, Length: > MaxBytes })
                File.Move(path, path + ".1", overwrite: true);
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Без журнала тоже работаем: настройка сети важнее.
        }
    }

    public void Write(string message) =>
        _writer?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{_reason}] {message}"));

    public void Dispose() => _writer?.Dispose();
}

namespace O2C.Core;

/// <summary>Общие пути данных — их используют и служба, и помощник настройки сети.</summary>
public static class ProductPaths
{
    public const string ProductName = "OpenToConnect";

    /// <summary>Помощник настройки сети, который openconnect запускает вместо vpnc-script.</summary>
    public const string NetConfigExeName = "OpenToConnect.NetConfig.exe";

    /// <summary>C:\ProgramData\OpenToConnect — доступ только SYSTEM и администраторам.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName);

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Журнал помощника настройки сети. Служба читает его и показывает в окне.</summary>
    public static string NetConfigLogFile => Path.Combine(LogDirectory, "netcfg.log");

    /// <summary>Что помощник поставил вне адаптера (маршрут до сервера и т.п.) — для снятия при отключении.</summary>
    public static string NetConfigStateFile => Path.Combine(DataDirectory, "netcfg-state.json");

    /// <summary>Строка журнала помощника с этим маркером означает, что сеть настроить не удалось.</summary>
    public const string FatalMarker = "FATAL:";

    /// <summary>Строка журнала помощника с этим маркером означает, что сеть туннеля настроена.</summary>
    public const string ReadyMarker = "READY:";
}

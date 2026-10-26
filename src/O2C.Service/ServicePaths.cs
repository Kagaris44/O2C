using System.Security.AccessControl;
using System.Security.Principal;
using O2C.Core;

namespace O2C.Service;

internal static class ServicePaths
{
    /// <summary>Имя службы Windows. Должно совпадать с ServiceInstall в installer/Product.wxs.</summary>
    public const string ServiceName = "O2C";

    public const string ProductName = ProductPaths.ProductName;

    /// <summary>C:\ProgramData\OpenToConnect — доступ только SYSTEM и администраторам.</summary>
    public static string DataDirectory => ProductPaths.DataDirectory;

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Маршруты-исключения, поставленные службой, — чтобы снять их после аварийного завершения.</summary>
    public static string RoutesStateFile => Path.Combine(DataDirectory, "routes.json");

    /// <summary>Адреса, к которым обращались программы из списка исключений (хранятся 7 дней).</summary>
    public static string AppAddressesFile => Path.Combine(DataDirectory, "app-addresses.json");

    /// <summary>Скачанный список российских подсетей (запись «@ru»).</summary>
    public static string RussianNetworksFile => Path.Combine(DataDirectory, "ru-networks.txt");

    /// <summary>Российские подсети, к которым обращался компьютер (хранятся 7 дней).</summary>
    public static string RussianSeenFile => Path.Combine(DataDirectory, "ru-networks-seen.json");
    public static string LogDirectory => ProductPaths.LogDirectory;
    public static string ServiceLogFile => Path.Combine(LogDirectory, "service.log");
    public static string OpenConnectLogFile => Path.Combine(LogDirectory, "openconnect.log");

    /// <summary>openconnect.exe, его DLL и wintun.dll лежат в подпапке openconnect рядом со службой.</summary>
    public static string OpenConnectDirectory => Path.Combine(AppContext.BaseDirectory, "openconnect");
    public static string OpenConnectExe => Path.Combine(OpenConnectDirectory, "openconnect.exe");

    /// <summary>Помощник настройки сети, который openconnect запускает вместо vpnc-script.</summary>
    public static string ConnectScript => Path.Combine(AppContext.BaseDirectory, ProductPaths.NetConfigExeName);

    public static void EnsureDataDirectory()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        var dir = new DirectoryInfo(DataDirectory);
        if (!dir.Exists)
            dir.Create(security);
        else
            dir.SetAccessControl(security);

        Directory.CreateDirectory(LogDirectory);
    }
}

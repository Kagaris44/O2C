namespace O2C.Core.OpenConnect;

public static class OpenConnectCommand
{
    /// <summary>Имя сетевого адаптера Wintun, который создаёт openconnect.</summary>
    public const string InterfaceName = "O2C";

    /// <summary>
    /// Аргументы openconnect.exe. Пароль в аргументы не попадает — он передаётся через stdin
    /// (--passwd-on-stdin), поэтому не виден в списке процессов.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(ConnectionSettings settings, string scriptPath)
    {
        var error = settings.Validate();
        if (error is not null)
            throw new ArgumentException(error, nameof(settings));

        var args = new List<string>
        {
            $"--protocol={settings.Protocol}",
            $"--user={settings.Username}",
            "--passwd-on-stdin",
            "--non-inter",
            $"--interface={InterfaceName}",
            $"--script={scriptPath}",
        };

        if (!string.IsNullOrWhiteSpace(settings.AuthGroup))
            args.Add($"--authgroup={settings.AuthGroup}");
        if (!string.IsNullOrWhiteSpace(settings.ServerCertPin))
            args.Add($"--servercert={settings.ServerCertPin}");

        // "--" защищает от адреса, похожего на опцию.
        args.Add("--");
        args.Add(settings.Server);
        return args;
    }
}

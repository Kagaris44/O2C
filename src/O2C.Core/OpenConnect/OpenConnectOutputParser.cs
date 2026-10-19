using System.Text.RegularExpressions;

namespace O2C.Core.OpenConnect;

public enum FailureKind
{
    /// <summary>Причина не распознана — считаем временной, можно повторять.</summary>
    Unknown,

    /// <summary>Сеть недоступна, сервер не отвечает — можно повторять.</summary>
    Network,

    /// <summary>Неверный логин/пароль или сервер требует ввод данных — повторять нельзя (блокировка учётки).</summary>
    Authentication,

    /// <summary>Недоверенный сертификат сервера — повторять бессмысленно.</summary>
    Certificate,

    /// <summary>Сервер завершил сессию.</summary>
    SessionTerminated,

    /// <summary>Не удалось поднять сетевой адаптер на этом компьютере — временно, можно повторять.</summary>
    TunnelSetup,
}

/// <summary>
/// Разбирает вывод одного запуска openconnect.exe. Строки сверены с исходниками openconnect 9.21
/// (main.c, library.c). Состояние — на один процесс: для нового запуска создаётся новый экземпляр.
/// </summary>
public sealed partial class OpenConnectOutputParser
{
    // main.c print_connection_info(): "Configured as 10.0.0.2, with SSL + LZ4 connected and DTLS ..."
    // Выводится после того, как туннель поднят и скрипт настройки сети отработал.
    [GeneratedRegex(@"^Configured as (?<addr>.*?), with ", RegexOptions.CultureInvariant)]
    private static partial Regex ConfiguredRegex();

    // "To trust this server in future, perhaps add this to your command line:\n    --servercert pin-sha256:..."
    [GeneratedRegex(@"--servercert[ =](?<pin>pin-sha256:[A-Za-z0-9+/=]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ServerCertRegex();

    private static readonly string[] CertificateMarkers =
    [
        "Server certificate verify failed",
        "certificate does not match hostname",
        "failed verification",
    ];

    private static readonly string[] NetworkMarkers =
    [
        "Failed to connect to host",
        "Failed to connect to",
        "getaddrinfo failed",
        "Failed to open HTTPS connection",
        "SSL connection failure",
        "Connection refused",
        "timed out",
        "Network is unreachable",
        "No route to host",
        "Failed to reconnect to host",
    ];

    private static readonly string[] AuthenticationMarkers =
    [
        "Login failed",
        "Invalid username or password",
        "User input required in non-interactive mode",
        "Authentication failed",
    ];

    private bool _sawCertificateError;
    private bool _sawNetworkError;
    private bool _sawAuthError;
    private bool _sawAuthIncomplete;
    private bool _sawSessionTerminated;
    private bool _sawTunnelSetupFailure;

    public bool IsConnected { get; private set; }
    public string? AssignedAddress { get; private set; }
    public string? RejectedCertPin { get; private set; }

    /// <summary>Обрабатывает строку. Возвращает true, если строка означает «туннель поднят».</summary>
    public bool ProcessLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0)
            return false;

        var configured = ConfiguredRegex().Match(line);
        if (configured.Success)
        {
            IsConnected = true;
            AssignedAddress = configured.Groups["addr"].Value.Replace(" + ", " ", StringComparison.Ordinal).Trim();
            // После успешного подключения прошлые сбои (например, неудачный DTLS) уже не важны.
            _sawNetworkError = false;
            return true;
        }

        var pin = ServerCertRegex().Match(line);
        if (pin.Success)
        {
            RejectedCertPin = pin.Groups["pin"].Value;
            _sawCertificateError = true;
        }

        if (ContainsAny(line, CertificateMarkers))
            _sawCertificateError = true;
        // При закреплённом отпечатке openconnect всё равно пишет «verify failed», а затем подключается.
        if (line.StartsWith("Connected to HTTPS on", StringComparison.Ordinal))
        {
            _sawCertificateError = false;
            RejectedCertPin = null;
        }
        if (line.Contains("Set up tun device failed", StringComparison.Ordinal) ||
            line.Contains("Could not open Wintun adapter", StringComparison.Ordinal) ||
            line.Contains("Could not create Wintun adapter", StringComparison.Ordinal))
        {
            _sawTunnelSetupFailure = true;
        }
        if (ContainsAny(line, NetworkMarkers))
            _sawNetworkError = true;
        if (ContainsAny(line, AuthenticationMarkers))
            _sawAuthError = true;
        if (line.Contains("Failed to complete authentication", StringComparison.Ordinal))
            _sawAuthIncomplete = true;
        if (line.Contains("Session terminated by server", StringComparison.Ordinal) ||
            line.Contains("Cookie was rejected by server", StringComparison.Ordinal))
            _sawSessionTerminated = true;

        return false;
    }

    /// <summary>Классифицирует причину завершения процесса по накопленному выводу.</summary>
    public FailureKind ClassifyExit()
    {
        // Порядок важен: ошибка сертификата и сети тоже заканчиваются "Failed to complete authentication".
        if (_sawCertificateError)
            return FailureKind.Certificate;
        if (_sawAuthError)
            return FailureKind.Authentication;
        // После сбоя адаптера openconnect шлёт BYE, и сервер отвергает cookie — это следствие, не причина.
        if (_sawTunnelSetupFailure)
            return FailureKind.TunnelSetup;
        if (_sawNetworkError)
            return FailureKind.Network;
        if (_sawSessionTerminated)
            return FailureKind.SessionTerminated;
        if (_sawAuthIncomplete)
            return FailureKind.Authentication;
        return FailureKind.Unknown;
    }

    private static bool ContainsAny(string line, string[] markers)
    {
        foreach (var m in markers)
        {
            if (line.Contains(m, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

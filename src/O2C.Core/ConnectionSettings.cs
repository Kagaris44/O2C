namespace O2C.Core;

/// <summary>Настройки подключения. Пароль здесь не хранится — см. <see cref="SettingsUpdate"/>.</summary>
public sealed record ConnectionSettings
{
    /// <summary>Адрес сервера: IP, имя или URL (https://host/group).</summary>
    public string Server { get; init; } = "";

    /// <summary>Протокол openconnect: anyconnect, nc, gp, pulse, f5, fortinet, array.</summary>
    public string Protocol { get; init; } = "anyconnect";

    public string Username { get; init; } = "";

    /// <summary>Группа авторизации (--authgroup), если сервер её спрашивает.</summary>
    public string? AuthGroup { get; init; }

    /// <summary>Отпечаток сертификата сервера (--servercert pin-sha256:...), для серверов с самоподписанным сертификатом.</summary>
    public string? ServerCertPin { get; init; }

    public bool AutoReconnect { get; init; } = true;

    /// <summary>
    /// true — DNS-серверы, присланные сервером (запросы идут через туннель);
    /// false (по умолчанию) — DNS этого компьютера, как без подключения.
    /// </summary>
    public bool UseServerDns { get; init; }

    public static readonly string[] KnownProtocols = ["anyconnect", "nc", "gp", "pulse", "f5", "fortinet", "array"];

    /// <summary>Возвращает текст ошибки или null, если настройки пригодны для подключения.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Server))
            return "Не указан сервер.";
        if (Server.Any(char.IsWhiteSpace) || Server.StartsWith('-'))
            return "Некорректный адрес сервера.";
        if (!KnownProtocols.Contains(Protocol))
            return $"Неизвестный протокол: {Protocol}.";
        if (string.IsNullOrWhiteSpace(Username))
            return "Не указан логин.";
        if (Username.Contains('\n') || Username.Contains('\r'))
            return "Некорректный логин.";
        if (ServerCertPin is { Length: > 0 } pin && !pin.StartsWith("pin-sha256:", StringComparison.Ordinal) && !pin.StartsWith("sha1:", StringComparison.Ordinal) && !pin.StartsWith("sha256:", StringComparison.Ordinal))
            return "Отпечаток сертификата должен начинаться с pin-sha256:";
        return null;
    }
}

/// <summary>Изменение настроек из GUI. <see cref="NewPassword"/> = null — пароль не меняется.</summary>
public sealed record SettingsUpdate
{
    public required ConnectionSettings Settings { get; init; }
    public string? NewPassword { get; init; }
}

/// <summary>Настройки для показа в GUI: вместо пароля — признак его наличия.</summary>
public sealed record SettingsView
{
    public required ConnectionSettings Settings { get; init; }
    public bool HasPassword { get; init; }
}

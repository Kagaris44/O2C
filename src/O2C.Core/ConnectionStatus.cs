namespace O2C.Core;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Disconnecting,
    Error,
}

public sealed record ConnectionStatus
{
    public ConnectionState State { get; init; } = ConnectionState.Disconnected;

    /// <summary>Пояснение для пользователя (причина ошибки, через сколько повтор и т.п.).</summary>
    public string? Message { get; init; }

    /// <summary>Адрес, выданный сервером.</summary>
    public string? AssignedIp { get; init; }

    public DateTimeOffset? ConnectedSince { get; init; }

    /// <summary>Отпечаток сертификата, если сервер отклонён из-за недоверенного сертификата.</summary>
    public string? RejectedCertPin { get; init; }

    public static ConnectionStatus Disconnected { get; } = new();
}

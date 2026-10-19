using System.Text.Json;
using System.Text.Json.Serialization;

namespace O2C.Core.Ipc;

public static class IpcCommands
{
    public const string GetStatus = "getStatus";
    public const string Connect = "connect";
    public const string Disconnect = "disconnect";
    public const string GetSettings = "getSettings";
    public const string SaveSettings = "saveSettings";
    public const string GetExclusions = "getExclusions";
    public const string SaveExclusions = "saveExclusions";
}

public static class IpcEvents
{
    public const string Status = "status";
    public const string Log = "log";
    public const string Exclusions = "exclusions";
}

/// <summary>
/// Единый конверт протокола между GUI и службой. Кадр — одна строка JSON (UTF-8, '\n').
/// Запрос: Id + Command. Ответ: Id + Ok/Error. Событие службы: Event.
/// </summary>
public sealed record IpcMessage
{
    public int? Id { get; init; }
    public string? Command { get; init; }
    public string? Event { get; init; }
    public bool? Ok { get; init; }
    public string? Error { get; init; }
    public ConnectionStatus? Status { get; init; }
    public SettingsUpdate? SettingsUpdate { get; init; }
    public SettingsView? Settings { get; init; }
    public string[]? LogLines { get; init; }

    /// <summary>Текст списка исключений — как его ввёл пользователь, с комментариями.</summary>
    public string? ExclusionsText { get; init; }

    public Exclusions.ExclusionReport? ExclusionReport { get; init; }
}

public static class IpcProtocol
{
    public const string PipeName = "O2C.Control";

    /// <summary>Ограничение на размер кадра, чтобы клиент не мог исчерпать память службы.</summary>
    public const int MaxFrameChars = 256 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(IpcMessage message) => JsonSerializer.Serialize(message, JsonOptions);

    public static IpcMessage? Deserialize(string line) => JsonSerializer.Deserialize<IpcMessage>(line, JsonOptions);

    /// <summary>Читает одну строку, ограничивая длину. null — конец потока.</summary>
    public static async Task<string?> ReadFrameAsync(TextReader reader, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        var buf = new char[1];
        while (true)
        {
            int n = await reader.ReadAsync(buf.AsMemory(), ct);
            if (n == 0)
                return sb.Length == 0 ? null : sb.ToString();
            char c = buf[0];
            if (c == '\n')
                return sb.ToString();
            if (c != '\r')
                sb.Append(c);
            if (sb.Length > MaxFrameChars)
                throw new InvalidDataException("IPC frame too large");
        }
    }
}

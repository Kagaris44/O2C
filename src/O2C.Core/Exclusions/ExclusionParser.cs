using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace O2C.Core.Exclusions;

public enum ExclusionKind
{
    /// <summary>Адрес или подсеть.</summary>
    Network,

    /// <summary>Доменное имя: адреса определяются через DNS и обновляются периодически.</summary>
    Host,

    /// <summary>Маска *.example.com — сам домен и все поддомены; адреса берутся из журнала DNS-клиента.</summary>
    Wildcard,

    /// <summary>
    /// Программа (путь к .exe) или папка с программами: адреса, к которым они обращаются,
    /// служба узнаёт из событий ядра Windows и запоминает на 7 дней.
    /// </summary>
    Application,

    /// <summary>
    /// Российские сети («@ru»): подсети, зарегистрированные в России (по данным RIPE NCC), и домены .ru, .su, .рф.
    /// Маршруты ставятся по требованию — только на подсети, к которым компьютер действительно обращается.
    /// </summary>
    RussianNetworks,
}

public sealed record ExclusionEntry
{
    /// <summary>Как запись выглядит после нормализации (для показа и сравнения).</summary>
    public required string Text { get; init; }
    public required ExclusionKind Kind { get; init; }
    public IPAddress? Address { get; init; }
    public int PrefixLength { get; init; }
    public string? Host { get; init; }

    /// <summary>Для <see cref="ExclusionKind.Application"/>: полный путь к .exe или к папке (с завершающим «\»).</summary>
    public string? AppPath { get; init; }

    public bool IsFolder => Kind == ExclusionKind.Application && AppPath is { } p && p.EndsWith('\\');
}

public sealed record ExclusionError(int Line, string Text, string Message);

public sealed record ExclusionParseResult(IReadOnlyList<ExclusionEntry> Entries, IReadOnlyList<ExclusionError> Errors);

/// <summary>
/// Разбирает список исключений: по одной записи на строку; «#» в начале строки или после пробела — комментарий.
/// Запись — IPv4/IPv6-адрес, подсеть в формате адрес/длина, доменное имя (можно вставить URL),
/// маска *.example.com, путь к программе (C:\...\app.exe) или к папке с программами (C:\...\),
/// набор «@ru» — российские сети.
/// </summary>
public static partial class ExclusionParser
{
    public const int MaxEntries = 500;

    /// <summary>Запись «Российские сети».</summary>
    public const string RussianNetworksEntry = "@ru";

    [GeneratedRegex(@"^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)*[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostRegex();

    public static ExclusionParseResult Parse(string? text)
    {
        var entries = new List<ExclusionEntry>();
        var errors = new List<ExclusionError>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var lines = (text ?? "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var value = StripComment(lines[i]).Trim();
            if (value.Length == 0)
                continue;

            var (entry, error) = ParseOne(value);
            if (error is not null)
            {
                errors.Add(new ExclusionError(i + 1, value, error));
                continue;
            }
            if (!seen.Add(entry!.Text))
                continue;
            if (entries.Count >= MaxEntries)
            {
                errors.Add(new ExclusionError(i + 1, value, $"Слишком много записей (максимум {MaxEntries})."));
                break;
            }
            entries.Add(entry);
        }

        return new ExclusionParseResult(entries, errors);
    }

    /// <summary>
    /// Убирает комментарий: «#» в начале строки или после пробела. «#» внутри пути (C:\Games\C#\) остаётся.
    /// </summary>
    public static string StripComment(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                return line[..i];
        }
        return line;
    }

    /// <summary>Похоже ли значение на путь Windows (C:\..., «C:\...» в кавычках).</summary>
    public static bool LooksLikePath(string value)
    {
        value = value.Trim().Trim('"');
        return value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/';
    }

    private static (ExclusionEntry? Entry, string? Error) ParseApplication(string value)
    {
        value = value.Trim().Trim('"').Replace('/', '\\');
        if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || value.Contains('*', StringComparison.Ordinal) || value.Contains('?', StringComparison.Ordinal))
            return (null, "Некорректный путь.");
        string full;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, "Некорректный путь.");
        }

        if (full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return (new ExclusionEntry { Kind = ExclusionKind.Application, AppPath = full, Text = full }, null);

        var folder = full.TrimEnd('\\') + "\\";
        if (folder.Length <= 3)
            return (null, "Укажите папку с программой, а не весь диск.");
        return (new ExclusionEntry { Kind = ExclusionKind.Application, AppPath = folder, Text = folder }, null);
    }

    private static (ExclusionEntry? Entry, string? Error) ParseOne(string value)
    {
        if (LooksLikePath(value))
            return ParseApplication(value);

        if (value.StartsWith('@'))
        {
            return string.Equals(value, RussianNetworksEntry, StringComparison.OrdinalIgnoreCase)
                ? (new ExclusionEntry { Kind = ExclusionKind.RussianNetworks, Text = RussianNetworksEntry }, null)
                : (null, "Неизвестный набор. Доступен @ru — российские сети.");
        }

        if (value.StartsWith("*.", StringComparison.Ordinal))
        {
            var domain = value[2..].TrimEnd('.').ToLowerInvariant();
            if (domain.Contains('*', StringComparison.Ordinal) || !HostRegex().IsMatch(domain) || !domain.Contains('.', StringComparison.Ordinal))
                return (null, "Маска должна иметь вид *.example.com.");
            return (new ExclusionEntry { Kind = ExclusionKind.Wildcard, Host = domain, Text = "*." + domain }, null);
        }
        if (value.Contains('*', StringComparison.Ordinal))
            return (null, "Звёздочка допустима только в начале: *.example.com.");

        // Вставили ссылку — берём из неё имя хоста.
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
                return (null, "Не удалось разобрать адрес.");
            value = uri.Host.Trim('[', ']');
        }

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        var addressPart = slash >= 0 ? value[..slash] : value;

        if (IPAddress.TryParse(addressPart, out var address) &&
            address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 &&
            (addressPart.Contains('.', StringComparison.Ordinal) || addressPart.Contains(':', StringComparison.Ordinal)))
        {
            int maxLength = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            int length = maxLength;
            if (slash >= 0 && (!int.TryParse(value[(slash + 1)..], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out length) || length > maxLength))
            {
                return (null, $"Длина префикса должна быть от 1 до {maxLength}.");
            }
            if (length == 0)
                return (null, "Префикс /0 исключил бы из туннеля весь трафик.");

            var network = Mask(address, length);
            return (new ExclusionEntry
            {
                Kind = ExclusionKind.Network,
                Address = network,
                PrefixLength = length,
                Text = length == maxLength ? network.ToString() : $"{network}/{length}",
            }, null);
        }

        if (slash >= 0)
            return (null, "Некорректная подсеть.");

        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return (null, @"Укажите полный путь к программе (C:\...\app.exe) или выберите её кнопкой «Добавить программу».");

        var host = value.TrimEnd('.').ToLowerInvariant();
        if (!HostRegex().IsMatch(host))
            return (null, "Некорректное доменное имя или адрес.");

        return (new ExclusionEntry { Kind = ExclusionKind.Host, Host = host, Text = host }, null);
    }

    /// <summary>Обнуляет биты адреса за пределами префикса: 203.0.113.5/24 → 203.0.113.0.</summary>
    public static IPAddress Mask(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (int bit = prefixLength; bit < bytes.Length * 8; bit++)
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        return new IPAddress(bytes);
    }
}

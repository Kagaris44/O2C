using System.Net;
using System.Net.Sockets;

namespace O2C.Core.Exclusions;

/// <summary>Сопоставление имён из DNS с записями исключений и разбор результатов журнала DNS-клиента.</summary>
public static class DnsNames
{
    /// <summary>Приводит имя к виду для сравнения: нижний регистр, без завершающей точки.</summary>
    public static string Normalize(string name) => name.Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>
    /// Подходит ли имя к записи: для домена — точное совпадение, для маски *.example.com —
    /// сам example.com и любые его поддомены (но не notexample.com).
    /// </summary>
    public static bool Matches(ExclusionEntry entry, string name)
    {
        if (entry.Host is not { } host)
            return false;
        name = Normalize(name);
        return entry.Kind switch
        {
            ExclusionKind.Host => name == host,
            ExclusionKind.Wildcard => name == host || name.EndsWith("." + host, StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// Подходит ли программа к записи: для .exe — тот же путь, для папки — программа в ней или во вложенных папках.
    /// </summary>
    public static bool MatchesApp(ExclusionEntry entry, string appPath)
    {
        if (entry.Kind != ExclusionKind.Application || entry.AppPath is not { } path)
            return false;
        return entry.IsFolder
            ? appPath.StartsWith(path, StringComparison.OrdinalIgnoreCase)
            : string.Equals(appPath, path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Разбирает поле QueryResults журнала Microsoft-Windows-DNS-Client: записи через «;»,
    /// адреса IPv4 — в виде ::ffff:a.b.c.d, псевдонимы — «type:  5 alias.example.com».
    /// </summary>
    public static List<IPAddress> ParseQueryResults(string? results)
    {
        var list = new List<IPAddress>();
        foreach (var raw in (results ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IPAddress.TryParse(raw, out var address))
                continue;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) ||
                IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                continue;
            }
            if (!list.Contains(address))
                list.Add(address);
        }
        return list;
    }
}

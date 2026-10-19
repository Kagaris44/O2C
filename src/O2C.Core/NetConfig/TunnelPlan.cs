using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace O2C.Core.NetConfig;

/// <summary>Сеть: адрес + длина префикса.</summary>
public readonly record struct IpNetwork(IPAddress Address, int PrefixLength)
{
    public AddressFamily Family => Address.AddressFamily;
    public override string ToString() => $"{Address}/{PrefixLength}";
}

/// <summary>
/// Что нужно сделать с сетью по переменным окружения, которые openconnect передаёт скрипту
/// (те же, что получает vpnc-script). Чистые данные — без обращения к Windows.
/// </summary>
public sealed record TunnelPlan
{
    /// <summary>pre-init, connect, disconnect, reconnect, attempt-reconnect.</summary>
    public required string Reason { get; init; }

    public uint? TunnelIndex { get; init; }

    /// <summary>Адрес сервера: до него нужен маршрут в обход туннеля.</summary>
    public IPAddress? Gateway { get; init; }

    public IpNetwork? Address4 { get; init; }
    public IpNetwork? Address6 { get; init; }
    public int? Mtu { get; init; }

    /// <summary>Весь трафик в туннель (сервер не прислал split-include).</summary>
    public bool FullTunnel4 { get; init; }
    public bool FullTunnel6 { get; init; }

    /// <summary>Маршруты в туннель.</summary>
    public IReadOnlyList<IpNetwork> TunnelRoutes { get; init; } = [];

    /// <summary>Маршруты мимо туннеля, присланные сервером (split-exclude).</summary>
    public IReadOnlyList<IpNetwork> BypassRoutes { get; init; } = [];

    public IReadOnlyList<IPAddress> Dns4 { get; init; } = [];
    public IReadOnlyList<IPAddress> Dns6 { get; init; } = [];
    public IReadOnlyList<string> SearchDomains { get; init; } = [];

    /// <summary>Замечания разбора (некорректные значения), для журнала.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Использовать DNS сервера (иначе — DNS компьютера). Служба передаёт режим в переменной O2C_DNS_MODE.</summary>
    public bool UseServerDns { get; init; }

    /// <summary>Имя переменной окружения с режимом DNS: server или local.</summary>
    public const string DnsModeVariable = "O2C_DNS_MODE";

    // Полный туннель IPv4 — две половины адресного пространства: они уже маршрута по умолчанию
    // и потому побеждают его при любой метрике, не трогая сам маршрут по умолчанию.
    public static readonly IpNetwork[] FullTunnelRoutes4 =
    [
        new(IPAddress.Parse("0.0.0.0"), 1),
        new(IPAddress.Parse("128.0.0.0"), 1),
    ];

    // Полный туннель IPv6 — только глобальные адреса (как vpnc-script), без link-local и multicast.
    public static readonly IpNetwork[] FullTunnelRoutes6 = [new(IPAddress.Parse("2000::"), 3)];

    public static TunnelPlan FromEnvironment(IReadOnlyDictionary<string, string> env)
    {
        var warnings = new List<string>();
        string? Get(string name) => env.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        int? GetInt(string name) => int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

        IPAddress? ParseAddress(string? text, string what)
        {
            if (text is null)
                return null;
            if (IPAddress.TryParse(text, out var a))
                return a;
            warnings.Add($"Некорректный {what}: {text}");
            return null;
        }

        var reason = Get("reason") ?? "";

        // IPv4-адрес туннеля и длина префикса.
        IpNetwork? address4 = null;
        if (ParseAddress(Get("INTERNAL_IP4_ADDRESS"), "IPv4-адрес") is { AddressFamily: AddressFamily.InterNetwork } a4)
            address4 = new IpNetwork(a4, Math.Clamp(GetInt("INTERNAL_IP4_NETMASKLEN") ?? 32, 1, 32));

        // IPv6: INTERNAL_IP6_NETMASK приходит как «адрес/длина», INTERNAL_IP6_ADDRESS — просто адрес.
        IpNetwork? address6 = null;
        if (Get("INTERNAL_IP6_NETMASK") is { } n6 && TryParseNetwork(n6, out var net6) && net6.Family == AddressFamily.InterNetworkV6)
            address6 = net6;
        else if (ParseAddress(Get("INTERNAL_IP6_ADDRESS"), "IPv6-адрес") is { AddressFamily: AddressFamily.InterNetworkV6 } a6)
            address6 = new IpNetwork(a6, 128);

        var tunnelRoutes = new List<IpNetwork>();
        var bypassRoutes = new List<IpNetwork>();

        var inc4 = ReadSplit(env, "CISCO_SPLIT_INC", ipv6: false, warnings);
        var inc6 = ReadSplit(env, "CISCO_IPV6_SPLIT_INC", ipv6: true, warnings);
        bool full4 = address4 is not null && inc4.Count == 0;
        bool full6 = address6 is not null && inc6.Count == 0;
        tunnelRoutes.AddRange(full4 ? FullTunnelRoutes4 : inc4);
        tunnelRoutes.AddRange(full6 ? FullTunnelRoutes6 : inc6);

        bypassRoutes.AddRange(ReadSplit(env, "CISCO_SPLIT_EXC", ipv6: false, warnings));
        bypassRoutes.AddRange(ReadSplit(env, "CISCO_IPV6_SPLIT_EXC", ipv6: true, warnings));

        var dns4 = new List<IPAddress>();
        var dns6 = new List<IPAddress>();
        foreach (var text in (Get("INTERNAL_IP4_DNS") + " " + Get("INTERNAL_IP6_DNS")).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ParseAddress(text, "DNS-сервер") is { } dns)
                (dns.AddressFamily == AddressFamily.InterNetwork ? dns4 : dns6).Add(dns);
        }

        var domains = new List<string>();
        foreach (var d in (Get("CISCO_DEF_DOMAIN") ?? "").Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!domains.Contains(d, StringComparer.OrdinalIgnoreCase))
                domains.Add(d);
        }

        uint? tunIdx = uint.TryParse(Get("TUNIDX"), NumberStyles.None, CultureInfo.InvariantCulture, out var idx) ? idx : null;
        int? mtu = GetInt("INTERNAL_IP4_MTU") is > 0 and var m ? m : null;

        return new TunnelPlan
        {
            Reason = reason,
            TunnelIndex = tunIdx,
            Gateway = ParseAddress(Get("VPNGATEWAY"), "адрес сервера"),
            Address4 = address4,
            Address6 = address6,
            Mtu = mtu,
            FullTunnel4 = full4,
            FullTunnel6 = full6,
            TunnelRoutes = tunnelRoutes,
            BypassRoutes = bypassRoutes,
            Dns4 = dns4,
            Dns6 = dns6,
            SearchDomains = domains,
            Warnings = warnings,
            UseServerDns = string.Equals(Get(DnsModeVariable), "server", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>Читает CISCO_SPLIT_INC (число) и CISCO_SPLIT_INC_n_ADDR / _MASKLEN.</summary>
    private static List<IpNetwork> ReadSplit(IReadOnlyDictionary<string, string> env, string prefix, bool ipv6, List<string> warnings)
    {
        var result = new List<IpNetwork>();
        if (!env.TryGetValue(prefix, out var countText) ||
            !int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            return result;
        }

        for (int i = 0; i < count; i++)
        {
            env.TryGetValue($"{prefix}_{i}_ADDR", out var addr);
            env.TryGetValue($"{prefix}_{i}_MASKLEN", out var lenText);
            int maxLen = ipv6 ? 128 : 32;
            if (IPAddress.TryParse(addr, out var a) &&
                a.AddressFamily == (ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork) &&
                int.TryParse(lenText, NumberStyles.None, CultureInfo.InvariantCulture, out var len) && len <= maxLen)
            {
                result.Add(new IpNetwork(Exclusions.ExclusionParser.Mask(a, len), len));
            }
            else
            {
                warnings.Add($"Некорректный маршрут {prefix}_{i}: {addr}/{lenText}");
            }
        }
        return result;
    }

    public static bool TryParseNetwork(string text, out IpNetwork network)
    {
        network = default;
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || !IPAddress.TryParse(text[..slash], out var a) ||
            !int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var len) ||
            len > (a.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))
        {
            return false;
        }
        network = new IpNetwork(a, len);
        return true;
    }
}

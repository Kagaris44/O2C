using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace O2C.Network;

/// <summary>Сведения об интерфейсе, нужные для выбора шлюза.</summary>
public sealed record InterfaceInfo(string Name, NetworkInterfaceType Type, bool IsUp);

/// <summary>Шлюз, через который исключения идут в обход туннеля.</summary>
public sealed record PhysicalGateway(ulong InterfaceLuid, uint InterfaceIndex, IPAddress NextHop, string InterfaceName);

/// <summary>
/// Выбирает «физический» маршрут по умолчанию — тот, по которому пошёл бы трафик без туннеля.
/// Метрики недостаточно: другие туннели (в том числе сторонние клиенты) часто ставят себе
/// маршрут по умолчанию с меньшей метрикой. Поэтому сначала реальные адаптеры со шлюзом, потом метрика.
/// </summary>
public static class GatewaySelector
{
    /// <summary>IF_TYPE_PROP_VIRTUAL — так Windows помечает адаптеры Wintun и подобные.</summary>
    private const NetworkInterfaceType PropVirtual = (NetworkInterfaceType)53;

    public static PhysicalGateway? Find(
        IEnumerable<RouteEntry> routes,
        AddressFamily family,
        uint? tunnelIndex,
        Func<uint, InterfaceInfo?> getInterface,
        Func<ulong, uint?> getInterfaceMetric)
    {
        var best = routes
            .Where(r => r.IsDefault && r.Destination.AddressFamily == family && r.InterfaceIndex != tunnelIndex)
            .Select(r => (Route: r, Info: getInterface(r.InterfaceIndex)))
            .Where(x => x.Info is { IsUp: true })
            .OrderByDescending(x => !IsVirtual(x.Info!.Type))
            .ThenByDescending(x => x.Route.HasGateway)
            .ThenBy(x => (ulong)x.Route.Metric + (getInterfaceMetric(x.Route.InterfaceLuid) ?? 0))
            .ThenBy(x => x.Route.InterfaceIndex)
            .FirstOrDefault();

        return best.Route is null
            ? null
            : new PhysicalGateway(best.Route.InterfaceLuid, best.Route.InterfaceIndex, best.Route.NextHop, best.Info!.Name);
    }

    public static bool IsVirtual(NetworkInterfaceType type) =>
        type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
            or NetworkInterfaceType.Unknown or PropVirtual;

    /// <summary>Сведения об интерфейсах системы по индексу (IPv4 и IPv6 индексы совпадают).</summary>
    public static Dictionary<uint, InterfaceInfo> SnapshotInterfaces()
    {
        var map = new Dictionary<uint, InterfaceInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var info = new InterfaceInfo(nic.Name, nic.NetworkInterfaceType, nic.OperationalStatus == OperationalStatus.Up);
            var props = nic.GetIPProperties();
            if (TryIndex(() => props.GetIPv4Properties()?.Index) is { } i4)
                map.TryAdd((uint)i4, info);
            if (TryIndex(() => props.GetIPv6Properties()?.Index) is { } i6)
                map.TryAdd((uint)i6, info);
        }
        return map;
    }

    private static int? TryIndex(Func<int?> get)
    {
        try
        {
            return get();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}

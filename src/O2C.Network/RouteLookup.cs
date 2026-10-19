using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using O2C.Core.Exclusions;

namespace O2C.Network;

/// <summary>Вспомогательные вычисления по таблице маршрутов.</summary>
public static class RouteLookup
{
    /// <summary>
    /// Маршрут, по которому Windows отправит пакет на адрес: самый длинный подходящий префикс,
    /// при равенстве — меньшая метрика.
    /// </summary>
    public static RouteEntry? Best(IEnumerable<RouteEntry> routes, IPAddress address) =>
        routes
            .Where(r => r.Destination.AddressFamily == address.AddressFamily &&
                        ExclusionParser.Mask(address, r.PrefixLength).Equals(r.Destination))
            .OrderByDescending(r => r.PrefixLength)
            .ThenBy(r => r.Metric)
            .FirstOrDefault();

    /// <summary>DNS-серверы, настроенные на интерфейсе (то, что Windows спрашивает без туннеля).</summary>
    public static List<IPAddress> DnsServersOf(uint interfaceIndex)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var props = nic.GetIPProperties();
            int? index4 = null, index6 = null;
            try
            {
                index4 = props.GetIPv4Properties()?.Index;
            }
            catch (NetworkInformationException)
            {
            }
            try
            {
                index6 = props.GetIPv6Properties()?.Index;
            }
            catch (NetworkInformationException)
            {
            }
            if (index4 == interfaceIndex || index6 == interfaceIndex)
            {
                return [.. props.DnsAddresses.Where(a =>
                    a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 &&
                    !a.IsIPv6SiteLocal && !IPAddress.IsLoopback(a))];
            }
        }
        return [];
    }
}

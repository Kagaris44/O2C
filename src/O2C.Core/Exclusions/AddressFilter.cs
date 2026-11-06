using System.Net;
using System.Net.Sockets;

namespace O2C.Core.Exclusions;

/// <summary>
/// Какие адреса можно исключать автоматически (по соединениям программ и журналу DNS).
/// Локальные, частные, групповые и служебные адреса не исключаются: локальная сеть доступна и так,
/// а частный адрес может быть внутренним адресом за туннелем — маршрут мимо туннеля сделал бы его недоступным.
/// Записи, введённые явно (адрес или подсеть), этот фильтр не затрагивает.
/// </summary>
public static class AddressFilter
{
    private static readonly NetworkList Special = NetworkList.Parse("""
        0.0.0.0/8
        10.0.0.0/8
        100.64.0.0/10
        127.0.0.0/8
        169.254.0.0/16
        172.16.0.0/12
        192.0.0.0/24
        192.0.2.0/24
        192.168.0.0/16
        198.18.0.0/15
        198.51.100.0/24
        203.0.113.0/24
        224.0.0.0/4
        240.0.0.0/4
        ::/128
        ::1/128
        64:ff9b:1::/48
        100::/64
        2001:db8::/32
        fc00::/7
        fe80::/10
        ff00::/8
        """);

    /// <summary>true — обычный публичный адрес, на него можно поставить маршрут мимо туннеля.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            return false;
        return !Special.Contains(address);
    }
}

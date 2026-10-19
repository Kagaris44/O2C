using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using O2C.Network;

namespace O2C.Service.Tests;

public class GatewaySelectorTests
{
    private static RouteEntry Default(uint index, string nextHop, uint metric) =>
        new(1000 + index, index, IPAddress.Any, 0, IPAddress.Parse(nextHop), metric);

    // Типичная картина: сторонний туннель (тип 53) с маршрутом по умолчанию и метрикой 1+1,
    // Ethernet с двумя шлюзами и метрикой интерфейса 25.
    private static readonly Dictionary<uint, InterfaceInfo> Interfaces = new()
    {
        [13] = new("other-tunnel", (NetworkInterfaceType)53, true),
        [25] = new("Ethernet 3", NetworkInterfaceType.Ethernet, true),
        [40] = new("O2C", (NetworkInterfaceType)53, true),
        [50] = new("Wi-Fi", NetworkInterfaceType.Wireless80211, false),
    };

    private static readonly RouteEntry[] Routes =
    [
        Default(13, "0.0.0.0", 1),
        Default(25, "192.0.2.1", 0),
        Default(25, "192.0.2.254", 256),
        Default(40, "0.0.0.0", 0),
        Default(50, "10.0.0.1", 0),
        new(1025, 25, IPAddress.Parse("192.0.2.0"), 24, IPAddress.Any, 0),
    ];

    private static uint? Metric(ulong luid) => luid switch
    {
        1013 => 1,
        1025 => 25,
        _ => 5,
    };

    [Fact]
    public void Prefers_physical_adapter_over_other_tunnels_and_ignores_own_tunnel()
    {
        var gw = GatewaySelector.Find(Routes, AddressFamily.InterNetwork, tunnelIndex: 40, i => Interfaces.GetValueOrDefault(i), Metric);
        Assert.NotNull(gw);
        Assert.Equal(25u, gw.InterfaceIndex);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), gw.NextHop);
        Assert.Equal("Ethernet 3", gw.InterfaceName);
    }

    [Fact]
    public void Falls_back_to_virtual_adapter_when_nothing_else()
    {
        var routes = Routes.Where(r => r.InterfaceIndex is 13 or 40).ToArray();
        var gw = GatewaySelector.Find(routes, AddressFamily.InterNetwork, tunnelIndex: 40, i => Interfaces.GetValueOrDefault(i), Metric);
        Assert.Equal(13u, gw?.InterfaceIndex);
    }

    [Fact]
    public void Down_interfaces_and_other_family_are_ignored()
    {
        var routes = Routes.Where(r => r.InterfaceIndex is 50 or 40).ToArray();
        Assert.Null(GatewaySelector.Find(routes, AddressFamily.InterNetwork, 40, i => Interfaces.GetValueOrDefault(i), Metric));
        Assert.Null(GatewaySelector.Find(Routes, AddressFamily.InterNetworkV6, 40, i => Interfaces.GetValueOrDefault(i), Metric));
    }
}

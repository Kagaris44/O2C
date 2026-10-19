using System.Net;
using O2C.Core.NetConfig;

namespace O2C.Core.Tests;

public class TunnelPlanTests
{
    private static TunnelPlan Plan(params (string Key, string Value)[] vars) =>
        TunnelPlan.FromEnvironment(vars.ToDictionary(v => v.Key, v => v.Value));

    [Fact]
    public void Full_tunnel_when_server_sends_no_split_include()
    {
        var plan = Plan(
            ("reason", "connect"), ("TUNIDX", "42"), ("VPNGATEWAY", "203.0.113.10"),
            ("INTERNAL_IP4_ADDRESS", "10.10.0.5"), ("INTERNAL_IP4_NETMASKLEN", "24"), ("INTERNAL_IP4_MTU", "1406"),
            ("INTERNAL_IP4_DNS", "10.10.0.1 10.10.0.2"), ("CISCO_DEF_DOMAIN", "corp.example lab.example"));

        Assert.Equal("connect", plan.Reason);
        Assert.Equal(42u, plan.TunnelIndex);
        Assert.Equal(IPAddress.Parse("203.0.113.10"), plan.Gateway);
        Assert.Equal(new IpNetwork(IPAddress.Parse("10.10.0.5"), 24), plan.Address4);
        Assert.Equal(1406, plan.Mtu);
        Assert.True(plan.FullTunnel4);
        Assert.False(plan.FullTunnel6);
        Assert.Equal(TunnelPlan.FullTunnelRoutes4, plan.TunnelRoutes);
        Assert.Equal(["10.10.0.1", "10.10.0.2"], plan.Dns4.Select(a => a.ToString()));
        Assert.Equal(["corp.example", "lab.example"], plan.SearchDomains);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Split_include_and_exclude_from_server()
    {
        var plan = Plan(
            ("reason", "connect"), ("TUNIDX", "7"),
            ("INTERNAL_IP4_ADDRESS", "10.10.0.5"),
            ("CISCO_SPLIT_INC", "2"),
            ("CISCO_SPLIT_INC_0_ADDR", "10.0.0.0"), ("CISCO_SPLIT_INC_0_MASKLEN", "8"),
            ("CISCO_SPLIT_INC_1_ADDR", "172.16.5.0"), ("CISCO_SPLIT_INC_1_MASKLEN", "24"),
            ("CISCO_SPLIT_EXC", "1"),
            ("CISCO_SPLIT_EXC_0_ADDR", "10.99.0.0"), ("CISCO_SPLIT_EXC_0_MASKLEN", "16"));

        Assert.False(plan.FullTunnel4);
        Assert.Equal(["10.0.0.0/8", "172.16.5.0/24"], plan.TunnelRoutes.Select(r => r.ToString()));
        Assert.Equal(["10.99.0.0/16"], plan.BypassRoutes.Select(r => r.ToString()));
        // Без длины префикса адрес туннеля считается /32.
        Assert.Equal(32, plan.Address4!.Value.PrefixLength);
    }

    [Fact]
    public void Ipv6_address_from_netmask_and_full_tunnel_routes()
    {
        var plan = Plan(
            ("reason", "connect"), ("TUNIDX", "7"),
            ("INTERNAL_IP4_ADDRESS", "10.10.0.5"),
            ("INTERNAL_IP6_NETMASK", "fd00:1::5/64"), ("INTERNAL_IP6_DNS", "fd00:1::53"));

        Assert.Equal(new IpNetwork(IPAddress.Parse("fd00:1::5"), 64), plan.Address6);
        Assert.True(plan.FullTunnel6);
        Assert.Contains(TunnelPlan.FullTunnelRoutes6[0], plan.TunnelRoutes);
        Assert.Equal(["fd00:1::53"], plan.Dns6.Select(a => a.ToString()));
    }

    [Fact]
    public void Ipv6_split_include_disables_ipv6_full_tunnel()
    {
        var plan = Plan(
            ("reason", "connect"), ("INTERNAL_IP6_ADDRESS", "fd00:1::5"),
            ("CISCO_IPV6_SPLIT_INC", "1"),
            ("CISCO_IPV6_SPLIT_INC_0_ADDR", "fd00:2::"), ("CISCO_IPV6_SPLIT_INC_0_MASKLEN", "48"));

        Assert.False(plan.FullTunnel6);
        Assert.Equal(["fd00:2::/48"], plan.TunnelRoutes.Select(r => r.ToString()));
        Assert.Equal(128, plan.Address6!.Value.PrefixLength);
    }

    [Fact]
    public void Bad_values_become_warnings_not_crashes()
    {
        var plan = Plan(
            ("reason", "connect"), ("INTERNAL_IP4_ADDRESS", "not-an-ip"), ("VPNGATEWAY", "bad"),
            ("CISCO_SPLIT_INC", "1"), ("CISCO_SPLIT_INC_0_ADDR", "10.0.0.0"), ("CISCO_SPLIT_INC_0_MASKLEN", "99"));

        Assert.Null(plan.Address4);
        Assert.Null(plan.Gateway);
        Assert.Empty(plan.TunnelRoutes);
        Assert.Equal(3, plan.Warnings.Count);
    }

    [Fact]
    public void Disconnect_needs_no_addresses()
    {
        var plan = Plan(("reason", "disconnect"), ("TUNIDX", "7"));
        Assert.Equal("disconnect", plan.Reason);
        Assert.False(plan.FullTunnel4);
        Assert.Empty(plan.TunnelRoutes);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("local", false)]
    [InlineData("server", true)]
    [InlineData("SERVER", true)]
    public void Dns_mode_defaults_to_local(string? mode, bool useServer)
    {
        var vars = new List<(string, string)> { ("reason", "connect") };
        if (mode is not null)
            vars.Add((TunnelPlan.DnsModeVariable, mode));
        Assert.Equal(useServer, Plan([.. vars]).UseServerDns);
    }
}

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using O2C.Core.NetConfig;
using O2C.Network;
using Xunit.Abstractions;

namespace O2C.Service.Tests;

/// <summary>
/// Настройка сети на настоящем адаптере Wintun. Только split-режим и адреса из тестовых диапазонов:
/// полный туннель на CI-машине оборвал бы связь самого раннера.
/// Нужны права администратора и путь к wintun.dll в O2C_WINTUN_DLL (в CI задаётся автоматически).
/// </summary>
public class TunnelConfiguratorTests(ITestOutputHelper output)
{
    private static bool InCi => Environment.GetEnvironmentVariable("CI") == "true";

    private string? Prerequisites()
    {
        using var identity = WindowsIdentity.GetCurrent();
        bool admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var dll = Environment.GetEnvironmentVariable("O2C_WINTUN_DLL");
        if (admin && dll is not null && File.Exists(dll))
            return dll;

        Assert.False(InCi, "В CI тест адаптера должен идти с правами администратора и O2C_WINTUN_DLL.");
        output.WriteLine("Пропущено: нужны права администратора и O2C_WINTUN_DLL.");
        return null;
    }

    [Fact]
    public void Connect_and_disconnect_configure_real_adapter()
    {
        if (Prerequisites() is not { } dll)
            return;

        using var adapter = new WintunAdapter(dll, "O2CTest");
        var index = IpHelper.TryGetIndexByLuid(adapter.Luid) ?? throw new InvalidOperationException("no index");
        var log = new List<string>();
        var configurator = new TunnelConfigurator(m => { log.Add(m); output.WriteLine(m); });

        var env = new Dictionary<string, string>
        {
            ["reason"] = "connect",
            ["TUNIDX"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["VPNGATEWAY"] = "198.51.100.200",
            ["INTERNAL_IP4_ADDRESS"] = "10.250.0.2",
            ["INTERNAL_IP4_NETMASKLEN"] = "24",
            ["INTERNAL_IP4_MTU"] = "1400",
            ["INTERNAL_IP4_DNS"] = "10.250.0.53",
            ["CISCO_DEF_DOMAIN"] = "o2c-test.example",
            [TunnelPlan.DnsModeVariable] = "server",
            ["CISCO_SPLIT_INC"] = "1",
            ["CISCO_SPLIT_INC_0_ADDR"] = "203.0.113.0",
            ["CISCO_SPLIT_INC_0_MASKLEN"] = "24",
            ["CISCO_SPLIT_EXC"] = "1",
            ["CISCO_SPLIT_EXC_0_ADDR"] = "192.0.2.0",
            ["CISCO_SPLIT_EXC_0_MASKLEN"] = "24",
        };

        TunnelState? state = null;
        try
        {
            state = configurator.Connect(TunnelPlan.FromEnvironment(env), null);

            Assert.Contains(IpHelper.GetAddresses(), a =>
                a.InterfaceLuid == adapter.Luid && a.Address.Equals(IPAddress.Parse("10.250.0.2")) && a.PrefixLength == 24);
            var routes = IpHelper.GetRoutes();
            Assert.Contains(routes, r =>
                r.InterfaceLuid == adapter.Luid && r.Destination.Equals(IPAddress.Parse("203.0.113.0")) && r.PrefixLength == 24);
            // Split-режим: маршрутов полного туннеля быть не должно.
            Assert.DoesNotContain(routes, r => r.InterfaceLuid == adapter.Luid && r.PrefixLength == 1);
            // Маршрут до сервера и исключение сервера — через физический шлюз, не через туннель.
            Assert.Equal(2, state.BypassRoutes.Count);
            Assert.All(state.BypassRoutes, b => Assert.NotEqual(adapter.Luid, b.InterfaceLuid));
            Assert.Contains(routes, r => r.Destination.Equals(IPAddress.Parse("198.51.100.200")) && r.PrefixLength == 32);
            Assert.Contains(routes, r => r.Destination.Equals(IPAddress.Parse("192.0.2.0")) && r.PrefixLength == 24);

            var nic = NetworkInterface.GetAllNetworkInterfaces().Single(n => n.Name == "O2CTest");
            var props = nic.GetIPProperties();
            Assert.Contains(IPAddress.Parse("10.250.0.53"), props.DnsAddresses);
            Assert.Equal(1400, props.GetIPv4Properties().Mtu);

            env["reason"] = "disconnect";
            configurator.Disconnect(TunnelPlan.FromEnvironment(env), state);
            state = null;

            routes = IpHelper.GetRoutes();
            Assert.DoesNotContain(routes, r => r.Destination.Equals(IPAddress.Parse("198.51.100.200")) && r.PrefixLength == 32);
            Assert.DoesNotContain(routes, r => r.Destination.Equals(IPAddress.Parse("192.0.2.0")) && r.PrefixLength == 24);
            Assert.DoesNotContain(routes, r => r.InterfaceLuid == adapter.Luid && r.Destination.Equals(IPAddress.Parse("203.0.113.0")));
            Assert.DoesNotContain(IpHelper.GetAddresses(), a =>
                a.InterfaceLuid == adapter.Luid && a.Address.AddressFamily == AddressFamily.InterNetwork);
        }
        finally
        {
            // Если тест упал посередине — не оставляем маршрутов на машине.
            foreach (var b in state?.BypassRoutes ?? [])
                IpHelper.DeleteRoute(b.InterfaceLuid, IPAddress.Parse(b.Destination), b.PrefixLength, IPAddress.Parse(b.NextHop));
        }
    }

    [Fact]
    public void Reads_loopback_address()
    {
        Assert.Contains(IpHelper.GetAddresses(), a => a.Address.Equals(IPAddress.Loopback) && a.PrefixLength == 8);
    }

    [Fact]
    public void Local_dns_mode_leaves_tunnel_without_dns()
    {
        if (Prerequisites() is not { } dll)
            return;

        using var adapter = new WintunAdapter(dll, "O2CTestDns");
        var index = IpHelper.TryGetIndexByLuid(adapter.Luid) ?? throw new InvalidOperationException("no index");
        var configurator = new TunnelConfigurator(output.WriteLine);
        var env = new Dictionary<string, string>
        {
            ["reason"] = "connect",
            ["TUNIDX"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["INTERNAL_IP4_ADDRESS"] = "10.251.0.2",
            ["INTERNAL_IP4_NETMASKLEN"] = "24",
            ["INTERNAL_IP4_DNS"] = "10.251.0.53",
            ["CISCO_SPLIT_INC"] = "1",
            ["CISCO_SPLIT_INC_0_ADDR"] = "203.0.113.0",
            ["CISCO_SPLIT_INC_0_MASKLEN"] = "24",
            [TunnelPlan.DnsModeVariable] = "local",
        };

        var state = configurator.Connect(TunnelPlan.FromEnvironment(env), null);
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().Single(n => n.Name == "O2CTestDns");
            Assert.DoesNotContain(IPAddress.Parse("10.251.0.53"), nic.GetIPProperties().DnsAddresses);
            // DNS машины CI доступен напрямую (split-режим) — лишних маршрутов не нужно.
            Assert.Empty(state.BypassRoutes);
        }
        finally
        {
            env["reason"] = "disconnect";
            configurator.Disconnect(TunnelPlan.FromEnvironment(env), state);
        }
    }
}

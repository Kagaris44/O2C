using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using O2C.Network;
using Xunit.Abstractions;

namespace O2C.Service.Tests;

/// <summary>Проверки работы с настоящей таблицей маршрутов Windows.</summary>
public class IpHelperTests(ITestOutputHelper output)
{
    [Fact]
    public void SockAddr_round_trips_for_both_families()
    {
        Span<byte> buf = stackalloc byte[28];
        foreach (var text in new[] { "203.0.113.5", "2001:db8::1", "fe80::1%12" })
        {
            var address = IPAddress.Parse(text);
            IpHelper.WriteSockAddr(buf, address);
            Assert.Equal(address, IpHelper.ReadSockAddr(buf));
        }
    }

    [Fact]
    public void Reads_route_table_with_sane_values()
    {
        var routes = IpHelper.GetRoutes();
        Assert.NotEmpty(routes);
        // Петлевой маршрут есть всегда: 127.0.0.0/8.
        Assert.Contains(routes, r => r.Destination.Equals(IPAddress.Parse("127.0.0.0")) && r.PrefixLength == 8);
        Assert.All(routes, r => Assert.InRange(r.PrefixLength, 0, r.Destination.AddressFamily == AddressFamily.InterNetwork ? 32 : 128));
        foreach (var r in routes.Where(r => r.IsDefault))
            output.WriteLine(r.ToString());
    }

    [Fact]
    public void Reads_interface_metric()
    {
        var loopback = IpHelper.GetRoutes().First(r => r.Destination.Equals(IPAddress.Parse("127.0.0.0")) && r.PrefixLength == 8);
        var metric = IpHelper.GetInterfaceMetric(loopback.InterfaceLuid, AddressFamily.InterNetwork);
        Assert.NotNull(metric);
        // Метрика петли в Windows — 75; проверяем лишь правдоподобный диапазон, чтобы поймать неверное смещение.
        Assert.InRange(metric.Value, 1u, 10000u);
        output.WriteLine($"loopback metric {metric}");
    }

    [Fact]
    public void Finds_gateway_on_this_machine()
    {
        var interfaces = GatewaySelector.SnapshotInterfaces();
        var gw = GatewaySelector.Find(IpHelper.GetRoutes(), AddressFamily.InterNetwork, null,
            i => interfaces.GetValueOrDefault(i), l => IpHelper.GetInterfaceMetric(l, AddressFamily.InterNetwork));
        output.WriteLine(gw?.ToString() ?? "no IPv4 gateway");
        Assert.NotNull(gw);
    }

    /// <summary>
    /// Добавляет и удаляет настоящий маршрут к адресу из TEST-NET-2. Нужны права администратора:
    /// без них тест ничего не делает (на раннерах GitHub Actions права есть).
    /// </summary>
    [Fact]
    public void Adds_and_deletes_route_via_physical_gateway()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            // В CI права есть всегда — там тест обязан выполниться по-настоящему.
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true", "В CI тест маршрутов должен идти с правами администратора.");
            output.WriteLine("Пропущено: нужны права администратора.");
            return;
        }

        var interfaces = GatewaySelector.SnapshotInterfaces();
        var gw = GatewaySelector.Find(IpHelper.GetRoutes(), AddressFamily.InterNetwork, null,
            i => interfaces.GetValueOrDefault(i), l => IpHelper.GetInterfaceMetric(l, AddressFamily.InterNetwork));
        Assert.NotNull(gw);

        var target = IPAddress.Parse("198.51.100.77");
        bool Exists() => IpHelper.GetRoutes().Any(r => r.Destination.Equals(target) && r.PrefixLength == 32 && r.InterfaceLuid == gw.InterfaceLuid);

        IpHelper.DeleteRoute(gw.InterfaceLuid, target, 32, gw.NextHop);
        Assert.False(Exists());
        try
        {
            Assert.True(IpHelper.AddRoute(gw.InterfaceLuid, target, 32, gw.NextHop, 1));
            Assert.True(Exists());
            // Повторное добавление — не ошибка, но и не «наш» маршрут.
            Assert.False(IpHelper.AddRoute(gw.InterfaceLuid, target, 32, gw.NextHop, 1));
        }
        finally
        {
            IpHelper.DeleteRoute(gw.InterfaceLuid, target, 32, gw.NextHop);
        }
        Assert.False(Exists());
    }
}

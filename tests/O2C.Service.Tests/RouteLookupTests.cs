using System.Net;
using O2C.Network;

namespace O2C.Service.Tests;

public class RouteLookupTests
{
    private static RouteEntry R(ulong luid, string dest, int len, uint metric = 0) =>
        new(luid, (uint)luid, IPAddress.Parse(dest), len, IPAddress.Any, metric);

    // Полный туннель (luid 2: 0/1 + 128/1), физический адаптер (luid 1): маршрут по умолчанию,
    // своя подсеть и исключение сервера 10.20.0.0/16 — типичная офисная сеть.
    private static readonly RouteEntry[] Routes =
    [
        R(1, "0.0.0.0", 0, 25),
        R(1, "10.20.4.0", 22, 256),
        R(1, "10.20.0.0", 16, 1),
        R(2, "0.0.0.0", 1),
        R(2, "128.0.0.0", 1),
    ];

    [Theory]
    [InlineData("8.8.8.8", 2ul)]         // публичный DNS — ушёл бы в туннель
    [InlineData("10.20.0.10", 1ul)]     // локальный DNS — напрямую (исключение сервера)
    [InlineData("10.20.6.78", 1ul)] 
    [InlineData("10.20.4.45", 1ul)]     // своя подсеть
    public void Longest_prefix_wins(string address, ulong expectedLuid) =>
        Assert.Equal(expectedLuid, RouteLookup.Best(Routes, IPAddress.Parse(address))?.InterfaceLuid);

    [Fact]
    public void Equal_prefix_prefers_lower_metric()
    {
        RouteEntry[] routes = [R(1, "10.0.0.0", 8, 5), R(2, "10.0.0.0", 8, 1)];
        Assert.Equal(2ul, RouteLookup.Best(routes, IPAddress.Parse("10.1.2.3"))?.InterfaceLuid);
    }

    [Fact]
    public void No_match_for_other_family() =>
        Assert.Null(RouteLookup.Best(Routes, IPAddress.Parse("2001:db8::1")));
}

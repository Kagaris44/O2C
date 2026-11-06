using System.Net;
using O2C.Core.Exclusions;

namespace O2C.Core.Tests;

public class NetworkSetTests
{
    [Theory]
    [InlineData("@blizzard")]
    [InlineData("@Blizzard")]
    [InlineData("  @BLIZZARD  # игры")]
    public void Parses_blizzard_set(string text)
    {
        var entry = Assert.Single(ExclusionParser.Parse(text).Entries);
        Assert.Equal(ExclusionKind.NetworkSet, entry.Kind);
        Assert.Equal("@blizzard", entry.Text);
        Assert.Same(NetworkSets.Blizzard, entry.Set);
    }

    [Fact]
    public void Unknown_set_lists_available_sets()
    {
        var message = Assert.Single(ExclusionParser.Parse("@valve").Errors).Message;
        Assert.Contains("@ru", message, StringComparison.Ordinal);
        Assert.Contains("@blizzard", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_does_not_match_dns_names() =>
        Assert.False(DnsNames.Matches(Assert.Single(ExclusionParser.Parse("@blizzard").Entries), "battle.net"));

    [Fact]
    public void List_enumerates_all_networks()
    {
        var list = NetworkList.Parse("""
            # date: 20261106
            37.244.0.0/18
            24.105.0.0/18
            2a04:e800::/29
            """);
        Assert.Equal(
            [(IPAddress.Parse("24.105.0.0"), 18), (IPAddress.Parse("37.244.0.0"), 18), (IPAddress.Parse("2a04:e800::"), 29)],
            list.All().ToList());
    }
}

public class AddressFilterTests
{
    [Theory]
    [InlineData("37.244.28.180")]
    [InlineData("8.8.8.8")]
    [InlineData("100.128.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("2a04:e800::1")]
    [InlineData("::ffff:37.244.28.180")]
    public void Public_addresses_pass(string address) => Assert.True(AddressFilter.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("10.20.30.40")]          // частная сеть — может быть за туннелем
    [InlineData("172.16.5.1")]
    [InlineData("192.168.0.3")]          // локальная сеть
    [InlineData("100.64.0.1")]           // CGNAT
    [InlineData("127.0.0.1")]
    [InlineData("169.254.10.1")]
    [InlineData("224.0.0.251")]          // mDNS
    [InlineData("239.255.255.250")]      // SSDP
    [InlineData("255.255.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("ff02::fb")]
    [InlineData("::ffff:192.168.1.1")]
    public void Special_addresses_are_rejected(string address) => Assert.False(AddressFilter.IsPublic(IPAddress.Parse(address)));
}

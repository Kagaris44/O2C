using System.Net;
using O2C.Core.Exclusions;

namespace O2C.Core.Tests;

public class NetworkListTests
{
    private const string Sample = """
        # OpenToConnect: networks registered in Russia (RU)
        # date: 20261020
        5.8.0.0/16
        77.88.0.0/18
        87.250.224.0/19
        95.108.128.0/17
        203.0.113.7/32
        not a network
        2a02:6b8::/32
        """;

    private static readonly NetworkList List = NetworkList.Parse(Sample);

    [Fact]
    public void Parses_header_and_networks()
    {
        Assert.Equal("20261020", List.Date);
        Assert.Equal(5, List.Count4);
        Assert.Equal(1, List.Count6);
    }

    [Theory]
    [InlineData("87.250.250.242", "87.250.224.0", 19)]
    [InlineData("87.250.224.0", "87.250.224.0", 19)]
    [InlineData("87.250.255.255", "87.250.224.0", 19)]
    [InlineData("5.8.200.1", "5.8.0.0", 16)]
    [InlineData("203.0.113.7", "203.0.113.7", 32)]
    [InlineData("2a02:6b8:a::a", "2a02:6b8::", 32)]
    [InlineData("::ffff:77.88.55.88", "77.88.0.0", 18)]
    public void Finds_containing_network(string address, string network, int prefix)
    {
        var found = List.Find(IPAddress.Parse(address));
        Assert.NotNull(found);
        Assert.Equal(IPAddress.Parse(network), found.Value.Network);
        Assert.Equal(prefix, found.Value.Prefix);
    }

    [Theory]
    [InlineData("87.250.256.0")]
    [InlineData("87.251.0.1")]
    [InlineData("1.1.1.1")]
    [InlineData("203.0.113.8")]
    [InlineData("255.255.255.255")]
    [InlineData("0.0.0.1")]
    [InlineData("2a03::1")]
    public void Outside_addresses_are_not_found(string address)
    {
        if (IPAddress.TryParse(address, out var ip))
            Assert.Null(List.Find(ip));
    }

    [Fact]
    public void Empty_list_finds_nothing() => Assert.Null(NetworkList.Empty.Find(IPAddress.Parse("87.250.250.242")));
}

public class NetworkBookTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Learn_reports_new_networks_and_normalizes()
    {
        var book = new NetworkBook();
        Assert.True(book.Learn(IPAddress.Parse("87.250.250.242"), 19, T0));
        Assert.False(book.Learn(IPAddress.Parse("87.250.224.0"), 19, T0.AddMinutes(5)));
        Assert.Equal((IPAddress.Parse("87.250.224.0"), 19), Assert.Single(book.Get()));
    }

    [Fact]
    public void Networks_expire_after_ttl_unless_used()
    {
        var book = new NetworkBook();
        book.Learn(IPAddress.Parse("5.8.0.0"), 16, T0);
        book.Learn(IPAddress.Parse("77.88.0.0"), 18, T0);
        book.Learn(IPAddress.Parse("5.8.0.0"), 16, T0.AddDays(6));   // снова обращались
        book.Prune(T0.AddDays(8));
        Assert.Equal((IPAddress.Parse("5.8.0.0"), 16), Assert.Single(book.Get()));
    }

    [Fact]
    public void Survives_serialization()
    {
        var book = new NetworkBook();
        book.Learn(IPAddress.Parse("5.8.0.0"), 16, T0);
        book.Learn(IPAddress.Parse("2a02:6b8::"), 32, T0);
        var restored = NetworkBook.Deserialize(book.Serialize());
        Assert.Equal(2, restored.Count);
        restored.Prune(T0.AddDays(8));
        Assert.Equal(0, restored.Count);
    }

    [Fact]
    public void Cap_drops_oldest()
    {
        var book = new NetworkBook();
        for (int i = 0; i < NetworkBook.MaxNetworks + 3; i++)
            book.Learn(new IPAddress([10, (byte)(i / 256), (byte)(i % 256), 0]), 24, T0.AddSeconds(i));
        Assert.Equal(NetworkBook.MaxNetworks, book.Count);
        Assert.DoesNotContain((IPAddress.Parse("10.0.0.0"), 24), book.Get());
    }
}

public class RussianEntryTests
{
    [Theory]
    [InlineData("@ru")]
    [InlineData("@RU")]
    [InlineData("  @ru   # российские сети")]
    public void Parses_russian_networks_entry(string text)
    {
        var entry = Assert.Single(ExclusionParser.Parse(text).Entries);
        Assert.Equal(ExclusionKind.RussianNetworks, entry.Kind);
        Assert.Equal("@ru", entry.Text);
    }

    [Fact]
    public void Unknown_set_is_an_error() =>
        Assert.Contains("@ru", Assert.Single(ExclusionParser.Parse("@kz").Errors).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("yandex.ru", true)]
    [InlineData("mail.yandex.ru.", true)]
    [InlineData("example.su", true)]
    [InlineData("xn--80aswg.xn--p1ai", true)]
    [InlineData("сайт.рф", true)]
    [InlineData("ru", false)]
    [InlineData("example.com", false)]
    [InlineData("rutube.com", false)]
    [InlineData("example.ru.com", false)]
    public void Matches_russian_zones(string name, bool expected)
    {
        var entry = Assert.Single(ExclusionParser.Parse("@ru").Entries);
        Assert.Equal(expected, DnsNames.Matches(entry, name));
    }
}

using O2C.Core.Exclusions;

namespace O2C.Core.Tests;

public class ExclusionParserTests
{
    [Fact]
    public void Parses_addresses_networks_hosts_and_comments()
    {
        var result = ExclusionParser.Parse("""
            # офисные сервисы
            203.0.113.5
            198.51.100.7/24      # маска нормализуется
            Example.COM.
            2001:db8::1
            2001:db8:abcd::/48

            https://portal.example.org/login?x=1
            """);

        Assert.Empty(result.Errors);
        Assert.Equal(
            ["203.0.113.5", "198.51.100.0/24", "example.com", "2001:db8::1", "2001:db8:abcd::/48", "portal.example.org"],
            result.Entries.Select(e => e.Text));

        var net = result.Entries[1];
        Assert.Equal(ExclusionKind.Network, net.Kind);
        Assert.Equal(24, net.PrefixLength);
        Assert.Equal(32, result.Entries[0].PrefixLength);
        Assert.Equal(128, result.Entries[3].PrefixLength);
        Assert.Equal(ExclusionKind.Host, result.Entries[2].Kind);
    }

    [Theory]
    [InlineData("ex*ample.com", "Звёздочка")]
    [InlineData("*.com", "Маска")]
    [InlineData("0.0.0.0/0", "/0")]
    [InlineData("203.0.113.0/33", "префикса")]
    [InlineData("203.0.113.0/abc", "префикса")]
    [InlineData("bad_host!", "Некорректное")]
    [InlineData("example.com/24", "подсеть")]
    public void Invalid_lines_are_reported_with_line_number(string line, string messagePart)
    {
        var result = ExclusionParser.Parse("good.example.com\n" + line);
        Assert.Single(result.Entries);
        var error = Assert.Single(result.Errors);
        Assert.Equal(2, error.Line);
        Assert.Contains(messagePart, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicates_are_removed()
    {
        var result = ExclusionParser.Parse("example.com\nEXAMPLE.com\n10.0.0.1\n10.0.0.1/32");
        Assert.Equal(["example.com", "10.0.0.1"], result.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Plain_number_is_not_treated_as_address()
    {
        // IPAddress.TryParse("123") даёт 0.0.0.123 — такого «адреса» пользователь не имел в виду.
        var result = ExclusionParser.Parse("123");
        Assert.Equal(ExclusionKind.Host, Assert.Single(result.Entries).Kind);
    }

    [Fact]
    public void Entry_limit_is_enforced()
    {
        var text = string.Join('\n', Enumerable.Range(0, ExclusionParser.MaxEntries + 5).Select(i => $"10.{i / 256}.{i % 256}.1"));
        var result = ExclusionParser.Parse(text);
        Assert.Equal(ExclusionParser.MaxEntries, result.Entries.Count);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void Empty_text_gives_empty_result()
    {
        var result = ExclusionParser.Parse(null);
        Assert.Empty(result.Entries);
        Assert.Empty(result.Errors);
    }
}

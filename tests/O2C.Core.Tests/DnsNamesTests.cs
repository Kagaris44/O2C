using System.Net;
using O2C.Core.Exclusions;

namespace O2C.Core.Tests;

public class DnsNamesTests
{
    private static ExclusionEntry Entry(string text) => Assert.Single(ExclusionParser.Parse(text).Entries);

    [Fact]
    public void Wildcard_is_parsed_and_normalized()
    {
        var e = Entry("*.Example.COM.");
        Assert.Equal(ExclusionKind.Wildcard, e.Kind);
        Assert.Equal("example.com", e.Host);
        Assert.Equal("*.example.com", e.Text);
    }

    [Theory]
    [InlineData("*.example.com", "example.com", true)]
    [InlineData("*.example.com", "www.example.com", true)]
    [InlineData("*.example.com", "a.b.example.com.", true)]
    [InlineData("*.example.com", "WWW.EXAMPLE.COM", true)]
    [InlineData("*.example.com", "notexample.com", false)]
    [InlineData("*.example.com", "example.com.evil.net", false)]
    [InlineData("example.com", "example.com", true)]
    [InlineData("example.com", "www.example.com", false)]
    [InlineData("203.0.113.5", "203.0.113.5", false)]
    public void Matches_names(string entry, string name, bool expected) =>
        Assert.Equal(expected, DnsNames.Matches(Entry(entry), name));

    [Fact]
    public void Parses_query_results_with_cname_and_mapped_ipv4()
    {
        var addresses = DnsNames.ParseQueryResults(
            "type:  5 www.example.com-v4.edgesuite.net;::ffff:93.184.215.14;::ffff:93.184.215.15;2606:2800:21f:cb07:6820:80da:af6b:8b2c;");
        Assert.Equal(
            ["93.184.215.14", "93.184.215.15", "2606:2800:21f:cb07:6820:80da:af6b:8b2c"],
            addresses.Select(a => a.ToString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("type:  5 alias.example.com;")]
    [InlineData("::ffff:127.0.0.1;::1;0.0.0.0;")]
    public void Ignores_empty_aliases_and_loopback(string? results) =>
        Assert.Empty(DnsNames.ParseQueryResults(results));

    [Fact]
    public void Plain_ipv4_is_accepted_and_duplicates_removed()
    {
        var addresses = DnsNames.ParseQueryResults("203.0.113.7; ::ffff:203.0.113.7;");
        Assert.Equal([IPAddress.Parse("203.0.113.7")], addresses);
    }
}

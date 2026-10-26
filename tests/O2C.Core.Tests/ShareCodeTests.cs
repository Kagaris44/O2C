using O2C.Core.Exclusions;

namespace O2C.Core.Tests;

public class ShareCodeTests
{
    private static readonly string[] Sample = ["example.com", "*.site.ru", "10.0.0.0/8", "@ru", "C:" + (char)92 + "Games" + (char)92 + "Foo" + (char)92 + "foo.exe", "сайт.рф"];

    [Fact]
    public void Round_trip()
    {
        var code = ShareCode.Encode(Sample);
        Assert.StartsWith(ShareCode.Prefix, code, StringComparison.Ordinal);
        Assert.DoesNotContain('=', code);
        Assert.True(ShareCode.TryDecode(code, out var entries));
        Assert.Equal(Sample, entries);
    }

    [Theory]
    [InlineData("{0}")]
    [InlineData("  {0}  ")]
    [InlineData("https://kagaris44.github.io/O2C/add#{0}")]
    [InlineData("o2c://add/{0}")]
    [InlineData("o2c://add/{0}/")]
    [InlineData("Мой список: {0}. Добавь себе")]
    public void Finds_code_in_links_and_text(string template)
    {
        var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, ShareCode.Encode(Sample));
        Assert.True(ShareCode.TryDecode(text, out var entries));
        Assert.Equal(Sample.Length, entries.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("O2C1.")]
    [InlineData("O2C1.!!!!")]
    [InlineData("O2C1.AAAAAAAA")]
    [InlineData("O2C1.A")]
    public void Rejects_garbage(string? text) => Assert.False(ShareCode.TryDecode(text, out _));

    [Fact]
    public void Rejects_decompression_bombs()
    {
        var code = ShareCode.Encode([new string('a', ShareCode.MaxTextLength + 1)]);
        Assert.True(code.Length < 2000);
        Assert.False(ShareCode.TryDecode(code, out _));
    }

    [Fact]
    public void Typical_list_is_short()
    {
        var code = ShareCode.Encode(Enumerable.Range(0, 20).Select(i => $"service{i}.example.com"));
        Assert.True(code.Length < 200, code);
    }
}

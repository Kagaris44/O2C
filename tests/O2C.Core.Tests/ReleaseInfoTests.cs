using O2C.Core.Updates;

namespace O2C.Core.Tests;

public class ReleaseInfoTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789ABCDEF";

    private static string Json(string tag, string assetUrl, string? digest = Digest) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/Kagaris44/O2C/releases/tag/{{tag}}",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://github.com/Kagaris44/O2C/releases/download/{{tag}}/notes.txt" },
            { "name": "setup.msi", "browser_download_url": "{{assetUrl}}" {{(digest is null ? "" : $", \"digest\": \"{digest}\"")}} }
          ]
        }
        """;

    [Fact]
    public void Parses_release_with_msi_and_digest()
    {
        var info = ReleaseInfo.Parse(Json("v0.8.1", "https://github.com/Kagaris44/O2C/releases/download/v0.8.1/OpenToConnect-0.8.1.msi"));
        Assert.NotNull(info);
        Assert.Equal(new Version(0, 8, 1), info.Version);
        Assert.Equal("https://github.com/Kagaris44/O2C/releases/tag/v0.8.1", info.PageUrl);
        Assert.EndsWith("OpenToConnect-0.8.1.msi", info.MsiUrl, StringComparison.Ordinal);
        Assert.Equal(Digest[7..].ToLowerInvariant(), info.MsiSha256);
    }

    [Fact]
    public void Msi_from_another_host_is_ignored()
    {
        var info = ReleaseInfo.Parse(Json("v0.8.1", "https://example.com/OpenToConnect-0.8.1.msi"));
        Assert.NotNull(info);
        Assert.Null(info.MsiUrl);
    }

    [Fact]
    public void Missing_digest_is_allowed()
    {
        var info = ReleaseInfo.Parse(Json("v0.8.1", "https://github.com/Kagaris44/O2C/releases/download/v0.8.1/a.msi", digest: null));
        Assert.NotNull(info?.MsiUrl);
        Assert.Null(info.MsiSha256);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "tag_name": "v0.9.0-beta" }""")]
    [InlineData("""{ "message": "Not Found" }""")]
    public void Bad_responses_return_null(string json) => Assert.Null(ReleaseInfo.Parse(json));

    [Theory]
    [InlineData("0.8.1", "0.8.0.0", true)]
    [InlineData("0.8.0", "0.8.0.0", false)]
    [InlineData("0.7.9", "0.8.0.0", false)]
    [InlineData("1.0.0", "0.8.0.0", true)]
    public void Newer_comparison(string release, string current, bool expected)
    {
        Assert.True(ReleaseInfo.TryParseVersion(release, out var version));
        Assert.Equal(expected, new ReleaseInfo(version, "", null, null).IsNewerThan(Version.Parse(current)));
    }
}

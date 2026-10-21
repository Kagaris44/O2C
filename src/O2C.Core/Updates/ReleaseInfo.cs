using System.Text.Json;

namespace O2C.Core.Updates;

/// <summary>Опубликованная версия OpenToConnect: номер, страница релиза и установщик MSI.</summary>
public sealed record ReleaseInfo(Version Version, string PageUrl, string? MsiUrl, string? MsiSha256)
{
    /// <summary>Последний релиз в репозитории (черновики и предварительные версии GitHub сюда не отдаёт).</summary>
    public const string LatestUrl = "https://api.github.com/repos/Kagaris44/O2C/releases/latest";

    /// <summary>Откуда разрешено скачивать установщик.</summary>
    private const string DownloadPrefix = "https://github.com/Kagaris44/O2C/releases/download/";
    private const string PagePrefix = "https://github.com/Kagaris44/O2C/releases/";

    /// <summary>
    /// Разбирает ответ GitHub API (releases/latest). Возвращает null, если ответ не похож на релиз O2C.
    /// Ссылки принимаются только на этот репозиторий; SHA-256 берётся из поля digest установщика, если оно есть.
    /// </summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
                !TryParseVersion(tag.GetString()!, out var version))
            {
                return null;
            }

            var page = root.TryGetProperty("html_url", out var html) && html.ValueKind == JsonValueKind.String &&
                       html.GetString() is { } p && p.StartsWith(PagePrefix, StringComparison.Ordinal)
                ? p
                : PagePrefix + "latest";

            string? msiUrl = null, sha = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object ||
                        !asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String ||
                        url.GetString() is not { } u ||
                        !u.StartsWith(DownloadPrefix, StringComparison.Ordinal) ||
                        !u.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    msiUrl = u;
                    if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String &&
                        digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
                        d.Length == 7 + 64)
                    {
                        sha = d[7..].ToLowerInvariant();
                    }
                    break;
                }
            }
            return new ReleaseInfo(version, page, msiUrl, sha);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>«v0.8.1» или «0.8.1» → 0.8.1. Предварительные версии («0.9.0-beta») не принимаются.</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        if (Version.TryParse(text, out var parsed) && parsed.Build >= 0 && text.Count(c => c == '.') == 2)
        {
            version = parsed;
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>Новее ли релиз, чем установленная версия (сравниваются три числа: X.Y.Z).</summary>
    public bool IsNewerThan(Version current) =>
        Version > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));
}

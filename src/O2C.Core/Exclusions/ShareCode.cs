using System.IO.Compression;
using System.Text;

namespace O2C.Core.Exclusions;

/// <summary>
/// Код для обмена списком исключений: «O2C1.» + base64url от сжатого (raw deflate) текста,
/// записи по одной на строку. Код можно передать как есть, ссылкой на страницу
/// (https://kagaris44.github.io/O2C/add#код — часть после # на сервер не уходит)
/// или ссылкой o2c://add/код, которую открывает установленная программа.
/// </summary>
public static class ShareCode
{
    public const string Prefix = "O2C1.";
    public const string PageLink = "https://kagaris44.github.io/O2C/add#";
    public const string Scheme = "o2c";
    public const string SchemeLink = "o2c://add/";

    /// <summary>Ограничения при разборе чужого кода: размер распакованного текста и число записей.</summary>
    public const int MaxTextLength = 256 * 1024;
    public const int MaxEntries = 2000;

    public static string Encode(IEnumerable<string> entries)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', entries));
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(bytes);
        return Prefix + Convert.ToBase64String(buffer.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string ToPageLink(string code) => PageLink + code;

    /// <summary>
    /// Ищет код в тексте (сам код, ссылка на страницу или o2c://add/…) и возвращает записи.
    /// Записи не проверяются — это делает <see cref="ExclusionTextEditor.Add"/>.
    /// </summary>
    public static bool TryDecode(string? text, out IReadOnlyList<string> entries)
    {
        entries = [];
        if (string.IsNullOrEmpty(text))
            return false;
        int start = text.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0)
            return false;
        int end = start + Prefix.Length;
        while (end < text.Length && IsBase64Url(text[end]))
            end++;
        var payload = text[(start + Prefix.Length)..end];
        if (payload.Length == 0 || payload.Length % 4 == 1)
            return false;

        try
        {
            var base64 = payload.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            using var input = new MemoryStream(Convert.FromBase64String(base64));
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = deflate.Read(chunk)) > 0)
            {
                if (output.Length + read > MaxTextLength)
                    return false;
                output.Write(chunk, 0, read);
            }
            var lines = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(output.ToArray())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0 || lines.Length > MaxEntries)
                return false;
            entries = lines;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsBase64Url(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
}

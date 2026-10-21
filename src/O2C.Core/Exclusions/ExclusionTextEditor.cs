namespace O2C.Core.Exclusions;

public sealed record ExclusionEditResult(string Text, IReadOnlyList<string> Added, IReadOnlyList<ExclusionError> Errors, int Duplicates);

/// <summary>
/// Правка текста списка исключений по одной записи (для окна): добавить, удалить.
/// Служба хранит список текстом; строки-комментарии и порядок сохраняются.
/// </summary>
public static class ExclusionTextEditor
{
    private static readonly char[] Separators = ['\n', '\r', ',', ';', ' ', '\t'];

    /// <summary>
    /// Добавляет записи из <paramref name="input"/> — одну или несколько (вставка списком:
    /// через перевод строки, запятую, точку с запятой или пробел). Повторы пропускаются.
    /// </summary>
    public static ExclusionEditResult Add(string? text, string input)
    {
        var existing = ExclusionParser.Parse(text).Entries.Select(e => e.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        var errors = new List<ExclusionError>();
        int duplicates = 0;

        // Комментарий во вставленном тексте отбрасываем построчно, затем режем по разделителям.
        // Строку-путь (C:\Program Files\...) не режем: в путях бывают пробелы и запятые.
        var tokens = input.Split('\n')
            .Select(line => ExclusionParser.StripComment(line).Trim())
            .Where(line => line.Length > 0)
            .SelectMany(line => ExclusionParser.LooksLikePath(line)
                ? [line]
                : line.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var token in tokens)
        {
            var parsed = ExclusionParser.Parse(token);
            if (parsed.Errors.Count > 0)
            {
                errors.Add(parsed.Errors[0] with { Line = 0 });
                continue;
            }
            var entry = parsed.Entries[0];
            if (!existing.Add(entry.Text))
            {
                duplicates++;
                continue;
            }
            added.Add(entry.Text);
        }

        var result = (text ?? "").TrimEnd('\r', '\n', ' ');
        if (added.Count > 0)
            result = (result.Length > 0 ? result + "\n" : "") + string.Join('\n', added);
        return new ExclusionEditResult(result, added, errors, duplicates);
    }

    /// <summary>
    /// Удаляет запись: строки, которые после разбора дают ту же запись (или совпадают с ней
    /// дословно — для строк с ошибкой). Комментарии и остальные строки не трогаются.
    /// </summary>
    public static string Remove(string? text, string entry)
    {
        var target = NormalizedText(entry);
        var kept = new List<string>();
        foreach (var line in (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var value = ExclusionParser.StripComment(line).Trim();
            bool match = value.Length > 0 &&
                (string.Equals(value, entry.Trim(), StringComparison.OrdinalIgnoreCase) ||
                 (target is not null && string.Equals(NormalizedText(value), target, StringComparison.OrdinalIgnoreCase)));
            if (!match)
                kept.Add(line);
        }
        return string.Join('\n', kept).TrimEnd('\n');
    }

    private static string? NormalizedText(string value) =>
        ExclusionParser.Parse(value).Entries is { Count: > 0 } entries ? entries[0].Text : null;
}

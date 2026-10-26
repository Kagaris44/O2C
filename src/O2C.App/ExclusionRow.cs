using System.IO;
using System.Windows;
using System.Windows.Media;
using O2C.Core.Exclusions;

namespace O2C.App;

/// <summary>Строка списка исключений: запись, метка состояния справа, цветная точка и подсказка.</summary>
internal sealed record ExclusionRow(string Entry, string Tag, string Tooltip, Brush DotBrush, Brush TagBrush)
{
    private const char Sep = (char)92;

    private bool IsApp => ExclusionParser.LooksLikePath(Entry);
    private bool IsFolder => IsApp && Entry.EndsWith(Sep);
    private bool IsRussian => string.Equals(Entry, ExclusionParser.RussianNetworksEntry, StringComparison.OrdinalIgnoreCase);

    /// <summary>Для программ — имя файла или папки (полный путь в подсказке), для наборов — название, для остальных — запись как есть.</summary>
    public string Display => IsRussian ? "Российские сети"
        : !IsApp ? Entry
        : IsFolder ? Path.GetFileName(Entry.TrimEnd(Sep)) + Sep
        : Path.GetFileName(Entry);

    /// <summary>Значок Segoe Fluent Icons: глобус (E774), папка (E8B7) или программа (E737); у адресов значка нет.</summary>
    public string Icon => IsRussian ? char.ToString((char)0xE774)
        : !IsApp ? "" : IsFolder ? char.ToString((char)0xE8B7) : char.ToString((char)0xE737);

    public Visibility IconVisibility => IsApp || IsRussian ? Visibility.Visible : Visibility.Collapsed;

    public string FullTooltip => IsRussian
        ? "Подсети, зарегистрированные в России, и сайты .ru, .su, .рф — маршрут ставится, когда компьютер к ним обращается."
          + Environment.NewLine + Tooltip
        : IsApp ? Entry + Environment.NewLine + Tooltip : Tooltip;
}

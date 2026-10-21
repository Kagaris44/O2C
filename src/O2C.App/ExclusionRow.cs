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

    /// <summary>Для программ — имя файла или папки (полный путь в подсказке), для остальных — запись как есть.</summary>
    public string Display => !IsApp ? Entry
        : IsFolder ? Path.GetFileName(Entry.TrimEnd(Sep)) + Sep
        : Path.GetFileName(Entry);

    /// <summary>Значок Segoe Fluent Icons: папка (E8B7) или программа (E737); у адресов значка нет.</summary>
    public string Icon => !IsApp ? "" : IsFolder ? char.ToString((char)0xE8B7) : char.ToString((char)0xE737);

    public Visibility IconVisibility => IsApp ? Visibility.Visible : Visibility.Collapsed;

    public string FullTooltip => IsApp ? Entry + Environment.NewLine + Tooltip : Tooltip;
}

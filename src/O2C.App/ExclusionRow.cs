using System.Windows.Media;

namespace O2C.App;

/// <summary>Строка списка исключений: запись, метка состояния справа, цветная точка и подсказка.</summary>
internal sealed record ExclusionRow(string Entry, string Tag, string Tooltip, Brush DotBrush, Brush TagBrush);

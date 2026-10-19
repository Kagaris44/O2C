using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace O2C.App;

/// <summary>Пустая строка → Visible (для подсказки в пустом поле ввода), иначе Collapsed.</summary>
internal sealed class EmptyToVisibleConverter : IValueConverter
{
    public static EmptyToVisibleConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

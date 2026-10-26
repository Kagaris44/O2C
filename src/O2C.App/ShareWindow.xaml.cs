using System.Runtime.InteropServices;
using System.Windows;
using O2C.Core.Exclusions;

namespace O2C.App;

/// <summary>
/// «Поделиться списком»: предпросмотр записей, галочка для программ (по умолчанию выключена —
/// пути могут раскрыть имя пользователя), копирование ссылки на страницу или кода.
/// </summary>
public partial class ShareWindow : Window
{
    private readonly IReadOnlyList<ExclusionEntry> _entries;

    public ShareWindow(IReadOnlyList<ExclusionEntry> entries)
    {
        InitializeComponent();
        _entries = entries;
        int apps = entries.Count(e => e.Kind == ExclusionKind.Application);
        IncludeApps.Content = $"Включить программы и папки ({apps})";
        IncludeApps.Visibility = AppsWarning.Visibility = apps > 0 ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private List<string> Selected() =>
        [.. _entries.Where(e => IncludeApps.IsChecked == true || e.Kind != ExclusionKind.Application).Select(e => e.Text)];

    private void Refresh()
    {
        var selected = Selected();
        EntryList.ItemsSource = selected;
        CopyLinkButton.IsEnabled = CopyCodeButton.IsEnabled = selected.Count > 0;
        StatusText.Text = selected.Count == 0
            ? "Нечего отправить: в списке только программы."
            : $"Записей: {selected.Count}. Длина ссылки: {ShareCode.ToPageLink(ShareCode.Encode(selected)).Length} символов.";
    }

    private void OnIncludeAppsChanged(object sender, RoutedEventArgs e) => Refresh();

    private void OnCopyLinkClick(object sender, RoutedEventArgs e) =>
        Copy(ShareCode.ToPageLink(ShareCode.Encode(Selected())), "Ссылка скопирована.");

    private void OnCopyCodeClick(object sender, RoutedEventArgs e) =>
        Copy(ShareCode.Encode(Selected()), "Код скопирован.");

    private void Copy(string text, string done)
    {
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = done + " Получатель может открыть ссылку или вставить код кнопкой «Вставить списком».";
        }
        catch (COMException)
        {
            StatusText.Text = "Буфер обмена занят другой программой — попробуйте ещё раз.";
        }
    }
}

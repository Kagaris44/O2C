using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace O2C.App;

/// <summary>Выбор программы из запущенных: результат — путь к exe или к её папке.</summary>
public partial class RunningAppPicker : Window
{
    private IReadOnlyList<RunningApp> _all = [];

    public RunningAppPicker()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Выбранный путь: exe или папка с завершающим разделителем.</summary>
    public string? SelectedPath { get; private set; }

    private async Task LoadAsync()
    {
        StatusText.Text = "Загрузка списка…";
        _all = await Task.Run(RunningApps.Collect);
        ApplyFilter();
        FilterInput.Focus();
    }

    private void ApplyFilter()
    {
        var filter = FilterInput.Text.Trim();
        var items = filter.Length == 0 ? _all : [.. _all.Where(a =>
            a.Path.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
            a.Description.Contains(filter, StringComparison.CurrentCultureIgnoreCase))];
        AppList.ItemsSource = items;
        if (items.Count > 0)
            AppList.SelectedIndex = 0;
        StatusText.Text = _all.Count == 0
            ? "Не удалось получить список программ."
            : $"Программ: {items.Count}. Сначала — с открытыми окнами. Программы, запущенные от администратора, могут не отображаться.";
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        FilterHint.Visibility = FilterInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool any = AppList.SelectedItem is RunningApp;
        AddFileButton.IsEnabled = any;
        AddFolderButton.IsEnabled = any;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadAsync();

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => Accept(folder: false);

    private void OnAddFileClick(object sender, RoutedEventArgs e) => Accept(folder: false);

    private void OnAddFolderClick(object sender, RoutedEventArgs e) => Accept(folder: true);

    private void Accept(bool folder)
    {
        if (AppList.SelectedItem is not RunningApp app)
            return;
        SelectedPath = folder ? Path.GetDirectoryName(app.Path) + Path.DirectorySeparatorChar : app.Path;
        DialogResult = true;
    }
}

using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using O2C.Core;
using O2C.Core.Exclusions;
using O2C.Core.Ipc;

namespace O2C.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Клиент канала и значок освобождаются при выходе — жизненный цикл окна WPF.")]
public partial class MainWindow : Window
{
    private const int MaxLogChars = 200_000;

    private readonly MainViewModel _vm = new();
    private readonly PipeClient _client = new();
    private readonly TrayIcon _tray;
    private readonly UpdateChecker _updates = new();
    private readonly AppPreferences _prefs = AppPreferences.Load();
    private readonly DispatcherTimer _timer;
    private string? _newPassword;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _tray = new TrayIcon(ShowFromTray, () => _ = ToggleConnectionAsync(), () => _ = CheckUpdatesFromTrayAsync(), () => _ = ExitAsync());
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _vm.Tick(), Dispatcher);

        _client.ConnectionChanged += connected => Dispatcher.InvokeAsync(() => OnServiceConnectionChanged(connected));
        _client.StatusReceived += status => Dispatcher.InvokeAsync(() =>
        {
            _vm.Status = status;
            UpdateTray();
        });
        _client.LogReceived += lines => Dispatcher.InvokeAsync(() => AppendLog(lines));
        _client.ExclusionsReceived += report => Dispatcher.InvokeAsync(() => _vm.ApplyExclusionReport(report));
        _client.Start();
        UpdateTray();

        _vm.AutoUpdateCheck = _prefs.AutoUpdateCheck;
        _updates.AutoCheck = _prefs.AutoUpdateCheck;
        _updates.UpdateFound += release => Dispatcher.InvokeAsync(() =>
        {
            _vm.Update = release;
            _vm.UpdateStatus = $"Доступна версия {release.Version.ToString(3)}.";
        });
        _updates.Start();
    }

    // ---------- Обновление ----------

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();

    private async Task CheckUpdatesFromTrayAsync()
    {
        ShowFromTray();
        _vm.SettingsOpen = true;
        await CheckUpdatesAsync();
    }

    private async Task CheckUpdatesAsync()
    {
        if (_vm.CheckingUpdate)
            return;
        _vm.CheckingUpdate = true;
        _vm.UpdateStatus = "Проверка…";
        try
        {
            var (release, error) = await _updates.CheckAsync(CancellationToken.None);
            if (release is null)
            {
                _vm.UpdateStatus = $"Не удалось проверить: {error}";
            }
            else if (release.IsNewerThan(UpdateChecker.CurrentVersion))
            {
                _vm.Update = release;
                _vm.UpdateStatus = $"Доступна версия {release.Version.ToString(3)}.";
            }
            else
            {
                _vm.Update = null;
                _vm.UpdateStatus = "Установлена последняя версия.";
            }
        }
        finally
        {
            _vm.CheckingUpdate = false;
        }
    }

    private void OnAutoUpdateCheckClick(object sender, RoutedEventArgs e)
    {
        _prefs.AutoUpdateCheck = _vm.AutoUpdateCheck;
        _prefs.Save();
        _updates.AutoCheck = _vm.AutoUpdateCheck;
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Update is not { } release || _vm.UpdateProgress >= 0)
            return;

        var version = release.Version.ToString(3);
        if (release.MsiUrl is null)
        {
            if (MessageBox.Show(this, $"Доступна версия {version}. Открыть страницу загрузки?", "OpenToConnect",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                UpdateChecker.OpenPage(release);
            return;
        }

        var answer = MessageBox.Show(this,
            $"""
            Доступна версия {version} (установлена {UpdateChecker.CurrentVersion.ToString(3)}).

            «Да» — скачать и установить. Windows запросит права администратора; подключение на время установки прервётся.
            «Нет» — открыть страницу версии со списком изменений.
            """,
            "OpenToConnect", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
        if (answer == MessageBoxResult.No)
        {
            UpdateChecker.OpenPage(release);
            return;
        }
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            _vm.UpdateProgress = 0;
            await _updates.DownloadAndInstallAsync(release, new Progress<int>(p => _vm.UpdateProgress = p), CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TaskCanceledException)
        {
            MessageBox.Show(this, $"Не удалось обновить: {ex.Message}", "OpenToConnect", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _vm.UpdateProgress = -1;
        }
    }

    /// <summary>Показать окно (из значка в области уведомлений или при повторном запуске программы).</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void UpdateTray() => _tray.Update(_vm.Status.State, _vm.ServiceAvailable, _vm.StateText);

    private async void OnServiceConnectionChanged(bool connected)
    {
        _vm.ServiceAvailable = connected;
        UpdateTray();
        if (!connected)
            return;

        LogBox.Clear();
        try
        {
            var response = await _client.RequestAsync(new IpcMessage { Command = IpcCommands.GetSettings });
            if (response.Settings is { } view)
                _vm.LoadSettings(view);

            var exclusions = await _client.RequestAsync(new IpcMessage { Command = IpcCommands.GetExclusions });
            _vm.ExclusionsText = exclusions.ExclusionsText ?? "";
            if (exclusions.ExclusionReport is { } report)
                _vm.ApplyExclusionReport(report);
        }
        catch (InvalidOperationException)
        {
            // Связь оборвалась — настройки загрузятся при следующем подключении.
        }
    }

    // ---------- Подключение ----------

    private async void OnToggleClick(object sender, RoutedEventArgs e) => await ToggleConnectionAsync();

    private async Task ToggleConnectionAsync()
    {
        if (!_vm.CanToggleConnection)
            return;
        try
        {
            var command = _vm.IsActive ? IpcCommands.Disconnect : IpcCommands.Connect;
            var response = await _client.RequestAsync(new IpcMessage { Command = command });
            if (response.Ok != true && response.Error is { } error)
            {
                ShowFromTray();
                MessageBox.Show(this, error, "OpenToConnect", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (InvalidOperationException ex)
        {
            ShowFromTray();
            MessageBox.Show(this, ex.Message, "OpenToConnect", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- Исключения ----------

    private async void OnAddExclusionClick(object sender, RoutedEventArgs e) => await AddExclusionsAsync(_vm.NewExclusion);

    private async void OnNewExclusionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await AddExclusionsAsync(_vm.NewExclusion);
        }
    }

    private async void OnPasteExclusionsClick(object sender, RoutedEventArgs e)
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (string.IsNullOrWhiteSpace(text))
        {
            _vm.ExclusionsMessage = "В буфере обмена нет текста. Скопируйте список адресов — по одному на строку или через запятую.";
            return;
        }
        await AddExclusionsAsync(text);
    }

    private void OnAddAppClick(object sender, RoutedEventArgs e)
    {
        var menu = AddAppButton.ContextMenu;
        menu.PlacementTarget = AddAppButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void OnAddAppFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Программа, соединения которой пойдут мимо туннеля",
            Filter = "Программы (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
            await AddExclusionsAsync(dialog.FileName);
    }

    private async void OnAddAppFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Папка: соединения всех программ из неё пойдут мимо туннеля",
        };
        if (dialog.ShowDialog(this) == true)
            await AddExclusionsAsync(dialog.FolderName);
    }

    private async void OnAddAppRunningClick(object sender, RoutedEventArgs e)
    {
        var picker = new RunningAppPicker { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedPath is { } path)
            await AddExclusionsAsync(path);
    }

    private async Task AddExclusionsAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            _vm.ExclusionsMessage = "Введите IP, подсеть, домен или маску *.домен.";
            return;
        }

        var result = ExclusionTextEditor.Add(_vm.ExclusionsText, input);
        if (result.Added.Count > 0 && !await SaveExclusionsAsync(result.Text))
            return;

        var parts = new List<string>();
        if (result.Added.Count > 0)
            parts.Add(result.Added.Count == 1 ? $"Добавлено: {result.Added[0]}" : $"Добавлено записей: {result.Added.Count}");
        if (result.Duplicates > 0)
            parts.Add($"уже в списке: {result.Duplicates}");
        if (result.Errors.Count > 0)
            parts.Add(result.Errors.Count == 1 ? $"«{result.Errors[0].Text}» — {result.Errors[0].Message}" : $"не распознано: {result.Errors.Count}");
        _vm.ExclusionsMessage = string.Join("; ", parts);

        if (result.Errors.Count == 0)
            _vm.NewExclusion = "";
        NewExclusionInput.Focus();
    }

    private async void OnRemoveExclusionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string entry)
            return;
        if (await SaveExclusionsAsync(ExclusionTextEditor.Remove(_vm.ExclusionsText, entry)))
            _vm.ExclusionsMessage = $"Удалено: {entry}";
    }

    private async Task<bool> SaveExclusionsAsync(string text)
    {
        try
        {
            var response = await _client.RequestAsync(new IpcMessage { Command = IpcCommands.SaveExclusions, ExclusionsText = text });
            if (response.Ok == true)
            {
                _vm.ExclusionsText = response.ExclusionsText ?? text;
                return true;
            }
            _vm.ExclusionsMessage = response.Error ?? "Не удалось сохранить.";
        }
        catch (InvalidOperationException ex)
        {
            _vm.ExclusionsMessage = ex.Message;
        }
        return false;
    }

    // ---------- Настройки ----------

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e) => _vm.SettingsOpen = true;

    private void OnCloseSettingsClick(object sender, RoutedEventArgs e)
    {
        _vm.SettingsOpen = false;
        _vm.SaveMessage = "";
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await SaveAsync();

    private async void OnTrustCertClick(object sender, RoutedEventArgs e)
    {
        if (_vm.RejectedCertPin is not { } pin)
            return;
        var answer = MessageBox.Show(this,
            $"Доверять серверу с отпечатком\n{pin}?\n\nСверьте отпечаток с администратором сервера.",
            "OpenToConnect", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;
        _vm.ServerCertPin = pin;
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        var settings = _vm.ToSettings();
        if (settings.Validate() is { } validation)
        {
            _vm.SaveMessage = validation;
            return;
        }

        try
        {
            var response = await _client.RequestAsync(new IpcMessage
            {
                Command = IpcCommands.SaveSettings,
                SettingsUpdate = new SettingsUpdate { Settings = settings, NewPassword = _newPassword },
            });
            if (response.Ok == true && response.Settings is { } view)
            {
                // Служба сбрасывает сохранённый пароль, если сменились сервер, логин, протокол или отпечаток.
                bool passwordDropped = _vm.HasPassword && _newPassword is null && !view.HasPassword;
                _vm.LoadSettings(view);
                _newPassword = null;
                PasswordInput.Clear();
                _vm.SaveMessage = passwordDropped
                    ? "Сохранено. Сервер или логин изменились — введите пароль заново."
                    : _vm.IsActive ? "Сохранено. Применится при следующем подключении." : "Сохранено.";
            }
            else
            {
                _vm.SaveMessage = response.Error ?? "Не удалось сохранить.";
            }
        }
        catch (InvalidOperationException ex)
        {
            _vm.SaveMessage = ex.Message;
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) =>
        _newPassword = PasswordInput.Password.Length > 0 ? PasswordInput.Password : null;

    // ---------- Журнал ----------

    private void AppendLog(string[] lines)
    {
        foreach (var line in lines)
            LogBox.AppendText(line + Environment.NewLine);
        if (LogBox.Text.Length > MaxLogChars)
            LogBox.Text = LogBox.Text[^(MaxLogChars / 2)..];
        LogBox.ScrollToEnd();
    }

    // ---------- Окно и выход ----------

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _timer.Start();
    }

    /// <summary>Крестик прячет окно в область уведомлений: подключение и значок остаются.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private async Task ExitAsync()
    {
        _exiting = true;
        _timer.Stop();
        _tray.Dispose();
        await _client.DisposeAsync();
        Close();
        Application.Current.Shutdown();
    }
}

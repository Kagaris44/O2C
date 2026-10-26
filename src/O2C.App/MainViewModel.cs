using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using O2C.Core;
using O2C.Core.Exclusions;
using O2C.Core.Updates;

namespace O2C.App;

internal sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly Brush Grey = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));
    private static readonly Brush Amber = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x00)));
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xD0, 0x30, 0x30)));

    private ConnectionStatus _status = ConnectionStatus.Disconnected;
    private bool _serviceAvailable;
    private bool _settingsOpen;
    private string _server = "";
    private string _protocol = "anyconnect";
    private string _username = "";
    private string _authGroup = "";
    private string _serverCertPin = "";
    private bool _autoReconnect = true;
    private bool _useServerDns;
    private bool _hasPassword;
    private string _saveMessage = "";
    private string _newExclusion = "";
    private string _exclusionsMessage = "";
    private string _exclusionsSummary = "";
    private ReleaseInfo? _update;
    private int _updateProgress = -1;
    private string _updateStatus = "";
    private bool _checkingUpdate;
    private bool _autoUpdateCheck = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---------- Обновление ----------

    public ReleaseInfo? Update
    {
        get => _update;
        set
        {
            if (Set(ref _update, value))
            {
                OnPropertyChanged(nameof(UpdateVisibility));
                OnPropertyChanged(nameof(UpdateTooltip));
            }
        }
    }

    /// <summary>Процент загрузки установщика; -1 — загрузки нет.</summary>
    public int UpdateProgress
    {
        get => _updateProgress;
        set
        {
            if (Set(ref _updateProgress, value))
                OnPropertyChanged(nameof(UpdateTooltip));
        }
    }

    public Visibility UpdateVisibility => Update is null ? Visibility.Collapsed : Visibility.Visible;

    public static string VersionText => $"Версия {UpdateChecker.CurrentVersion.ToString(3)}";

    /// <summary>Результат ручной проверки обновлений (в настройках).</summary>
    public string UpdateStatus { get => _updateStatus; set => Set(ref _updateStatus, value); }

    public bool CheckingUpdate
    {
        get => _checkingUpdate;
        set
        {
            if (Set(ref _checkingUpdate, value))
                OnPropertyChanged(nameof(CanCheckUpdate));
        }
    }

    public bool CanCheckUpdate => !CheckingUpdate;

    public bool AutoUpdateCheck { get => _autoUpdateCheck; set => Set(ref _autoUpdateCheck, value); }

    public string UpdateTooltip => Update is null ? ""
        : UpdateProgress >= 0 ? $"Загрузка версии {Update.Version.ToString(3)}: {UpdateProgress}%"
        : $"Доступна версия {Update.Version.ToString(3)} (установлена {UpdateChecker.CurrentVersion.ToString(3)}). Нажмите, чтобы обновиться.";

    // ---------- Подключение ----------

    public bool ServiceAvailable
    {
        get => _serviceAvailable;
        set
        {
            if (Set(ref _serviceAvailable, value))
                OnStatusPropertiesChanged();
        }
    }

    public ConnectionStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            OnStatusPropertiesChanged();
        }
    }

    public string StateText => !ServiceAvailable ? "Служба недоступна" : Status.State switch
    {
        ConnectionState.Disconnected => "Отключено",
        ConnectionState.Connecting => "Подключение…",
        ConnectionState.Connected => "Подключено",
        ConnectionState.Reconnecting => "Переподключение…",
        ConnectionState.Disconnecting => "Отключение…",
        ConnectionState.Error => "Ошибка",
        _ => Status.State.ToString(),
    };

    /// <summary>Под статусом: адрес и время в подключении, либо пояснение (ошибка, повтор через N с).</summary>
    public string StateDetails
    {
        get
        {
            if (!ServiceAvailable)
                return "Служба OpenToConnect не запущена. Переустановите программу.";
            if (Status.State == ConnectionState.Connected && Status.ConnectedSince is { } since)
            {
                var elapsed = DateTimeOffset.Now - since;
                var time = string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");
                return $"{Status.AssignedIp} · {time}";
            }
            return Status.Message ?? (Status.State == ConnectionState.Disconnected ? "Нажмите, чтобы подключиться" : "");
        }
    }

    public Brush StateBrush => !ServiceAvailable ? Grey : Status.State switch
    {
        ConnectionState.Connected => Green,
        ConnectionState.Error => Red,
        ConnectionState.Disconnected => Grey,
        _ => Amber,
    };

    public bool IsActive => Status.State is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Reconnecting;

    public string ConnectTooltip => IsActive ? "Отключить" : "Подключить";

    public bool CanToggleConnection => ServiceAvailable && Status.State != ConnectionState.Disconnecting;

    public string? RejectedCertPin => Status.RejectedCertPin;

    public Visibility CertPromptVisibility => Status.RejectedCertPin is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Раз в секунду — обновить время в подключении.</summary>
    public void Tick()
    {
        if (Status.State == ConnectionState.Connected)
            OnPropertyChanged(nameof(StateDetails));
    }

    // ---------- Экраны ----------

    public bool SettingsOpen
    {
        get => _settingsOpen;
        set
        {
            if (Set(ref _settingsOpen, value))
            {
                OnPropertyChanged(nameof(MainVisibility));
                OnPropertyChanged(nameof(SettingsVisibility));
            }
        }
    }

    public Visibility MainVisibility => SettingsOpen ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SettingsVisibility => SettingsOpen ? Visibility.Visible : Visibility.Collapsed;

    // ---------- Исключения ----------

    /// <summary>Список как его хранит служба (текст, по записи на строку). Окно правит его по одной записи.</summary>
    public string ExclusionsText { get; set; } = "";

    public string NewExclusion { get => _newExclusion; set => Set(ref _newExclusion, value); }
    public string ExclusionsMessage { get => _exclusionsMessage; set => Set(ref _exclusionsMessage, value); }
    public string ExclusionsSummary { get => _exclusionsSummary; private set => Set(ref _exclusionsSummary, value); }

    public ObservableCollection<ExclusionRow> ExclusionRows { get; } = [];

    public Visibility EmptyListVisibility => ExclusionRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public void ApplyExclusionReport(ExclusionReport report)
    {
        ExclusionsSummary = report.Message ?? "";
        ExclusionRows.Clear();
        foreach (var item in report.Items)
            ExclusionRows.Add(MakeRow(item, report.Active));
        foreach (var error in report.ParseErrors)
            ExclusionRows.Add(new ExclusionRow(error.Text, "ошибка", error.Message, Red, Red));
        OnPropertyChanged(nameof(EmptyListVisibility));
    }

    private static ExclusionRow MakeRow(ExclusionItemStatus item, bool active)
    {
        if (item.Error is { } error)
            return new ExclusionRow(item.Entry, "ошибка", error, Red, Red);
        if (!active)
            return new ExclusionRow(item.Entry, "", "Действует, пока есть подключение.", Grey, Grey);
        if (item.Routes.Count == 0)
            return new ExclusionRow(item.Entry, "ждёт", item.Note ?? "Маршрутов пока нет.", Grey, Grey);

        int count = item.Routes.Count;
        // Служба присылает не больше 10 адресов и строку «…ещё N».
        if (item.Routes[^1].StartsWith('…') &&
            int.TryParse(new string([.. item.Routes[^1].Where(char.IsDigit)]), NumberStyles.None, CultureInfo.InvariantCulture, out var more))
        {
            count = count - 1 + more;
        }
        return new ExclusionRow(item.Entry, count.ToString(CultureInfo.InvariantCulture),
            "Мимо туннеля: " + string.Join(", ", item.Routes), Green, Grey);
    }

    // ---------- Настройки ----------

    public IReadOnlyList<string> Protocols { get; } = ConnectionSettings.KnownProtocols;

    public string Server { get => _server; set => Set(ref _server, value); }
    public string Protocol { get => _protocol; set => Set(ref _protocol, value); }
    public string Username { get => _username; set => Set(ref _username, value); }
    public string AuthGroup { get => _authGroup; set => Set(ref _authGroup, value); }
    public string ServerCertPin { get => _serverCertPin; set => Set(ref _serverCertPin, value); }
    public bool AutoReconnect { get => _autoReconnect; set => Set(ref _autoReconnect, value); }
    public bool UseServerDns { get => _useServerDns; set => Set(ref _useServerDns, value); }
    public string SaveMessage { get => _saveMessage; set => Set(ref _saveMessage, value); }

    public bool HasPassword
    {
        get => _hasPassword;
        set
        {
            if (Set(ref _hasPassword, value))
                OnPropertyChanged(nameof(PasswordHint));
        }
    }

    public string PasswordHint => HasPassword ? "Пароль сохранён. Оставьте поле пустым, чтобы не менять." : "Пароль не задан.";

    public void LoadSettings(SettingsView view)
    {
        var s = view.Settings;
        Server = s.Server;
        Protocol = s.Protocol;
        Username = s.Username;
        AuthGroup = s.AuthGroup ?? "";
        ServerCertPin = s.ServerCertPin ?? "";
        AutoReconnect = s.AutoReconnect;
        UseServerDns = s.UseServerDns;
        HasPassword = view.HasPassword;
    }

    public ConnectionSettings ToSettings() => new()
    {
        Server = Server.Trim(),
        Protocol = Protocol,
        Username = Username.Trim(),
        AuthGroup = string.IsNullOrWhiteSpace(AuthGroup) ? null : AuthGroup.Trim(),
        ServerCertPin = string.IsNullOrWhiteSpace(ServerCertPin) ? null : ServerCertPin.Trim(),
        AutoReconnect = AutoReconnect,
        UseServerDns = UseServerDns,
    };

    private void OnStatusPropertiesChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(StateText), nameof(StateDetails), nameof(StateBrush), nameof(IsActive), nameof(ConnectTooltip),
                     nameof(CanToggleConnection), nameof(RejectedCertPin), nameof(CertPromptVisibility),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

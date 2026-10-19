using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using O2C.Core;
using O2C.Core.Ipc;

namespace O2C.Service;

/// <summary>
/// Хранит настройки в C:\ProgramData\OpenToConnect\settings.json. Пароль зашифрован DPAPI (LocalMachine)
/// и дополнительно защищён правами на папку (только SYSTEM и администраторы).
/// </summary>
internal sealed class SettingsStore
{
    private static readonly byte[] Entropy = "O2C.Password.v1"u8.ToArray();
    private readonly Lock _lock = new();
    private readonly ILogger<SettingsStore> _logger;
    private StoredSettings _current;

    public SettingsStore(ILogger<SettingsStore> logger)
    {
        _logger = logger;
        _current = Load();
    }

    public ConnectionSettings Settings
    {
        get
        {
            lock (_lock)
                return _current.Settings;
        }
    }

    public SettingsView GetView()
    {
        lock (_lock)
            return new SettingsView { Settings = _current.Settings, HasPassword = _current.ProtectedPassword is not null };
    }

    public string? GetPassword()
    {
        string? protectedPassword;
        lock (_lock)
            protectedPassword = _current.ProtectedPassword;
        if (protectedPassword is null)
            return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedPassword), Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Не удалось расшифровать сохранённый пароль");
            return null;
        }
    }

    /// <summary>
    /// Сохраняет настройки. Возвращает true, если сохранённый пароль сброшен (см. <see cref="KeepsPassword"/>).
    /// </summary>
    public bool Save(SettingsUpdate update)
    {
        bool passwordDropped;
        lock (_lock)
        {
            // Без нового пароля старый остаётся, только если не менялось, куда и под кем он уходит.
            bool keep = KeepsPassword(_current.Settings, update.Settings);
            passwordDropped = update.NewPassword is null && !keep && _current.ProtectedPassword is not null;
            var protectedPassword = update.NewPassword switch
            {
                null => keep ? _current.ProtectedPassword : null,
                "" => null,
                var p => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(p), Entropy, DataProtectionScope.LocalMachine)),
            };
            Write(_current with { Settings = update.Settings, ProtectedPassword = protectedPassword });
        }
        _logger.LogInformation("Настройки сохранены (сервер {Server}, пользователь {User}{Dropped})",
            update.Settings.Server, update.Settings.Username, passwordDropped ? ", сохранённый пароль сброшен" : "");
        return passwordDropped;
    }

    /// <summary>
    /// Сохранённый пароль можно оставить, только если не изменились сервер, логин и отпечаток сертификата.
    /// Иначе любой пользователь компьютера, сменив адрес сервера, отправил бы чужой сохранённый пароль
    /// на свой сервер.
    /// </summary>
    internal static bool KeepsPassword(ConnectionSettings before, ConnectionSettings after) =>
        string.Equals(before.Server.Trim(), after.Server.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(before.Username, after.Username, StringComparison.Ordinal) &&
        string.Equals(before.ServerCertPin ?? "", after.ServerCertPin ?? "", StringComparison.Ordinal) &&
        string.Equals(before.Protocol, after.Protocol, StringComparison.Ordinal);

    /// <summary>Список исключений — как его ввёл пользователь (с комментариями).</summary>
    public event Action? ExclusionsChanged;

    public string GetExclusionsText()
    {
        lock (_lock)
            return _current.ExclusionsText ?? "";
    }

    public void SaveExclusions(string text)
    {
        lock (_lock)
            Write(_current with { ExclusionsText = text });
        _logger.LogInformation("Список исключений сохранён");
        ExclusionsChanged?.Invoke();
    }

    /// <summary>Атомарная запись: сначала во временный файл, затем замена.</summary>
    private void Write(StoredSettings next)
    {
        var tmp = ServicePaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(next, IpcProtocol.JsonOptions));
        File.Move(tmp, ServicePaths.SettingsFile, overwrite: true);
        _current = next;
    }

    private StoredSettings Load()
    {
        try
        {
            if (File.Exists(ServicePaths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(ServicePaths.SettingsFile), IpcProtocol.JsonOptions);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Не удалось прочитать настройки, используются значения по умолчанию");
        }
        return new StoredSettings { Settings = new ConnectionSettings() };
    }

    private sealed record StoredSettings
    {
        public required ConnectionSettings Settings { get; init; }
        public string? ProtectedPassword { get; init; }
        public string? ExclusionsText { get; init; }
    }
}

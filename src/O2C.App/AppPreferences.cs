using System.IO;
using System.Text.Json;

namespace O2C.App;

/// <summary>
/// Настройки окна для текущего пользователя (не службы): %LOCALAPPDATA%\OpenToConnect\app.json.
/// </summary>
internal sealed class AppPreferences
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenToConnect", "app.json");

    /// <summary>Проверять новые версии автоматически.</summary>
    public bool AutoUpdateCheck { get; set; } = true;

    public static AppPreferences Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(FilePath)) ?? new AppPreferences();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Повреждённый файл — настройки по умолчанию.
        }
        return new AppPreferences();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не сохранилось — останется до перезапуска окна.
        }
    }
}

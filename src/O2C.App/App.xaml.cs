using System.IO;
using System.Windows;
using O2C.Core.Exclusions;

namespace O2C.App;

/// <summary>
/// Один экземпляр на пользователя. Повторный запуск показывает уже открытое окно.
/// Ключ --minimized (автозапуск при входе в Windows) — только значок в области уведомлений.
/// Аргумент со списком (o2c://add/… или код O2C1.…) передаётся открытому окну через файл
/// и то же событие показа окна.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Объекты синхронизации освобождаются в OnExit — жизненный цикл WPF-приложения.")]
public partial class App : Application
{
    // O2C_DEV_INSTANCE — отдельный экземпляр для проверки сборки рядом с установленной программой.
    private static readonly string Suffix = Environment.GetEnvironmentVariable("O2C_DEV_INSTANCE") is { Length: > 0 } s ? "." + s : "";
    private static readonly string InstanceName = @"Local\O2C.App" + Suffix;
    private static readonly string ShowEventName = @"Local\O2C.App.Show" + Suffix;
    private static readonly string PendingImportFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenToConnect", "import" + Suffix + ".txt");

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        var import = e.Args.FirstOrDefault(a => a.Contains(ShareCode.Prefix, StringComparison.Ordinal));

        _singleInstance = new Mutex(initiallyOwned: true, InstanceName, out var created);
        if (!created)
        {
            // Уже запущено: при ручном запуске просим первый экземпляр показать окно.
            if (!minimized && EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
            {
                if (import is not null)
                    WritePendingImport(import);
                existing.Set();
                existing.Dispose();
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var window = new MainWindow();
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.InvokeAsync(() =>
            {
                window.ShowFromTray();
                if (TakePendingImport() is { } link)
                    window.ImportFromLink(link);
            }), null, Timeout.Infinite, executeOnlyOnce: false);

        if (!minimized)
            window.Show();
        if (import is not null)
            window.ImportFromLink(import);
    }

    private static void WritePendingImport(string link)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PendingImportFile)!);
            File.WriteAllText(PendingImportFile, link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не передалось — окно просто откроется.
        }
    }

    private static string? TakePendingImport()
    {
        try
        {
            if (!File.Exists(PendingImportFile))
                return null;
            var link = File.ReadAllText(PendingImportFile);
            File.Delete(PendingImportFile);
            return link;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

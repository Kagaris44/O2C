using System.Windows;

namespace O2C.App;

/// <summary>
/// Один экземпляр на пользователя. Повторный запуск показывает уже открытое окно.
/// Ключ --minimized (автозапуск при входе в Windows) — только значок в области уведомлений.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Объекты синхронизации освобождаются в OnExit — жизненный цикл WPF-приложения.")]
public partial class App : Application
{
    // O2C_DEV_INSTANCE — отдельный экземпляр для проверки сборки рядом с установленной программой.
    private static readonly string Suffix = Environment.GetEnvironmentVariable("O2C_DEV_INSTANCE") is { Length: > 0 } s ? "." + s : "";
    private static readonly string InstanceName = @"Local\O2C.App" + Suffix;
    private static readonly string ShowEventName = @"Local\O2C.App.Show" + Suffix;

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

        _singleInstance = new Mutex(initiallyOwned: true, InstanceName, out var created);
        if (!created)
        {
            // Уже запущено: при ручном запуске просим первый экземпляр показать окно.
            if (!minimized && EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
            {
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
            (_, _) => Dispatcher.InvokeAsync(window.ShowFromTray), null, Timeout.Infinite, executeOnlyOnce: false);

        if (!minimized)
            window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

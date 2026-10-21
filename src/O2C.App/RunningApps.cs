using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace O2C.App;

/// <summary>
/// Запущенная программа для выбора в исключения.
/// <see cref="Folder"/> — папка, которую предлагается добавить: если из той же папки программы запущены
/// и другие exe (Steam, браузеры, лаунчеры), это их общая папка, иначе — папка самого exe.
/// </summary>
internal sealed record RunningApp(string Path, string Name, string Description, ImageSource? Icon, bool HasWindow,
    string Folder, int Siblings)
{
    public string Details => Path;

    public string FolderHint => Siblings == 0 ? ""
        : $"Ещё {Siblings} {Plural(Siblings)} из папки {System.IO.Path.GetFileName(Folder.TrimEnd(System.IO.Path.DirectorySeparatorChar))} — лучше добавить папку.";

    public Visibility FolderHintVisibility => Siblings == 0 ? Visibility.Collapsed : Visibility.Visible;

    private static string Plural(int n) => (n % 10, n % 100) switch
    {
        (1, not 11) => "программа",
        (2 or 3 or 4, not (12 or 13 or 14)) => "программы",
        _ => "программ",
    };
}

/// <summary>
/// Список запущенных программ текущего сеанса пользователя (службы Windows не входят).
/// Приложением считается процесс с окном на панели задач — как вкладка «Приложения» в диспетчере задач.
/// Окно работает с правами пользователя, поэтому программы, запущенные от администратора, могут не попасть в список.
/// </summary>
internal static unsafe partial class RunningApps
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;
    private const uint DwmwaCloaked = 14;

    public static IReadOnlyList<RunningApp> Collect()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + System.IO.Path.DirectorySeparatorChar;
        var self = Environment.ProcessPath;
        var session = Process.GetCurrentProcess().SessionId;
        var windowed = WindowProcessIds();
        var apps = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);   // путь → есть окно

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? path;
                try
                {
                    if (process.SessionId != session)
                        continue;   // службы и другие пользователи
                    path = GetPath(process.Id);
                }
                catch (InvalidOperationException)
                {
                    continue;   // процесс уже завершился
                }
                if (path is null ||
                    path.StartsWith(windows, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, self, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                apps[path] = (apps.TryGetValue(path, out var has) && has) || windowed.Contains(process.Id);
            }
        }

        var paths = apps.Keys.ToList();
        return [.. apps
            .Select(kv => Describe(kv.Key, kv.Value, paths))
            .OrderByDescending(a => a.HasWindow)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static RunningApp Describe(string path, bool hasWindow, IReadOnlyList<string> allPaths)
    {
        var (folder, siblings) = SuggestFolder(path, allPaths);
        var description = "";
        try
        {
            description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? "";
        }
        catch (FileNotFoundException)
        {
        }
        return new RunningApp(path, System.IO.Path.GetFileName(path), description, LoadIcon(path), hasWindow, folder, siblings);
    }

    /// <summary>
    /// Папка программы с учётом её «соседей»: другие запущенные exe той же установки
    /// (первая папка под Program Files, AppData и т. п.). Предлагается их общая папка.
    /// </summary>
    internal static (string Folder, int Siblings) SuggestFolder(string path, IReadOnlyList<string> allPaths)
    {
        var sep = System.IO.Path.DirectorySeparatorChar;
        var own = System.IO.Path.GetDirectoryName(path) + sep;
        var root = InstallRoot(path);
        if (root is null)
            return (own, 0);

        var siblings = allPaths
            .Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase) &&
                        p.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (siblings.Count == 0)
            return (own, 0);

        var common = own;
        foreach (var other in siblings)
        {
            while (!other.StartsWith(common, StringComparison.OrdinalIgnoreCase) && common.Length > root.Length)
                common = System.IO.Path.GetDirectoryName(common.TrimEnd(sep)) + sep;
        }
        return (common, siblings.Count);
    }

    /// <summary>Папка установки: первая папка под Program Files (для приложений Store — папка пакета), AppData\Local\Programs, AppData\Local или AppData\Roaming.</summary>
    private static string? InstallRoot(string path)
    {
        var sep = System.IO.Path.DirectorySeparatorChar;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] bases =
        [
            // Приложения Microsoft Store: у каждого пакета своя папка внутри WindowsApps.
            System.IO.Path.Combine(programFiles, "WindowsApps"),
            System.IO.Path.Combine(local, "Programs"),
            local,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            programFiles,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];
        foreach (var b in bases)
        {
            if (b.Length == 0)
                continue;
            var prefix = b.TrimEnd(sep) + sep;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var next = path.IndexOf(sep, prefix.Length);
            return next < 0 ? null : path[..(next + 1)];
        }
        return null;
    }

    private static BitmapSource? LoadIcon(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null)
                return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>PID процессов, у которых есть окно на панели задач: видимое, без владельца, не служебное, не скрытое DWM.</summary>
    private static HashSet<int> WindowProcessIds()
    {
        var pids = new HashSet<int>();
        var handle = GCHandle.Alloc(pids);
        try
        {
            EnumWindows(&OnWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return pids;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnWindow(IntPtr hwnd, IntPtr state)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero ||
            (GetWindowLongPtrW(hwnd, GwlExStyle) & WsExToolWindow) != 0 || GetWindowTextLengthW(hwnd) == 0)
        {
            return 1;
        }
        int cloaked = 0;
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, &cloaked, sizeof(int)) == 0 && cloaked != 0)
            return 1;
        uint pid = 0;
        if (GetWindowThreadProcessId(hwnd, &pid) == 0)
            return 1;
        ((HashSet<int>)GCHandle.FromIntPtr(state).Target!).Add((int)pid);
        return 1;
    }

    private static string? GetPath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            const int capacity = 1024;
            char* buffer = stackalloc char[capacity];
            int size = capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> callback, IntPtr state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(IntPtr hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextLengthW(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, uint* pid);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, void* value, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(IntPtr process, uint flags, char* name, ref int size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}

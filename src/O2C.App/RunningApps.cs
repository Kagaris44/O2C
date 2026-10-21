using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace O2C.App;

/// <summary>Запущенная программа для выбора в исключения: путь к exe, имя, значок, есть ли окно.</summary>
internal sealed record RunningApp(string Path, string Name, string Description, ImageSource? Icon, bool HasWindow, int Count)
{
    public string Details => Count > 1 ? $"{Path}  ·  процессов: {Count}" : Path;
}

/// <summary>
/// Список запущенных программ. Окно работает с правами пользователя, поэтому видит пути программ своей
/// сессии; системные службы и программы, запущенные от администратора, могут не попасть в список.
/// </summary>
internal static partial class RunningApps
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public static IReadOnlyList<RunningApp> Collect()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var self = Environment.ProcessPath;
        var groups = new Dictionary<string, (bool HasWindow, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? path;
                bool hasWindow;
                try
                {
                    path = GetPath(process.Id);
                    hasWindow = process.MainWindowHandle != IntPtr.Zero;
                }
                catch (InvalidOperationException)
                {
                    continue; // процесс уже завершился
                }
                if (path is null ||
                    path.StartsWith(windows + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, self, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                groups[path] = groups.TryGetValue(path, out var g)
                    ? (g.HasWindow || hasWindow, g.Count + 1)
                    : (hasWindow, 1);
            }
        }

        return [.. groups
            .Select(kv => Describe(kv.Key, kv.Value.HasWindow, kv.Value.Count))
            .OrderByDescending(a => a.HasWindow)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static RunningApp Describe(string path, bool hasWindow, int count)
    {
        var name = System.IO.Path.GetFileName(path);
        var description = "";
        try
        {
            description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? "";
        }
        catch (FileNotFoundException)
        {
        }
        return new RunningApp(path, name, description, LoadIcon(path), hasWindow, count);
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

    private static unsafe string? GetPath(int pid)
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(IntPtr process, uint flags, char* name, ref int size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}

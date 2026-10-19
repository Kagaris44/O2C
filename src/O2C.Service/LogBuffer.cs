using System.Globalization;

namespace O2C.Service;

/// <summary>Последние строки журнала подключения: пишутся в файл и рассылаются подключённым GUI.</summary>
internal sealed class LogBuffer
{
    private const int Capacity = 500;
    private readonly Queue<string> _lines = new();
    private readonly Lock _lock = new();
    private readonly RotatingFileWriter _file = new(ServicePaths.OpenConnectLogFile);

    public event Action<string>? LineAdded;

    public void Add(string line)
    {
        var stamped = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss} {line}");
        lock (_lock)
        {
            _lines.Enqueue(stamped);
            while (_lines.Count > Capacity)
                _lines.Dequeue();
        }
        _file.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd} {stamped}"));
        LineAdded?.Invoke(stamped);
    }

    /// <summary>Только в файл журнала, без показа в окне.</summary>
    public void AddFileOnly(string line) =>
        _file.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}"));

    public string[] Snapshot()
    {
        lock (_lock)
            return [.. _lines];
    }
}

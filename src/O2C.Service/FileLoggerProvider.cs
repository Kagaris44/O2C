using System.Globalization;

namespace O2C.Service;

/// <summary>Простой файловый лог с ротацией по размеру (один архивный файл .1).</summary>
internal sealed class RotatingFileWriter
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private readonly string _path;
    private readonly Lock _lock = new();

    public RotatingFileWriter(string path) => _path = path;

    public void WriteLine(string line)
    {
        lock (_lock)
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(_path, _path + ".1", overwrite: true);
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Лог не должен ронять службу.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly RotatingFileWriter _writer;

    public FileLoggerProvider(string path) => _writer = new RotatingFileWriter(path);

    public ILogger CreateLogger(string categoryName) => new FileLogger(_writer, categoryName);

    public void Dispose()
    {
    }

    private sealed class FileLogger(RotatingFileWriter writer, string category) : ILogger
    {
        private readonly string _category = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var text = string.Create(CultureInfo.InvariantCulture,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_category}: {formatter(state, exception)}");
            if (exception is not null)
                text += Environment.NewLine + exception;
            writer.WriteLine(text);
        }
    }
}

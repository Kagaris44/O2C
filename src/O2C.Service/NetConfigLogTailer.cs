using System.Text;
using O2C.Core;

namespace O2C.Service;

/// <summary>
/// Переносит журнал помощника настройки сети (его запускает openconnect, вывод которого мы не видим)
/// в общий журнал окна. Если помощник сообщил о неустранимой ошибке — разрывает подключение:
/// туннель без настроенной сети только мешает.
/// </summary>
internal sealed class NetConfigLogTailer : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly LogBuffer _log;
    private readonly ConnectionManager _connection;
    private readonly ILogger<NetConfigLogTailer> _logger;
    private long _offset;

    public NetConfigLogTailer(LogBuffer log, ConnectionManager connection, ILogger<NetConfigLogTailer> logger)
    {
        _log = log;
        _connection = connection;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Старые записи (до запуска службы) не показываем.
        var file = new FileInfo(ProductPaths.NetConfigLogFile);
        _offset = file.Exists ? file.Length : 0;

        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                ReadNewLines();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ReadNewLines()
    {
        string text;
        try
        {
            var info = new FileInfo(ProductPaths.NetConfigLogFile);
            if (!info.Exists)
                return;
            if (info.Length < _offset)
                _offset = 0; // файл ротирован
            if (info.Length == _offset)
                return;

            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(_offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
            // Берём только целые строки: последняя может дописываться прямо сейчас.
            var lastNewLine = text.LastIndexOf('\n');
            if (lastNewLine < 0)
                return;
            text = text[..(lastNewLine + 1)];
            _offset += Encoding.UTF8.GetByteCount(text);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Журнал помощника недоступен");
            return;
        }

        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            // Отметку времени из файла отбрасываем — у журнала окна своя.
            var message = line.Length > 20 && line[19] == ' ' ? line[20..] : line;
            _log.Add("[сеть] " + message);

            if (message.Contains(ProductPaths.ReadyMarker, StringComparison.Ordinal))
                _connection.OnNetworkReady();

            var fatal = message.IndexOf(ProductPaths.FatalMarker, StringComparison.Ordinal);
            if (fatal >= 0)
            {
                var reason = message[(fatal + ProductPaths.FatalMarker.Length)..].Trim();
                _logger.LogError("Помощник настройки сети: {Reason}", reason);
                _ = _connection.DisconnectAsync($"Не удалось настроить сеть: {reason}");
            }
        }
    }
}

using System.Diagnostics.Eventing.Reader;
using System.Net;
using System.Xml;
using O2C.Core.Exclusions;

namespace O2C.Service;

/// <summary>
/// Следит за журналом Microsoft-Windows-DNS-Client/Operational: каждое имя, разрешённое через DNS-клиент
/// Windows, приходит сюда вместе с адресами. Служба не встаёт на пути DNS-запросов — её сбой DNS не ломает.
/// Журнал по умолчанию выключен: включаем на время работы и выключаем обратно, если включали сами.
/// </summary>
internal sealed class DnsClientWatcher : IDisposable
{
    public const string Channel = "Microsoft-Windows-DNS-Client/Operational";

    // 3008 — запрос завершён (имя и результаты), 3020 — получен ответ сервера.
    private const string Query = "*[System[(EventID=3008 or EventID=3020)]]";

    private readonly ILogger _logger;
    private readonly Action<string, IReadOnlyList<IPAddress>> _onResolved;
    private EventLogWatcher? _watcher;
    private bool _enabledByUs;

    public DnsClientWatcher(ILogger logger, Action<string, IReadOnlyList<IPAddress>> onResolved)
    {
        _logger = logger;
        _onResolved = onResolved;
    }

    public bool IsRunning => _watcher is not null;

    public void Start()
    {
        if (_watcher is not null)
            return;

        using (var config = new EventLogConfiguration(Channel))
        {
            if (!config.IsEnabled)
            {
                config.IsEnabled = true;
                config.SaveChanges();
                _enabledByUs = true;
                _logger.LogInformation("Журнал DNS-клиента включён");
            }
        }

        var watcher = new EventLogWatcher(new EventLogQuery(Channel, PathType.LogName, Query));
        watcher.EventRecordWritten += OnEvent;
        watcher.Enabled = true;
        _watcher = watcher;
    }

    public void Stop()
    {
        if (_watcher is { } watcher)
        {
            watcher.Enabled = false;
            watcher.EventRecordWritten -= OnEvent;
            watcher.Dispose();
            _watcher = null;
        }

        if (_enabledByUs)
        {
            try
            {
                using var config = new EventLogConfiguration(Channel);
                config.IsEnabled = false;
                config.SaveChanges();
                _enabledByUs = false;
                _logger.LogInformation("Журнал DNS-клиента выключен");
            }
            catch (EventLogException ex)
            {
                _logger.LogWarning(ex, "Не удалось выключить журнал DNS-клиента");
            }
        }
    }

    private void OnEvent(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventRecord is not { } record)
            return;
        try
        {
            using (record)
            {
                if (Parse(record.ToXml()) is var (name, addresses) && addresses.Count > 0)
                    _onResolved(name, addresses);
            }
        }
        catch (Exception ex) when (ex is XmlException or EventLogException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Не удалось разобрать событие DNS-клиента");
        }
    }

    /// <summary>Достаёт из XML события имя запроса и адреса результата.</summary>
    internal static (string Name, List<IPAddress> Addresses)? Parse(string xml)
    {
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
            doc.Load(reader);

        string? name = null, results = null;
        foreach (XmlNode node in doc.GetElementsByTagName("Data"))
        {
            switch (node.Attributes?["Name"]?.Value)
            {
                case "QueryName":
                    name = node.InnerText;
                    break;
                case "QueryResults":
                    results = node.InnerText;
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return (DnsNames.Normalize(name), DnsNames.ParseQueryResults(results));
    }

    public void Dispose() => Stop();
}

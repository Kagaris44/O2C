using O2C.Core.Exclusions;

namespace O2C.Service;

/// <summary>
/// Список подсетей для набора («@ru», «@blizzard»). Берётся более свежий из двух: вложенного в установщик
/// (data\&lt;файл&gt; рядом со службой) и скачанного с GitHub (C:\ProgramData\OpenToConnect\&lt;файл&gt;).
/// Скачивается не чаще раза в сутки, пока запись есть в списке и есть подключение.
/// </summary>
internal sealed class NetworkListSource : IDisposable
{
    /// <summary>Файлы прикладываются к каждому релизу и обновляются еженедельно (workflow network-lists).</summary>
    private const string DownloadBase = "https://github.com/Kagaris44/O2C/releases/latest/download/";

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromDays(1);

    private readonly string _fileName;
    private readonly string _title;
    private readonly int _minNetworks;
    private readonly ILogger _logger;
    private readonly LogBuffer _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly SemaphoreSlim _updating = new(1, 1);
    private DateTime _lastAttempt = DateTime.MinValue;
    private NetworkList? _list;

    /// <param name="fileName">Имя файла списка (одинаковое в установщике, в данных службы и в релизе).</param>
    /// <param name="title">Название для журнала в родительном падеже: «российских сетей», «сетей Blizzard».</param>
    /// <param name="minNetworks">Минимум подсетей IPv4: список короче считается испорченным.</param>
    public NetworkListSource(string fileName, string title, int minNetworks, ILogger logger, LogBuffer log)
    {
        _fileName = fileName;
        _title = title;
        _minNetworks = minNetworks;
        _logger = logger;
        _log = log;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenToConnect");
    }

    private string BundledFile => Path.Combine(AppContext.BaseDirectory, "data", _fileName);
    private string DownloadedFile => Path.Combine(ServicePaths.DataDirectory, _fileName);

    /// <summary>Текущий список (загружается при первом обращении).</summary>
    public NetworkList List => _list ??= LoadBest();

    /// <summary>Список обновился (после загрузки свежего файла).</summary>
    public event Action? Updated;

    private NetworkList LoadBest()
    {
        var bundled = TryLoad(BundledFile);
        var downloaded = TryLoad(DownloadedFile);
        var best = string.CompareOrdinal(downloaded?.Date, bundled?.Date) > 0 ? downloaded : bundled ?? downloaded;
        if (best is null)
        {
            _log.Add($"Список {_title} не найден ({_fileName}).");
            return NetworkList.Empty;
        }
        _logger.LogInformation("Список {Title}: {Count4} IPv4, {Count6} IPv6, данные от {Date}", _title, best.Count4, best.Count6, best.Date);
        return best;
    }

    private NetworkList? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var list = NetworkList.Parse(File.ReadAllText(path));
            return list.Count4 >= _minNetworks ? list : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось прочитать {Path}", path);
            return null;
        }
    }

    /// <summary>Скачивает свежий список, если с прошлой попытки прошли сутки. Ошибки только пишутся в журнал.</summary>
    public async Task UpdateIfDueAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastAttempt < UpdateInterval || !await _updating.WaitAsync(0, ct))
            return;
        try
        {
            _lastAttempt = DateTime.UtcNow;
            var text = await _http.GetStringAsync(DownloadBase + _fileName, ct);
            var list = NetworkList.Parse(text);
            if (list.Count4 < _minNetworks)
            {
                _logger.LogWarning("Скачанный список {Title} подозрительно мал: {Count}", _title, list.Count4);
                return;
            }
            if (string.CompareOrdinal(list.Date, List.Date) <= 0)
                return;

            var tmp = DownloadedFile + ".tmp";
            await File.WriteAllTextAsync(tmp, text, ct);
            File.Move(tmp, DownloadedFile, overwrite: true);
            _list = list;
            _log.Add($"Список {_title} обновлён: данные от {list.Date}, подсетей {list.Count4 + list.Count6}.");
            Updated?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Не удалось обновить список {Title}: {Message}", _title, ex.Message);
        }
        finally
        {
            _updating.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _updating.Dispose();
    }
}

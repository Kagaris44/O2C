using O2C.Core.Exclusions;

namespace O2C.Service;

/// <summary>
/// Список российских подсетей для записи «@ru». Берётся более свежий из двух: вложенного в установщик
/// (data\ru-networks.txt рядом со службой) и скачанного с GitHub (C:\ProgramData\OpenToConnect\ru-networks.txt).
/// Скачивается не чаще раза в сутки, пока запись есть в списке и есть подключение.
/// </summary>
internal sealed class RussianNetworkSource : IDisposable
{
    /// <summary>Файл прикладывается к каждому релизу и обновляется еженедельно (workflow ru-networks).</summary>
    public const string DownloadUrl = "https://github.com/Kagaris44/O2C/releases/latest/download/ru-networks.txt";

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromDays(1);
    private const int MinNetworks = 3000;

    private readonly ILogger _logger;
    private readonly LogBuffer _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly SemaphoreSlim _updating = new(1, 1);
    private DateTime _lastAttempt = DateTime.MinValue;
    private NetworkList? _list;

    public RussianNetworkSource(ILogger logger, LogBuffer log)
    {
        _logger = logger;
        _log = log;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenToConnect");
    }

    public static string BundledFile => Path.Combine(AppContext.BaseDirectory, "data", "ru-networks.txt");

    /// <summary>Текущий список (загружается при первом обращении).</summary>
    public NetworkList List => _list ??= LoadBest();

    /// <summary>Список обновился (после загрузки свежего файла).</summary>
    public event Action? Updated;

    private NetworkList LoadBest()
    {
        var bundled = TryLoad(BundledFile);
        var downloaded = TryLoad(ServicePaths.RussianNetworksFile);
        var best = string.CompareOrdinal(downloaded?.Date, bundled?.Date) > 0 ? downloaded : bundled ?? downloaded;
        if (best is null)
        {
            _log.Add("Список российских сетей не найден — запись @ru будет работать только по доменам .ru, .su, .рф.");
            return NetworkList.Empty;
        }
        _logger.LogInformation("Российские сети: {Count4} IPv4, {Count6} IPv6, данные от {Date}", best.Count4, best.Count6, best.Date);
        return best;
    }

    private NetworkList? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var list = NetworkList.Parse(File.ReadAllText(path));
            return list.Count4 >= MinNetworks ? list : null;
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
            var text = await _http.GetStringAsync(DownloadUrl, ct);
            var list = NetworkList.Parse(text);
            if (list.Count4 < MinNetworks)
            {
                _logger.LogWarning("Скачанный список российских сетей подозрительно мал: {Count}", list.Count4);
                return;
            }
            if (string.CompareOrdinal(list.Date, List.Date) <= 0)
                return;

            var tmp = ServicePaths.RussianNetworksFile + ".tmp";
            await File.WriteAllTextAsync(tmp, text, ct);
            File.Move(tmp, ServicePaths.RussianNetworksFile, overwrite: true);
            _list = list;
            _log.Add($"Список российских сетей обновлён: данные от {list.Date}, подсетей {list.Count4 + list.Count6}.");
            Updated?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Не удалось обновить список российских сетей: {Message}", ex.Message);
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

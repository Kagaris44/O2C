using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using O2C.Core.Updates;

namespace O2C.App;

/// <summary>
/// Проверка новых версий на GitHub: при запуске окна и затем раз в 12 часов.
/// Отправляется только запрос к api.github.com — без сведений о компьютере и подключении.
/// </summary>
internal sealed class UpdateChecker : IDisposable
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();

    public UpdateChecker()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub API требует User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"OpenToConnect/{CurrentVersion.ToString(3)}");
    }

    public static Version CurrentVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Найдена версия новее установленной (вызывается в потоке пула).</summary>
    public event Action<ReleaseInfo>? UpdateFound;

    /// <summary>Проверять автоматически (при запуске и раз в 12 часов). Ручная проверка работает всегда.</summary>
    public bool AutoCheck { get; set; } = true;

    public void Start() => _ = LoopAsync(_cts.Token);

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstDelay, ct);
            while (true)
            {
                if (AutoCheck && await CheckAsync(ct) is { Release: { } release } && release.IsNewerThan(CurrentVersion))
                    UpdateFound?.Invoke(release);
                await Task.Delay(Interval, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Последний релиз на GitHub или текст ошибки (нет сети, GitHub недоступен, непонятный ответ).
    /// Новее ли он установленной версии — решает вызывающий.
    /// </summary>
    public async Task<(ReleaseInfo? Release, string? Error)> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseInfo.LatestUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return (null, $"GitHub ответил {(int)response.StatusCode}.");
            var release = ReleaseInfo.Parse(await response.Content.ReadAsStringAsync(ct));
            return release is null ? (null, "Не удалось разобрать ответ GitHub.") : (release, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, "нет связи с GitHub.");
        }
    }

    /// <summary>
    /// Скачивает установщик во временную папку пользователя, сверяет SHA-256 (если GitHub его сообщил)
    /// и запускает установку. Windows запросит права администратора.
    /// </summary>
    public async Task DownloadAndInstallAsync(ReleaseInfo release, IProgress<int> progress, CancellationToken ct)
    {
        if (release.MsiUrl is not { } url)
            throw new InvalidOperationException("В релизе нет установщика.");

        var folder = Path.Combine(Path.GetTempPath(), "OpenToConnect-update");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"OpenToConnect-{release.Version.ToString(3)}.msi");

        using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                    progress.Report((int)(done * 100 / total));
            }
        }

        if (release.MsiSha256 is { } expected)
        {
            string actual;
            await using (var file = File.OpenRead(path))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                File.Delete(path);
                throw new InvalidOperationException("Контрольная сумма скачанного установщика не совпала с указанной на GitHub. Установка отменена.");
            }
        }

        Process.Start(new ProcessStartInfo("msiexec.exe") { ArgumentList = { "/i", path } })?.Dispose();
    }

    public static void OpenPage(ReleaseInfo release) =>
        Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true })?.Dispose();

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _http.Dispose();
    }
}

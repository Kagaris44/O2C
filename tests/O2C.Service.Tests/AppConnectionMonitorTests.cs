using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using O2C.Network;
using Xunit.Abstractions;

namespace O2C.Service.Tests;

/// <summary>
/// События сетевого фильтра Windows о разрешённых соединениях: путь программы и адрес собеседника
/// для UDP и TCP. Нужны права администратора (в CI есть, и там тест обязан выполниться).
/// </summary>
public class AppConnectionMonitorTests(ITestOutputHelper output)
{
    private static readonly IPAddress UdpTarget = IPAddress.Parse("198.51.100.77");
    private static readonly IPAddress TcpTarget = IPAddress.Parse("192.0.2.10");

    [Fact]
    public void Device_path_is_converted_to_drive_letter()
    {
        var mapper = new DevicePathMapper();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);   // C:\Windows
        // Путь, не похожий на устройство, возвращается как есть.
        Assert.Equal(windows, mapper.ToDosPath(windows));
    }

    [Fact]
    public async Task Reports_app_path_and_remote_address_for_udp_and_tcp()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true", "В CI тест должен идти с правами администратора.");
            output.WriteLine("Пропущено: нужны права администратора.");
            return;
        }

        var events = new ConcurrentQueue<AppConnection>();
        using var monitor = new AppConnectionMonitor(events.Enqueue);
        monitor.Start();
        await Task.Delay(500);

        var started = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        // UDP: одна датаграмма на адрес, где никто не слушает.
        using (var udp = new UdpClient(AddressFamily.InterNetwork))
            await udp.SendAsync(new byte[] { 1, 2, 3 }, 3, new IPEndPoint(UdpTarget, 40000));

        // TCP: попытка соединения (ответа не будет — нам нужно только разрешение на исходящее соединение).
        using (var tcp = new TcpClient(AddressFamily.InterNetwork))
        {
            try
            {
                await tcp.ConnectAsync(TcpTarget, 40001).WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (ex is TimeoutException or SocketException)
            {
            }
        }

        var self = Environment.ProcessPath!;
        AppConnection? udpEvent = null, tcpEvent = null;
        TimeSpan udpLatency = default, tcpLatency = default;
        while (sw.Elapsed < TimeSpan.FromSeconds(15) && (udpEvent is null || tcpEvent is null))
        {
            foreach (var e in events)
            {
                if (udpEvent is null && e.RemoteAddress.Equals(UdpTarget))
                {
                    udpEvent = e;
                    udpLatency = sw.Elapsed;
                }
                if (tcpEvent is null && e.RemoteAddress.Equals(TcpTarget))
                {
                    tcpEvent = e;
                    tcpLatency = sw.Elapsed;
                }
            }
            await Task.Delay(50);
        }

        var all = events.ToArray();
        output.WriteLine($"Наш процесс: {self}");
        output.WriteLine($"Всего событий за {sw.Elapsed.TotalSeconds:0.0} с: {all.Length}");
        foreach (var e in all.Take(15))
            output.WriteLine($"  {e.Protocol,3} {e.RemoteAddress}:{e.RemotePort}  {e.AppPath}");
        output.WriteLine($"UDP: {(udpEvent is null ? "нет события" : $"через {udpLatency.TotalMilliseconds:0} мс, {udpEvent.AppPath}, порт {udpEvent.RemotePort}, протокол {udpEvent.Protocol}")}");
        output.WriteLine($"TCP: {(tcpEvent is null ? "нет события" : $"через {tcpLatency.TotalMilliseconds:0} мс, {tcpEvent.AppPath}, порт {tcpEvent.RemotePort}, протокол {tcpEvent.Protocol}")}");

        Assert.NotNull(udpEvent);
        Assert.NotNull(tcpEvent);
        Assert.Equal(self, udpEvent.AppPath, ignoreCase: true);
        Assert.Equal(self, tcpEvent.AppPath, ignoreCase: true);
        Assert.Equal(17, udpEvent.Protocol);
        Assert.Equal(6, tcpEvent.Protocol);
        Assert.Equal(40000, udpEvent.RemotePort);
        Assert.Equal(40001, tcpEvent.RemotePort);
        Assert.True(udpEvent.Time >= started.AddSeconds(-1));
    }

    /// <summary>Диагностика: какие события приходят без аудита и с аудитом «Filtering Platform Connection».</summary>
    [Fact]
    public async Task Diagnose_event_sources()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ||
            Environment.GetEnvironmentVariable("CI") != "true")
        {
            return;   // меняет политику аудита — только на одноразовой машине CI
        }

        output.WriteLine("auditpol до: " + Run("auditpol", "/get /subcategory:{0CCE9226-69AE-11D9-BED3-505054503030}"));
        output.WriteLine("Брандмауэр: " + Run("netsh", "advfirewall show allprofiles state"));

        foreach (var audit in new[] { false, true })
        {
            if (audit)
                output.WriteLine("auditpol вкл: " + Run("auditpol", "/set /subcategory:{0CCE9226-69AE-11D9-BED3-505054503030} /success:enable /failure:enable"));

            var types = new ConcurrentDictionary<int, int>();
            var connections = new ConcurrentQueue<AppConnection>();
            using var monitor = new AppConnectionMonitor(connections.Enqueue);
            monitor.RawEvent += t => types.AddOrUpdate(t, 1, (_, n) => n + 1);
            monitor.Start();
            output.WriteLine($"--- аудит {(audit ? "ВКЛ" : "выкл")}: опции после Start = {monitor.Options}");
            await Task.Delay(500);

            for (int i = 0; i < 3; i++)
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                await udp.SendAsync(new byte[] { 1 }, 1, new IPEndPoint(UdpTarget, 41000 + i));
            }
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                await http.GetAsync(new Uri("https://www.example.com/"));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }

            await Task.Delay(8000);
            output.WriteLine($"типы событий: {string.Join(", ", types.Select(kv => $"{kv.Key}={kv.Value}"))}");
            output.WriteLine($"разрешённых соединений: {connections.Count}");
            output.WriteLine($"вызовов обработчика: {AppConnectionMonitor.CallbackCount}, ошибка: {AppConnectionMonitor.LastCallbackError ?? "нет"}");
            foreach (var c in connections.Take(8))
                output.WriteLine($"  {c.Protocol} {c.RemoteAddress}:{c.RemotePort} {c.AppPath}");
        }

        output.WriteLine("Security 5156 за минуту: " + Run("powershell",
            "-NoProfile -Command \"(Get-WinEvent -FilterHashtable @{LogName='Security';Id=5156;StartTime=(Get-Date).AddMinutes(-1)} -ErrorAction SilentlyContinue).Count\""));
    }

    private static string Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}

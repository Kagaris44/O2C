using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using O2C.Network;
using Xunit.Abstractions;

namespace O2C.Service.Tests;

/// <summary>
/// События ядра о соединениях программ (ETW): путь программы и адрес собеседника для UDP и TCP,
/// плюс замер нагрузки. Нужны права администратора (в CI есть, и там тест обязан выполниться).
/// </summary>
public class AppConnectionMonitorTests(ITestOutputHelper output)
{
    private static readonly IPAddress UdpTarget = IPAddress.Parse("198.51.100.77");
    // Событие ядра о TCP приходит, когда соединение установлено, поэтому нужен настоящий сервер.
    private const string TcpHost = "example.com";

    private bool HasAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return true;
        Assert.False(Environment.GetEnvironmentVariable("CI") == "true", "В CI тест должен идти с правами администратора.");
        output.WriteLine("Пропущено: нужны права администратора.");
        return false;
    }

    [Fact]
    public async Task Reports_app_path_and_remote_address_for_udp_and_tcp()
    {
        if (!HasAdmin())
            return;

        var events = new ConcurrentQueue<AppConnection>();
        using var monitor = new AppConnectionMonitor(events.Enqueue);
        monitor.Start();
        await Task.Delay(2000); // сеанс ETW начинает отдавать события не мгновенно

        var sw = Stopwatch.StartNew();
        using (var udp = new UdpClient(AddressFamily.InterNetwork))
            await udp.SendAsync(new byte[] { 1, 2, 3 }, 3, new IPEndPoint(UdpTarget, 40000));
        var udpSent = sw.Elapsed;

        var tcpTarget = (await Dns.GetHostAddressesAsync(TcpHost)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        var tcpStart = sw.Elapsed;
        using (var tcp = new TcpClient(AddressFamily.InterNetwork))
            await tcp.ConnectAsync(tcpTarget, 443).WaitAsync(TimeSpan.FromSeconds(10));
        var tcpConnected = sw.Elapsed;

        int self = Environment.ProcessId;
        AppConnection? udpEvent = null, tcpEvent = null;
        TimeSpan udpSeen = default, tcpSeen = default;
        while (sw.Elapsed < TimeSpan.FromSeconds(10) && (udpEvent is null || tcpEvent is null))
        {
            foreach (var e in events.Where(e => e.ProcessId == self))
            {
                if (udpEvent is null && e.RemoteAddress.Equals(UdpTarget))
                    (udpEvent, udpSeen) = (e, sw.Elapsed);
                if (tcpEvent is null && e.RemoteAddress.Equals(tcpTarget))
                    (tcpEvent, tcpSeen) = (e, sw.Elapsed);
            }
            await Task.Delay(20);
        }

        output.WriteLine($"Наш процесс: {self} {Environment.ProcessPath}");
        output.WriteLine($"Событий ядра всего: {monitor.EventCount}, сообщено соединений: {events.Count}");
        foreach (var e in events.Take(10))
            output.WriteLine($"  {e.Protocol,2} {e.RemoteAddress}:{e.RemotePort}  pid {e.ProcessId}  {e.AppPath}");
        output.WriteLine($"UDP: {(udpEvent is null ? "нет события" : $"через {(udpSeen - udpSent).TotalMilliseconds:0} мс после отправки, порт {udpEvent.RemotePort}, {udpEvent.AppPath}")}");
        output.WriteLine($"TCP: {(tcpEvent is null ? "нет события" : $"через {(tcpSeen - tcpConnected).TotalMilliseconds:0} мс после соединения ({(tcpConnected - tcpStart).TotalMilliseconds:0} мс на соединение), порт {tcpEvent.RemotePort}, {tcpEvent.AppPath}")}");

        Assert.NotNull(udpEvent);
        Assert.NotNull(tcpEvent);
        Assert.Equal(Environment.ProcessPath, udpEvent.AppPath, ignoreCase: true);
        Assert.Equal(17, udpEvent.Protocol);
        Assert.Equal(6, tcpEvent.Protocol);
        Assert.Equal(40000, udpEvent.RemotePort);
        Assert.Equal(443, tcpEvent.RemotePort);
        // С принудительной выгрузкой буферов событие должно приходить быстрее секунды.
        Assert.True(udpSeen - udpSent < TimeSpan.FromSeconds(1), $"UDP-событие пришло через {(udpSeen - udpSent).TotalMilliseconds:0} мс");
    }

    /// <summary>Замер: сколько процессорного времени стоит наблюдение при потоке UDP-пакетов.</summary>
    [Fact]
    public async Task Measures_overhead_under_udp_flood()
    {
        if (!HasAdmin())
            return;

        const int packets = 50_000;
        var baseline = Flood(packets);

        using var monitor = new AppConnectionMonitor(_ => { });
        monitor.Start();
        await Task.Delay(2000);
        long before = monitor.EventCount;
        var withMonitor = Flood(packets);
        await Task.Delay(2000); // дождаться доставки буферов ETW
        long delivered = monitor.EventCount - before;

        output.WriteLine($"{packets} UDP-пакетов: без наблюдения {baseline.Cpu.TotalMilliseconds:0} мс CPU за {baseline.Wall.TotalMilliseconds:0} мс; " +
                         $"с наблюдением {withMonitor.Cpu.TotalMilliseconds:0} мс CPU за {withMonitor.Wall.TotalMilliseconds:0} мс");
        output.WriteLine($"событий доставлено: {delivered}; цена наблюдения ≈ {(withMonitor.Cpu - baseline.Cpu).TotalMilliseconds:0} мс CPU " +
                         $"(≈ {(withMonitor.Cpu - baseline.Cpu).TotalMicroseconds / Math.Max(1, delivered):0.00} мкс на событие)");
    }

    private static (TimeSpan Cpu, TimeSpan Wall) Flood(int packets)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var target = new IPEndPoint(UdpTarget, 40002);
        var payload = new byte[64];
        var process = Process.GetCurrentProcess();
        var cpu0 = process.TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < packets; i++)
            udp.Send(payload, payload.Length, target);
        sw.Stop();
        process.Refresh();
        return (process.TotalProcessorTime - cpu0, sw.Elapsed);
    }
}

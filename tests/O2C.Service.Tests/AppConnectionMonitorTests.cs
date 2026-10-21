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
}

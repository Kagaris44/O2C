using System.Collections.Concurrent;
using System.Net;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace O2C.Service.Tests;

public class DnsClientWatcherTests(ITestOutputHelper output)
{
    [Fact]
    public void Parses_event_xml()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System><Provider Name="Microsoft-Windows-DNS-Client" /><EventID>3008</EventID></System>
              <EventData>
                <Data Name="QueryName">WWW.Example.com</Data>
                <Data Name="QueryType">1</Data>
                <Data Name="QueryOptions">140737488355328</Data>
                <Data Name="QueryStatus">0</Data>
                <Data Name="QueryResults">type:  5 www.example.com-v4.edgesuite.net;::ffff:93.184.215.14;</Data>
              </EventData>
            </Event>
            """;
        var parsed = DnsClientWatcher.Parse(xml);
        Assert.NotNull(parsed);
        Assert.Equal("www.example.com", parsed.Value.Name);
        Assert.Equal([IPAddress.Parse("93.184.215.14")], parsed.Value.Addresses);
    }

    [Fact]
    public void Event_without_name_is_ignored()
    {
        Assert.Null(DnsClientWatcher.Parse("""<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><EventData /></Event>"""));
    }

    /// <summary>
    /// Настоящий журнал DNS-клиента: включаем, разрешаем имя и ждём событие с адресами.
    /// Нужны права администратора (в CI есть, и там тест обязан выполниться).
    /// </summary>
    [Fact]
    public async Task Sees_real_dns_resolution()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true", "В CI тест журнала DNS должен идти с правами администратора.");
            output.WriteLine("Пропущено: нужны права администратора.");
            return;
        }

        var seen = new ConcurrentDictionary<string, IReadOnlyList<IPAddress>>();
        using var watcher = new DnsClientWatcher(NullLogger.Instance, (name, addresses) => seen[name] = addresses);
        watcher.Start();

        // Случайное имя nip.io (отвечает адресом из самого имени) — гарантированно не из кэша.
        var name = $"o2c{Guid.NewGuid():N}.203.0.113.9.nip.io";
        var expected = IPAddress.Parse("203.0.113.9");
        IPAddress[] resolved;
        try
        {
            resolved = await Dns.GetHostAddressesAsync(name);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            output.WriteLine($"nip.io недоступен ({ex.SocketErrorCode}) — проверка по github.com");
            name = "github.com";
            resolved = await Dns.GetHostAddressesAsync(name);
            expected = resolved.First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        }
        Assert.NotEmpty(resolved);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!seen.ContainsKey(name) && DateTime.UtcNow < deadline)
            await Task.Delay(200);

        foreach (var (n, a) in seen)
            output.WriteLine($"{n} -> {string.Join(", ", a)}");
        var firstSeen = new Dictionary<string, IReadOnlyList<IPAddress>>(seen);

        // Для сведения: пишет ли Windows событие, когда ответ берётся из кэша.
        seen.Clear();
        await Dns.GetHostAddressesAsync(name);
        await Task.Delay(2000);
        output.WriteLine($"Повторный запрос из кэша: событие {(seen.ContainsKey(name) ? "есть" : "нет")}");

        Assert.True(firstSeen.TryGetValue(name, out var addresses), $"Событие для {name} не пришло");
        Assert.Contains(expected, addresses!);
    }
}

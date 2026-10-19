using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using O2C.Network;

namespace O2C.Service.Tests;

/// <summary>Таблица TCP и закрытие соединений — на собственном loopback-соединении с уникальными адресами.</summary>
public class TcpResetTests
{
    private static readonly IPAddress ServerAddress = IPAddress.Parse("127.77.0.1");
    private static readonly IPAddress ClientAddress = IPAddress.Parse("127.77.0.2");

    private static (TcpListener Listener, TcpClient Client, TcpClient Accepted) Connect()
    {
        var listener = new TcpListener(ServerAddress, 0);
        listener.Start();
        var client = new TcpClient(new IPEndPoint(ClientAddress, 0));
        client.Connect(ServerAddress, ((IPEndPoint)listener.LocalEndpoint).Port);
        var accepted = listener.AcceptTcpClient();
        return (listener, client, accepted);
    }

    [Fact]
    public void Tcp_table_contains_our_connection_with_correct_ports()
    {
        var (listener, client, accepted) = Connect();
        using (listener.Server)
        using (client)
        using (accepted)
        {
            var local = (IPEndPoint)client.Client.LocalEndPoint!;
            var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
            Assert.Contains(IpHelper.GetTcpConnections(), c =>
                c.LocalAddress.Equals(ClientAddress) && c.LocalPort == local.Port &&
                c.RemoteAddress.Equals(ServerAddress) && c.RemotePort == remote.Port);
            listener.Stop();
        }
    }

    [Fact]
    public void Reset_closes_only_matching_connections()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true", "В CI тест должен идти с правами администратора.");
            return;
        }

        var (listener, client, accepted) = Connect();
        using (listener.Server)
        using (client)
        using (accepted)
        {
            int reset = IpHelper.ResetConnections(new HashSet<IPAddress> { ClientAddress }, r => r.Equals(ServerAddress));
            Assert.Equal(1, reset);
            Assert.DoesNotContain(IpHelper.GetTcpConnections(), c =>
                c.LocalAddress.Equals(ClientAddress) && c.State == 5);
            // Соединение действительно разорвано: запись/чтение на стороне клиента падает.
            Assert.ThrowsAny<IOException>(() =>
            {
                var stream = client.GetStream();
                stream.Write(new byte[1]);
                stream.ReadByte();
            });
            listener.Stop();
        }
    }
}

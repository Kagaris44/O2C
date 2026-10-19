using O2C.Core.Ipc;
using O2C.Core.OpenConnect;

namespace O2C.Core.Tests;

public class CommandAndSettingsTests
{
    private static readonly ConnectionSettings Valid = new() { Server = "203.0.113.10", Username = "ivanov" };

    [Fact]
    public void Password_never_appears_in_arguments()
    {
        var args = OpenConnectCommand.BuildArguments(Valid, @"C:\oc\o2c-script.js");
        Assert.Contains("--passwd-on-stdin", args);
        Assert.Contains("--non-inter", args);
        Assert.Contains("--user=ivanov", args);
        Assert.Equal("203.0.113.10", args[^1]);
        Assert.Equal("--", args[^2]);
    }

    [Fact]
    public void Optional_arguments_are_added_only_when_set()
    {
        var plain = OpenConnectCommand.BuildArguments(Valid, "s.js");
        Assert.DoesNotContain(plain, a => a.StartsWith("--authgroup", StringComparison.Ordinal));
        Assert.DoesNotContain(plain, a => a.StartsWith("--servercert", StringComparison.Ordinal));

        var full = OpenConnectCommand.BuildArguments(Valid with { AuthGroup = "staff", ServerCertPin = "pin-sha256:abc=" }, "s.js");
        Assert.Contains("--authgroup=staff", full);
        Assert.Contains("--servercert=pin-sha256:abc=", full);
    }

    [Theory]
    [InlineData("", "ivanov")]
    [InlineData("-x", "ivanov")]
    [InlineData("gw example", "ivanov")]
    [InlineData("gw.example.com", "")]
    public void Invalid_settings_are_rejected(string server, string user)
    {
        var settings = new ConnectionSettings { Server = server, Username = user };
        Assert.NotNull(settings.Validate());
        Assert.Throws<ArgumentException>(() => OpenConnectCommand.BuildArguments(settings, "s.js"));
    }

    [Fact]
    public void Unknown_protocol_is_rejected()
    {
        Assert.NotNull((Valid with { Protocol = "ipsec" }).Validate());
    }

    [Fact]
    public void Ipc_message_round_trips()
    {
        var message = new IpcMessage
        {
            Id = 7,
            Command = IpcCommands.SaveSettings,
            SettingsUpdate = new SettingsUpdate { Settings = Valid, NewPassword = "secret" },
            Status = new ConnectionStatus { State = ConnectionState.Connected, AssignedIp = "10.0.0.2" },
        };
        var json = IpcProtocol.Serialize(message);
        Assert.DoesNotContain('\n', json);
        var back = IpcProtocol.Deserialize(json);
        Assert.NotNull(back);
        Assert.Equal(7, back.Id);
        Assert.Equal(Valid, back.SettingsUpdate!.Settings);
        Assert.Equal("secret", back.SettingsUpdate.NewPassword);
        Assert.Equal(ConnectionState.Connected, back.Status!.State);
    }

    [Fact]
    public async Task Oversized_frame_is_rejected()
    {
        var reader = new StringReader(new string('x', IpcProtocol.MaxFrameChars + 10) + "\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcProtocol.ReadFrameAsync(reader, CancellationToken.None));
    }

    [Fact]
    public void Backoff_grows_and_caps()
    {
        var backoff = new ReconnectBackoff();
        var delays = Enumerable.Range(0, 7).Select(_ => backoff.NextDelay().TotalSeconds).ToArray();
        Assert.Equal([5, 10, 20, 40, 60, 60, 60], delays);
        backoff.Reset();
        Assert.Equal(5, backoff.NextDelay().TotalSeconds);
    }
}

using O2C.Core;

namespace O2C.Service.Tests;

public class SettingsStoreTests
{
    private static readonly ConnectionSettings Base = new()
    {
        Server = "vpn.example.com",
        Username = "user",
        Protocol = "anyconnect",
        ServerCertPin = "pin-sha256:abc=",
    };

    [Fact]
    public void Password_kept_when_only_harmless_fields_change()
    {
        Assert.True(SettingsStore.KeepsPassword(Base, Base with { AutoReconnect = false, UseServerDns = true, AuthGroup = "staff" }));
        Assert.True(SettingsStore.KeepsPassword(Base, Base with { Server = " VPN.example.com " }));
    }

    [Theory]
    [InlineData("server")]
    [InlineData("username")]
    [InlineData("pin")]
    [InlineData("protocol")]
    public void Password_dropped_when_destination_or_identity_changes(string field)
    {
        var changed = field switch
        {
            "server" => Base with { Server = "evil.example.net" },
            "username" => Base with { Username = "other" },
            "pin" => Base with { ServerCertPin = "pin-sha256:zzz=" },
            _ => Base with { Protocol = "gp" },
        };
        Assert.False(SettingsStore.KeepsPassword(Base, changed));
    }
}

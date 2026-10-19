using O2C.Core.OpenConnect;

namespace O2C.Core.Tests;

public class OutputParserTests
{
    private static OpenConnectOutputParser Feed(params string[] lines)
    {
        var parser = new OpenConnectOutputParser();
        foreach (var line in lines)
            parser.ProcessLine(line);
        return parser;
    }

    [Fact]
    public void Configured_line_means_connected_and_gives_address()
    {
        var parser = new OpenConnectOutputParser();
        Assert.False(parser.ProcessLine("Connected to 203.0.113.10:443"));
        Assert.True(parser.ProcessLine("Configured as 10.10.0.5, with SSL + LZ4 connected and DTLS in progress"));
        Assert.True(parser.IsConnected);
        Assert.Equal("10.10.0.5", parser.AssignedAddress);
    }

    [Fact]
    public void Configured_line_with_ipv6_keeps_both_addresses()
    {
        var parser = Feed("Configured as 10.10.0.5 + fd00::5/64, with SSL connected and DTLS connected");
        Assert.Equal("10.10.0.5 fd00::5/64", parser.AssignedAddress);
    }

    [Fact]
    public void Untrusted_certificate_is_detected_with_pin()
    {
        var parser = Feed(
            "Server certificate verify failed: signer not found",
            "To trust this server in future, perhaps add this to your command line:",
            "    --servercert pin-sha256:AbCdEf0123456789+/xyz=",
            "Failed to complete authentication");
        Assert.Equal(FailureKind.Certificate, parser.ClassifyExit());
        Assert.Equal("pin-sha256:AbCdEf0123456789+/xyz=", parser.RejectedCertPin);
    }

    [Fact]
    public void Network_failure_before_auth_is_retryable()
    {
        var parser = Feed(
            "Failed to connect to host gw.example.com",
            "Failed to open HTTPS connection to gw.example.com",
            "Failed to complete authentication");
        Assert.Equal(FailureKind.Network, parser.ClassifyExit());
    }

    [Fact]
    public void Wrong_password_is_authentication_failure()
    {
        var parser = Feed(
            "POST https://gw.example.com/",
            "Login failed.",
            "User input required in non-interactive mode",
            "Failed to complete authentication");
        Assert.Equal(FailureKind.Authentication, parser.ClassifyExit());
    }

    [Fact]
    public void Bare_auth_incomplete_is_authentication_failure()
    {
        Assert.Equal(FailureKind.Authentication, Feed("Failed to complete authentication").ClassifyExit());
    }

    [Fact]
    public void Session_terminated_by_server()
    {
        var parser = Feed(
            "Configured as 10.10.0.5, with SSL connected and DTLS connected",
            "Session terminated by server; exiting.");
        Assert.Equal(FailureKind.SessionTerminated, parser.ClassifyExit());
    }

    [Fact]
    public void Unknown_exit_is_unknown()
    {
        Assert.Equal(FailureKind.Unknown, Feed("Unknown error; exiting.").ClassifyExit());
    }

    [Fact]
    public void Pinned_certificate_is_not_a_certificate_failure()
    {
        // Реальный журнал: отпечаток закреплён, проверка CA не прошла, но соединение установлено.
        var parser = Feed(
            "Server certificate verify failed: signer not found",
            "Connected to HTTPS on 203.0.113.10 with ciphersuite (TLS1.2)-(ECDHE-SECP256R1)-(RSA-SHA256)-(AES-256-GCM)",
            "Got CONNECT response: HTTP/1.1 200 CONNECTED",
            "Session terminated by server; exiting.");
        Assert.Equal(FailureKind.SessionTerminated, parser.ClassifyExit());
        Assert.Null(parser.RejectedCertPin);
    }

    [Fact]
    public void Adapter_failure_is_retryable_tunnel_setup()
    {
        // Реальный журнал 0.4.0: устаревшая запись адаптера в реестре.
        var parser = Feed(
            "Server certificate verify failed: signer not found",
            "Connected to HTTPS on 203.0.113.10 with ciphersuite (TLS1.2)-(ECDHE-SECP256R1)-(RSA-SHA256)-(AES-256-GCM)",
            "Configured as 10.10.0.231, with SSL + LZ4 connected and DTLS + LZ4 in progress",
            "Could not open Wintun adapter 'O2C': not found.",
            "Set up tun device failed",
            "Send BYE packet: Set up tun device failed",
            "Cookie was rejected by server; exiting.");
        Assert.Equal(FailureKind.TunnelSetup, parser.ClassifyExit());
    }
}

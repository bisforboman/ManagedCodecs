using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ManagedCodecs.Playback;
using ManagedCodecs.Playback.Rtsp;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// RTSPS (docs/decisions/2026-11-15-live-rtsps.md): LivePlayer over TLS against the loopback RTSP server with a
/// self-signed certificate made here, the recorded RTP inside the TLS connection; the certificate pinned by its SHA-256,
/// refused without a pin or with another one; a 461 over TLS ends with the SRTP error and no UDP; rtsps' default port.
/// </summary>
public class LiveRtspsTests
{
    private const string Slices = "h264-main-slices";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public void PlaysOverTlsWithThePinnedCertificate()
    {
        using var certificate = SelfSigned("localhost");
        var packets = RtpH264DepacketizerTests.Recording(Slices);
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices), async play =>
        {
            foreach (var packet in packets)
            {
                await play.SendAsync(packet);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        })
        {
            Certificate = certificate,
        };

        Assert.Equal("rtsps", server.Uri.Scheme);
        var source = new RtspSource(server.Uri) { CertificateHash = Hash(certificate), StallTimeout = TimeSpan.FromMinutes(10) };
        using var player = new LivePlayer(source);
        player.Start();

        // Every unit through: the frames the RTP-over-TCP tests check one by one, here inside TLS.
        Until(player, () => player.Statistics.ReceivedFrames >= 20 && player.Statistics.DecodedFrames >= 20);
        Assert.NotNull(player.TakeLatest());
        Assert.Equal(LiveState.Playing, player.State);
        Assert.Equal((0L, 0L), (player.Statistics.LostPackets, player.Statistics.DamagedFrames));
        Assert.Null(player.Error);
        Assert.Equal(1, server.Connections);
    }

    [Theory]
    [InlineData(false)] // no pin: the system refuses a self-signed certificate
    [InlineData(true)] // a pin of another certificate
    public void RefusesACertificateItWasNotGiven(bool otherPin)
    {
        using var certificate = SelfSigned("localhost");
        using var other = SelfSigned("localhost");
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices), play => Task.Delay(Timeout.Infinite, play.Token))
        {
            Certificate = certificate,
        };

        var clock = new FakeLiveClock();
        var source = new RtspSource(server.Uri) { CertificateHash = otherPin ? Hash(other) : null };
        using var player = new LivePlayer(source, clock);
        player.Start();
        Until(player, () => player.State == LiveState.Failed);
        Assert.IsType<AuthenticationException>(player.Error);
        Assert.Empty(server.Requests);

        // Final: a certificate doesn't change on the next try.
        clock.Advance(TimeSpan.FromMinutes(1));
        Thread.Sleep(20);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public void FailsWhenTheServerWantsUdpAndOpensNoPorts()
    {
        using var certificate = SelfSigned("localhost");
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices), play => Task.Delay(Timeout.Infinite, play.Token))
        {
            Certificate = certificate,
            UdpOnly = true,
        };

        using var player = new LivePlayer(new RtspSource(server.Uri) { CertificateHash = Hash(certificate) }, new FakeLiveClock());
        player.Start();
        Until(player, () => player.State == LiveState.Failed);
        var error = Assert.IsType<NotSupportedException>(player.Error);
        Assert.Contains("SRTP", error.Message, StringComparison.Ordinal);
        Assert.Single(server.Requests, r => r.Method == "SETUP"); // no second SETUP asking for plain UDP
    }

    [Fact]
    public void TakesRtspsUrlsAndTheirPortAndAHashAsBrowsersShowIt()
    {
        Assert.Equal(322, new RtspSource(new Uri("rtsps://camera/stream")).Port);
        Assert.Equal(554, new RtspSource(new Uri("rtsp://camera/stream")).Port);
        Assert.Equal(8322, new RtspSource(new Uri("rtsps://camera:8322/stream")).Port);
        Assert.True(new RtspSource(new Uri("RTSPS://camera/stream")).Secure);
        Assert.Throws<ArgumentException>(() => new RtspSource(new Uri("https://camera/stream")));

        string hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string colons = string.Join(':', Enumerable.Range(0, 32).Select(i => hex.Substring(2 * i, 2).ToLowerInvariant()));
        var source = new RtspSource(new Uri("rtsps://camera/stream")) { CertificateHash = colons };
        Assert.Equal(Convert.FromHexString(hex), source.PinnedCertificate);
        Assert.Equal(colons, source.CertificateHash);
        Assert.Throws<ArgumentException>(() => new RtspSource(new Uri("rtsps://camera/stream")) { CertificateHash = hex[..62] });
        Assert.Throws<ArgumentException>(() => new RtspSource(new Uri("rtsps://camera/stream")) { CertificateHash = "zz" + hex[2..] });
    }

    /// <summary>The certificate check itself: a pin accepts its certificate whatever the system says, and nothing else.</summary>
    [Fact]
    public void APinAcceptsItsCertificateOnly()
    {
        using var certificate = SelfSigned("camera.local");
        using var other = SelfSigned("camera.local");
        byte[] pin = SHA256.HashData(certificate.RawData);
        const SslPolicyErrors errors = SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch;
        Assert.True(RtspConnection.Accepts(certificate, errors, pin));
        Assert.False(RtspConnection.Accepts(other, SslPolicyErrors.None, pin));
        Assert.False(RtspConnection.Accepts(null, SslPolicyErrors.RemoteCertificateNotAvailable, pin));
        Assert.True(RtspConnection.Accepts(certificate, SslPolicyErrors.None, null));
        Assert.False(RtspConnection.Accepts(certificate, SslPolicyErrors.RemoteCertificateChainErrors, null));
    }

    internal static string Hash(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    // A self-signed certificate for `name` (and 127.0.0.1), with its private key, as a camera has.
    internal static X509Certificate2 SelfSigned(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(name);
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }

    private static void Until(LivePlayer player, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out: {player.State}, {player.Error?.Message}");
            Thread.Sleep(5);
        }
    }
}

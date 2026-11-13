using System.Net;
using ManagedCodecs.Playback.Rtsp;

namespace ManagedCodecs.Tests.Playback;

/// <summary>The RTSP client's parts on their own: Digest by RFC 2617's worked example, challenges, SDP and control URLs.</summary>
public class RtspProtocolTests
{
    [Fact]
    public void DigestMatchesRfc2617sExample()
    {
        // RFC 2617, 3.5: Mufasa, "Circle Of Life", GET /dir/index.html, qop=auth.
        Assert.Equal(
            "6629fae49393a05397450978507c4ef1",
            RtspAuthenticator.DigestResponse("Mufasa", "testrealm@host.com", "Circle Of Life", "GET", "/dir/index.html", "dcd98b7102dd2f0e8b11d0f600bfb0c093", "00000001", "0a4f113b"));
    }

    [Fact]
    public void PrefersDigestToBasicAndCountsItsRequests()
    {
        var credential = new NetworkCredential("user", "pass");
        var authenticator = RtspAuthenticator.From(["Basic realm=\"x\"", "Digest realm=\"r\", nonce=\"n\", qop=\"auth,auth-int\""], credential)!;
        string first = authenticator.Header("DESCRIBE", "rtsp://h/s");
        string second = authenticator.Header("SETUP", "rtsp://h/s/t");
        Assert.StartsWith("Digest username=\"user\", realm=\"r\", nonce=\"n\", uri=\"rtsp://h/s\"", first, StringComparison.Ordinal);
        Assert.Contains("nc=00000001", first, StringComparison.Ordinal);
        Assert.Contains("nc=00000002", second, StringComparison.Ordinal);
    }

    [Fact]
    public void FallsBackToBasicAndRefusesWhatItCantAnswer()
    {
        var credential = new NetworkCredential("user", "pa:ss");
        Assert.Equal("Basic dXNlcjpwYTpzcw==", RtspAuthenticator.From(["Digest realm=\"r\", nonce=\"n\", algorithm=SHA-256", "Basic realm=\"x\""], credential)!.Header("OPTIONS", "rtsp://h/"));
        Assert.Null(RtspAuthenticator.From(["Digest realm=\"r\", nonce=\"n\", algorithm=SHA-256"], credential));
        Assert.Null(RtspAuthenticator.From(["Negotiate"], credential));
    }

    [Fact]
    public void FindsTheFirstH264VideoTrack()
    {
        var sdp = Sdp.Parse(
            "v=0\r\ns=-\r\na=control:*\r\n"
            + "m=audio 0 RTP/AVP 97\r\na=rtpmap:97 PCMU/8000\r\na=control:trackID=0\r\n"
            + "m=video 0 RTP/AVP 98 96\r\na=rtpmap:98 H265/90000\r\na=rtpmap:96 h264/90000\r\n"
            + "a=fmtp:96 profile-level-id=42e01f; packetization-mode=1; sprop-parameter-sets=Z0IAHpZUBQHtgKA=,aM48gA==\r\na=control:trackID=1\r\n");
        Assert.Equal("*", sdp.SessionControl);
        var track = sdp.Track!;
        Assert.Equal((96, "trackID=1", 1), (track.PayloadType, track.Control, track.PacketizationMode));
        Assert.Equal(2, track.ParameterSets.Length);
        Assert.Equal(0x67, track.ParameterSets[0][0]);
    }

    [Theory]
    [InlineData("m=video 0 RTP/AVP 26\n", "Jpeg", 26)] // JPEG's static payload type needs no a=rtpmap
    [InlineData("m=video 0 RTP/AVP 97\na=rtpmap:97 jpeg/90000\n", "Jpeg", 97)]
    [InlineData("m=video 0 RTP/AVP 26 96\na=rtpmap:96 H264/90000\n", "Jpeg", 26)] // the first in SDP order (decision 3)
    [InlineData("m=video 0 RTP/AVP 96 26\na=rtpmap:96 H264/90000\n", "H264", 96)]
    [InlineData("m=video 0 RTP/AVP 96\na=rtpmap:96 H264/90000\nm=video 0 RTP/AVP 26\n", "H264", 96)]
    [InlineData("m=video 0 RTP/AVP 26\na=rtpmap:26 H265/90000\nm=video 0 RTP/AVP 26\n", "Jpeg", 26)] // 26 mapped to another codec isn't JPEG
    public void ChoosesTheFirstVideoTrackItCanPlay(string media, string codec, int payloadType)
    {
        var track = Sdp.Parse("v=0\n" + media).Track!;
        Assert.Equal((codec, payloadType), (track.Codec.ToString(), track.PayloadType));
    }

    [Fact]
    public void ToleratesParameterSetsThatArentBase64()
    {
        var track = Sdp.Parse("m=video 0 RTP/AVP 96\na=rtpmap:96 H264/90000\na=fmtp:96 sprop-parameter-sets=@@@\n").Track!;
        Assert.Empty(track.ParameterSets);
        Assert.Equal(0, track.PacketizationMode);
    }

    [Theory]
    [InlineData("rtsp://h/s/", "trackID=1", "rtsp://h/s/trackID=1")]
    [InlineData("rtsp://h/s", "trackID=1", "rtsp://h/s/trackID=1")] // no slash in the base: joined by one, as FFmpeg does
    [InlineData("rtsp://h/s/", "rtsp://other/x", "rtsp://other/x")]
    [InlineData("rtsp://h/s/", "*", "rtsp://h/s/")]
    [InlineData("rtsp://h/s/", null, "rtsp://h/s/")]
    public void ResolvesControlUrls(string baseUrl, string? control, string resolved) => Assert.Equal(resolved, Sdp.Resolve(baseUrl, control));
}

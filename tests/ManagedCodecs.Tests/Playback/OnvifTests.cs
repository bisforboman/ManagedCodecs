using System.Net;
using System.Xml.Linq;
using ManagedCodecs.Playback;
using ManagedCodecs.Playback.Onvif;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// ONVIF discovery and stream URIs (issue #527; docs/decisions/2026-11-27-live-onvif.md) against
/// <see cref="OnvifTestDevice"/>: the probe and its answers' rules (types, message IDs, merging), the media service
/// found by GetServices or GetCapabilities, and HTTP Digest or the WS-Security UsernameToken dated by the device's clock.
/// </summary>
public class OnvifTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(1.5);
    private static readonly NetworkCredential Credential = new(OnvifTestDevice.User, OnvifTestDevice.Password);

    [Fact]
    public async Task FindsTheDeviceWithItsNameHardwareAndAddress()
    {
        using var device = new OnvifTestDevice();
        var found = await Probe(device);

        var camera = Assert.Single(found);
        Assert.Equal(device.Endpoint, camera.Endpoint);
        Assert.Equal("Front door", camera.Name);
        Assert.Equal("IPC-1234", camera.Hardware);
        Assert.Equal(device.DeviceService, camera.Addresses[0]);
        Assert.Contains("onvif://www.onvif.org/location/country/sweden", camera.Scopes);
    }

    [Fact]
    public async Task ProbesForEachTypeSeparatelyAndRepeatsEachProbeWithItsOwnId()
    {
        // A device listing only dn:NetworkVideoTransmitter (older cameras) matches only the probe naming that type alone.
        using var device = new OnvifTestDevice { Types = ["dn:NetworkVideoTransmitter"] };
        var found = await Probe(device);

        Assert.Single(found);
        lock (device.ProbeIds)
        {
            // Two probes, each sent three times (SOAP over UDP's repetition) with the same message ID.
            Assert.Equal(6, device.ProbeIds.Count);
            Assert.All(device.ProbeIds.GroupBy(id => id), g => Assert.Equal(3, g.Count()));
        }
    }

    [Fact]
    public async Task MergesTheAnswersOfOneDevice()
    {
        // The device answers both probes, three times each: one device.
        using var device = new OnvifTestDevice();
        Assert.Single(await Probe(device));
    }

    [Fact]
    public async Task IgnoresAnswersToAnotherProbe()
    {
        using var device = new OnvifTestDevice { WrongRelatesTo = "urn:uuid:" + Guid.NewGuid() };
        Assert.Empty(await Probe(device));
    }

    [Fact]
    public async Task IgnoresAnswersOfOtherDeviceTypes()
    {
        // A printer that answers probes for types it doesn't have, against WS-Discovery 5.1.
        using var device = new OnvifTestDevice { Types = ["i:PrintBasic"], AnswersAnyProbe = true };
        var found = await OnvifDiscovery.ProbeAsync(Wait, device.DiscoveryEndPoint, [IPAddress.Loopback], CancellationToken.None);
        Assert.Empty(found);
    }

    [Fact]
    public async Task IgnoresDatagramsThatAreNotXml()
    {
        using var device = new OnvifTestDevice { SendsGarbage = true };
        Assert.Single(await Probe(device));
    }

    [Fact]
    public async Task ListsTheAddressItAnsweredFromFirst()
    {
        using var device = new OnvifTestDevice { ExtraAddresses = ["http://192.0.2.7/onvif/device_service", "https://[2001:db8::7]/onvif/device_service"], OwnAddressLast = true };
        var camera = Assert.Single(await Probe(device));
        Assert.Equal(3, camera.Addresses.Count);
        Assert.Equal(device.DeviceService, camera.Addresses[0]);
    }

    [Fact]
    public async Task FindsTheDeviceThroughTheMulticastGroup()
    {
        // The public method: the probe to 239.255.255.250:3702 from every interface, loopback among them (the cloud
        // sandbox's and CI's loopback carry multicast, as LiveRtspMulticastTests relies on).
        using var device = new OnvifTestDevice(multicast: true);
        var found = await OnvifDiscovery.FindAsync(Wait);
        Assert.Contains(found, d => d.Endpoint == device.Endpoint);
    }

    [Fact]
    public async Task EndsAtCancellationWithWhatHasAnswered()
    {
        using var device = new OnvifTestDevice();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var started = DateTime.UtcNow;
        var found = await OnvifDiscovery.ProbeAsync(TimeSpan.FromMinutes(5), device.DiscoveryEndPoint, [IPAddress.Loopback], cancel.Token);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
        Assert.Single(found);
    }

    [Fact]
    public void RefusesANegativeWait()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = OnvifDiscovery.FindAsync(TimeSpan.FromSeconds(-1)); });
    }

    [Fact]
    public async Task GetsEachProfilesStreamWithHttpDigest()
    {
        using var device = new OnvifTestDevice { Auth = OnvifAuth.Digest };
        var streams = await new OnvifDevice(device.DeviceService).GetStreamsAsync(Credential);

        Assert.Equal(3, streams.Count);
        Assert.Equal("Profile_1", streams[0].Profile);
        Assert.Equal("mainStream", streams[0].Name);
        Assert.Equal("H264", streams[0].Encoding);
        Assert.Equal((1920, 1080), (streams[0].Width, streams[0].Height));
        Assert.Equal(new Uri("rtsp://127.0.0.1:554/Streaming/Channels/101?transportmode=unicast&profile=Profile_1"), streams[0].Uri);
        Assert.Equal(("JPEG", 640, 360), (streams[1].Encoding, streams[1].Width, streams[1].Height));
        Assert.Null(streams[2].Encoding); // a profile without a video encoder
        Assert.Equal(0, streams[2].Width);
        lock (device.Calls)
        {
            Assert.DoesNotContain("GetSystemDateAndTime", device.Calls); // no token, so no need of the device's clock
        }
    }

    [Fact]
    public async Task GetsTheStreamsOfADeviceFoundByDiscovery()
    {
        using var device = new OnvifTestDevice();
        var camera = Assert.Single(await Probe(device));
        var streams = await camera.GetStreamsAsync(Credential);
        Assert.Equal(3, streams.Count);
    }

    [Fact]
    public async Task UsesTheUsernameTokenWhereTheDeviceTakesNothingElse()
    {
        using var device = new OnvifTestDevice { Auth = OnvifAuth.UsernameToken };
        var streams = await new OnvifDevice(device.DeviceService).GetStreamsAsync(Credential);
        Assert.Equal(3, streams.Count);
    }

    [Fact]
    public async Task DatesTheUsernameTokenByTheDevicesClock()
    {
        // A camera without NTP, five hours behind: a token dated by this machine's clock is refused.
        using var device = new OnvifTestDevice { Auth = OnvifAuth.UsernameToken, ClockOffset = TimeSpan.FromHours(-5) };
        var streams = await new OnvifDevice(device.DeviceService).GetStreamsAsync(Credential);
        Assert.Equal(3, streams.Count);
        lock (device.Calls)
        {
            Assert.Single(device.Calls, c => c == "GetSystemDateAndTime"); // asked once, for every token after
        }
    }

    [Fact]
    public async Task FindsTheMediaServiceByGetCapabilitiesOnOlderDevices()
    {
        using var device = new OnvifTestDevice { NoGetServices = true, MediaPath = "/onvif/Media" };
        var streams = await new OnvifDevice(device.DeviceService).GetStreamsAsync(Credential);
        Assert.Equal(3, streams.Count);
        lock (device.Calls)
        {
            Assert.Contains("GetCapabilities", device.Calls);
        }
    }

    [Theory]
    [InlineData(OnvifAuth.Digest)]
    [InlineData(OnvifAuth.UsernameToken)]
    public async Task ThrowsOnAWrongPassword(OnvifAuth auth)
    {
        using var device = new OnvifTestDevice { Auth = auth };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new OnvifDevice(device.DeviceService).GetStreamsAsync(new NetworkCredential(OnvifTestDevice.User, "wrong")));
    }

    [Theory]
    [InlineData(OnvifAuth.Digest)]
    [InlineData(OnvifAuth.UsernameToken)]
    public async Task ThrowsWhenCredentialsAreAskedForAndNoneGiven(OnvifAuth auth)
    {
        using var device = new OnvifTestDevice { Auth = auth };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new OnvifDevice(device.DeviceService).GetStreamsAsync(null));
    }

    [Fact]
    public async Task NeedsNoCredentialsWhereTheDeviceAsksForNone()
    {
        using var device = new OnvifTestDevice { Auth = OnvifAuth.None };
        Assert.Equal(3, (await new OnvifDevice(device.DeviceService).GetStreamsAsync(null)).Count);
    }

    [Fact]
    public async Task TriesTheNextAddressWhenOneDoesNotAnswer()
    {
        using var device = new OnvifTestDevice();
        var camera = new OnvifDevice(null, [new Uri("http://127.0.0.1:1/onvif/device_service"), device.DeviceService], []);
        Assert.Equal(3, (await camera.GetStreamsAsync(Credential)).Count);
    }

    [Fact]
    public void RefusesAnAddressThatIsNotHttp()
    {
        Assert.Throws<ArgumentException>(() => new OnvifDevice(new Uri("rtsp://camera/stream")));
    }

    [Fact]
    public void UsernameTokenDigestIsTheProfilesFormula()
    {
        // UsernameToken Profile 1.0, 3.1: Base64(SHA-1(nonce + created + password)), the nonce as its octets. The
        // expected value was computed separately (Python's hashlib) for these inputs.
        var token = OnvifSoap.UsernameToken(new NetworkCredential("admin", "Secret 42"), new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
        XNamespace wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
        XNamespace wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
        Assert.Equal("2026-11-27T12:00:00.000Z", token.Descendants(wsu + "Created").Single().Value);
        Assert.Equal("AQIDBAUGBwgJCgsMDQ4PEA==", token.Descendants(wsse + "Nonce").Single().Value);
        Assert.Equal(ExpectedDigest, token.Descendants(wsse + "Password").Single().Value);
    }

    [Fact]
    public void RefusesDocumentsWithADocumentTypeDeclaration()
    {
        // An entity expansion (a "billion laughs") in a datagram: not parsed.
        var hostile = "<?xml version=\"1.0\"?><!DOCTYPE a [<!ENTITY b \"bbbbbbbb\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;\">]><a>&c;</a>"u8.ToArray();
        Assert.Null(OnvifSoap.TryParse(hostile, hostile.Length));
    }

    // python3 -c "import base64,hashlib; print(base64.b64encode(hashlib.sha1(bytes(range(1,17)) + b'2026-11-27T12:00:00.000Z' + b'Secret 42').digest()).decode())"
    private const string ExpectedDigest = "magmYkMovHC/7LZAAjrTgNffQ9M=";

    private static Task<IReadOnlyList<OnvifDevice>> Probe(OnvifTestDevice device) =>
        OnvifDiscovery.ProbeAsync(Wait, device.DiscoveryEndPoint, [IPAddress.Loopback], CancellationToken.None);
}

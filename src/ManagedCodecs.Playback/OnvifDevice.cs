using System.Globalization;
using System.Net;
using System.Xml.Linq;
using ManagedCodecs.Playback.Onvif;

namespace ManagedCodecs.Playback;

/// <summary>
/// An ONVIF camera (or video encoder or recorder): what it said when <see cref="OnvifDiscovery.FindAsync"/> found it,
/// or one at a known address. <see cref="GetStreamsAsync"/> asks it for its streams' RTSP URLs.
/// docs/decisions/2026-11-27-live-onvif.md.
/// </summary>
public sealed class OnvifDevice
{
    private const string NameScope = "onvif://www.onvif.org/name/";
    private const string HardwareScope = "onvif://www.onvif.org/hardware/";

    /// <summary>A camera at a known address, where discovery can't reach it (another subnet, a VPN, a NAT).</summary>
    /// <param name="address">
    /// Its ONVIF device service, an absolute http or https URL; most cameras serve it at
    /// <c>http://&lt;host&gt;/onvif/device_service</c>.
    /// </param>
    /// <exception cref="ArgumentException">Not an absolute http or https URL.</exception>
    public OnvifDevice(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!IsHttp(address))
        {
            throw new ArgumentException("Not an absolute http or https URL.", nameof(address));
        }

        Addresses = [address];
        Scopes = [];
    }

    internal OnvifDevice(string? endpoint, IReadOnlyList<Uri> addresses, IReadOnlyList<string> scopes)
    {
        Endpoint = endpoint;
        Addresses = addresses;
        Scopes = scopes;
    }

    /// <summary>
    /// The camera's endpoint reference, stable across its addresses and restarts (most often <c>urn:uuid:...</c>); null
    /// for one made from an address.
    /// </summary>
    public string? Endpoint { get; }

    /// <summary>The addresses of its ONVIF device service, the one it answered from first.</summary>
    public IReadOnlyList<Uri> Addresses { get; }

    /// <summary>
    /// Its scopes (ONVIF Core 7.3.2.2): URIs such as <c>onvif://www.onvif.org/name/Front_door</c>,
    /// <c>onvif://www.onvif.org/hardware/DS-2CD2043G2</c>, <c>onvif://www.onvif.org/location/...</c>; empty for one made
    /// from an address.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>The name its first <c>name</c> scope gives, unescaped; null without one.</summary>
    public string? Name => Scope(NameScope);

    /// <summary>The hardware its first <c>hardware</c> scope gives (most often the model), unescaped; null without one.</summary>
    public string? Hardware => Scope(HardwareScope);

    /// <summary>
    /// Its media profiles and each one's RTSP URL (ONVIF Media: GetProfiles, then GetStreamUri for RTP over RTSP), from
    /// the first of its <see cref="Addresses"/> that answers.
    /// </summary>
    /// <param name="credential">
    /// A user of the camera's, or null for one that asks for none: HTTP Digest, else (a camera that refuses it) the
    /// WS-Security UsernameToken. The streams most often take the same credentials (<see cref="RtspSource"/>).
    /// </param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <exception cref="UnauthorizedAccessException">The camera refused the credentials, or asked for some and none were given.</exception>
    /// <exception cref="HttpRequestException">No address answered, or one answered with an error.</exception>
    /// <exception cref="InvalidDataException">An answer that isn't ONVIF's.</exception>
    public async Task<IReadOnlyList<OnvifStream>> GetStreamsAsync(NetworkCredential? credential, CancellationToken cancellationToken = default)
    {
        HttpRequestException? failure = null;
        foreach (var address in Addresses)
        {
            try
            {
                return await GetStreamsFromAsync(address, credential, null, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                failure = e; // the next address, if there is one
            }
        }

        throw failure ?? new HttpRequestException("The camera has no device service address.");
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name ?? Endpoint ?? "ONVIF device"} ({string.Join(", ", Addresses)})";

    /// <summary>The streams from one device service address; <paramref name="handler"/> for tests.</summary>
    internal static async Task<IReadOnlyList<OnvifStream>> GetStreamsFromAsync(Uri address, NetworkCredential? credential, HttpMessageHandler? handler, CancellationToken cancellationToken)
    {
        using var client = new OnvifClient(address, credential, handler);
        var media = await MediaServiceAsync(client, address, cancellationToken).ConfigureAwait(false);
        var profiles = await client.CallAsync(media, "http://www.onvif.org/ver10/media/wsdl/GetProfiles", new XElement(OnvifSoap.Media + "GetProfiles"), cancellationToken).ConfigureAwait(false);
        var streams = new List<OnvifStream>();
        foreach (var profile in OnvifSoap.Children(profiles, "Profiles"))
        {
            string? token = profile.Attribute("token")?.Value;
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            var request = new XElement(
                OnvifSoap.Media + "GetStreamUri",
                new XElement(
                    OnvifSoap.Media + "StreamSetup",
                    new XElement(OnvifSoap.Schema + "Stream", "RTP-Unicast"),
                    new XElement(OnvifSoap.Schema + "Transport", new XElement(OnvifSoap.Schema + "Protocol", "RTSP"))),
                new XElement(OnvifSoap.Media + "ProfileToken", token));
            var answer = await client.CallAsync(media, "http://www.onvif.org/ver10/media/wsdl/GetStreamUri", request, cancellationToken).ConfigureAwait(false);
            if (!Uri.TryCreate(OnvifSoap.Find(answer, "Uri")?.Value.Trim(), UriKind.Absolute, out var uri))
            {
                throw new InvalidDataException($"The camera's stream URI for profile {token} is not an absolute URI.");
            }

            var encoder = OnvifSoap.Children(profile, "VideoEncoderConfiguration").FirstOrDefault();
            var resolution = OnvifSoap.Children(encoder, "Resolution").FirstOrDefault();
            string name = OnvifSoap.Children(profile, "Name").FirstOrDefault()?.Value.Trim() is { Length: > 0 } n ? n : token!;
            string? encoding = OnvifSoap.Children(encoder, "Encoding").FirstOrDefault()?.Value.Trim();
            streams.Add(new OnvifStream(token!, name, uri, string.IsNullOrEmpty(encoding) ? null : encoding, Number(resolution, "Width"), Number(resolution, "Height")));
        }

        return streams;
    }

    internal static bool IsHttp(Uri uri) => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// The media service's address: GetServices' service in Media's namespace, else (older devices) GetCapabilities'
    /// Media, else the device service itself (ONVIF Core 8.1.2).
    /// </summary>
    private static async Task<Uri> MediaServiceAsync(OnvifClient client, Uri device, CancellationToken cancellationToken)
    {
        try
        {
            var services = await client.CallAsync(device, "http://www.onvif.org/ver10/device/wsdl/GetServices", new XElement(OnvifSoap.Device + "GetServices", new XElement(OnvifSoap.Device + "IncludeCapability", "false")), cancellationToken).ConfigureAwait(false);
            foreach (var service in OnvifSoap.Children(services, "Service"))
            {
                if (OnvifSoap.Children(service, "Namespace").FirstOrDefault()?.Value.Trim() == OnvifSoap.Media.NamespaceName
                    && Address(OnvifSoap.Children(service, "XAddr").FirstOrDefault()) is { } media)
                {
                    return media;
                }
            }
        }
        catch (HttpRequestException)
        {
            // No GetServices (a fault): GetCapabilities below.
        }

        try
        {
            var capabilities = await client.CallAsync(device, "http://www.onvif.org/ver10/device/wsdl/GetCapabilities", new XElement(OnvifSoap.Device + "GetCapabilities", new XElement(OnvifSoap.Device + "Category", "Media")), cancellationToken).ConfigureAwait(false);
            if (Address(OnvifSoap.Children(OnvifSoap.Find(capabilities, "Media"), "XAddr").FirstOrDefault()) is { } media)
            {
                return media;
            }
        }
        catch (HttpRequestException)
        {
            // Neither: the device service itself, which some devices serve every service at.
        }

        return device;
    }

    private static Uri? Address(XElement? element) =>
        Uri.TryCreate(element?.Value.Trim(), UriKind.Absolute, out var uri) && IsHttp(uri) ? uri : null;

    private static int Number(XElement? parent, string name) =>
        int.TryParse(OnvifSoap.Children(parent, name).FirstOrDefault()?.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private string? Scope(string prefix)
    {
        foreach (var scope in Scopes)
        {
            if (scope.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && scope.Length > prefix.Length)
            {
                return Uri.UnescapeDataString(scope.Substring(prefix.Length));
            }
        }

        return null;
    }
}

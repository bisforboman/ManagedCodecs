namespace ManagedCodecs.Playback;

/// <summary>
/// One media profile of an ONVIF camera (<see cref="OnvifDevice.GetStreamsAsync"/>): a configuration of its video
/// (and often sound) the camera streams over RTSP, such as a main stream and a smaller substream.
/// </summary>
public sealed class OnvifStream
{
    internal OnvifStream(string profile, string name, Uri uri, string? encoding, int width, int height)
    {
        Profile = profile;
        Name = name;
        Uri = uri;
        Encoding = encoding;
        Width = width;
        Height = height;
    }

    /// <summary>The profile's token, the camera's name for it in ONVIF requests.</summary>
    public string Profile { get; }

    /// <summary>The profile's name as the camera shows it (<c>MainStream</c>, <c>Profile_1</c>, ...); the token when it has none.</summary>
    public string Name { get; }

    /// <summary>
    /// The stream's RTSP URL, for <see cref="RtspSource"/> with the same credentials. Some cameras put the credentials
    /// it needs into it; some, behind a NAT, give their local address.
    /// </summary>
    public Uri Uri { get; }

    /// <summary>
    /// The video encoding the profile's encoder is set to, as the camera names it (<c>H264</c>, <c>JPEG</c>, <c>H265</c>,
    /// <c>MPEG4</c>), or null when the profile has no video encoder. <see cref="LivePlayer"/> plays H264 and JPEG.
    /// </summary>
    public string? Encoding { get; }

    /// <summary>The encoder's width in pixels, or 0 when the profile doesn't say.</summary>
    public int Width { get; }

    /// <summary>The encoder's height in pixels, or 0 when the profile doesn't say.</summary>
    public int Height { get; }

    /// <inheritdoc/>
    public override string ToString() => Width > 0 ? $"{Name}: {Encoding} {Width}x{Height}, {Uri}" : $"{Name}: {Encoding}, {Uri}";
}

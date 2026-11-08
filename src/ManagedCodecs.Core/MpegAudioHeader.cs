namespace ManagedCodecs;

/// <summary>
/// The header of an MPEG audio Layer III frame (ISO/IEC 11172-3 2.4.2.3, with ISO/IEC 13818-3's lower sampling
/// frequencies and MPEG-2.5's, a widespread extension, at half those again): what the raw MP3 demuxer reads frames by.
/// </summary>
/// <param name="Version">1 for MPEG-1, 2 for MPEG-2 (lower sampling frequencies), 25 for MPEG-2.5.</param>
/// <param name="HasCrc">Whether a CRC follows the header (protection_bit 0).</param>
/// <param name="Bitrate">Bits per second.</param>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Mode">0 stereo, 1 joint stereo, 2 dual channel, 3 single channel.</param>
/// <param name="FrameLength">The frame's length in bytes, this header included.</param>
/// <param name="Raw">The header's four bytes, big-endian.</param>
internal readonly record struct MpegAudioHeader(
    int Version,
    bool HasCrc,
    int Bitrate,
    int SampleRate,
    int Mode,
    int FrameLength,
    uint Raw)
{
    /// <summary>The header's length in bytes, without the CRC.</summary>
    public const int Size = 4;

    // What two frames of one stream share as FFmpeg's raw MP3 demuxer tells its first frame from junk (by what it does:
    // a first frame that differs from the next in any of these is not a packet): the sync word, version, layer and
    // sampling frequency, and the mode, copyright, original and emphasis. Not the protection bit, bitrate, padding,
    // private bit or mode extension, which vary between frames.
    private const uint StreamMask = 0xFFFE0CCF;

    /// <summary>The channels: 1 for single channel, else 2.</summary>
    public int Channels => Mode == 3 ? 1 : 2;

    /// <summary>Samples per channel in the frame: 1152 for MPEG-1, 576 for the lower sampling frequencies (2.4.2.1).</summary>
    public int SamplesPerFrame => Version == 1 ? 1152 : 576;

    /// <summary>
    /// The side information's length in bytes (2.4.1.7; 13818-3 2.4.1.7): 32 or 17 (single channel) for MPEG-1, 17 or 9
    /// for the lower sampling frequencies. It follows the header and the CRC.
    /// </summary>
    public int SideInfoLength => Version == 1 ? (Channels == 1 ? 17 : 32) : (Channels == 1 ? 9 : 17);

    // Table 2.4.2.3's bitrate_index for Layer III in kbit/s, MPEG-1 and then 13818-3's (MPEG-2 and 2.5); 0 is the free
    // format and 15 forbidden.
    private static ReadOnlySpan<short> Mpeg1Bitrates => [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];

    private static ReadOnlySpan<short> LowerBitrates => [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    // sampling_frequency for MPEG-1; MPEG-2 halves them, MPEG-2.5 quarters them.
    private static ReadOnlySpan<int> Mpeg1Rates => [44100, 48000, 32000];

    /// <summary>
    /// Reads the header at the start of <paramref name="data"/>: false unless it has the sync word, a version that is not
    /// the reserved one, Layer III, a bitrate of the table (not the free format, whose frame length the header doesn't
    /// give) and a sampling frequency that is not reserved.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out MpegAudioHeader header)
    {
        header = default;
        if (data.Length < Size || data[0] != 0xFF || (data[1] & 0xE0) != 0xE0)
        {
            return false; // 11 bits of sync (MPEG-2.5 takes the twelfth for its version)
        }

        int versionBits = (data[1] >> 3) & 3;
        int layerBits = (data[1] >> 1) & 3;
        int bitrateIndex = data[2] >> 4;
        int rateIndex = (data[2] >> 2) & 3;
        if (versionBits == 1 || layerBits != 1 || bitrateIndex is 0 or 15 || rateIndex == 3)
        {
            return false;
        }

        int version = versionBits switch
        {
            3 => 1,
            2 => 2,
            _ => 25,
        };
        int rate = Mpeg1Rates[rateIndex] >> (version == 1 ? 0 : version == 2 ? 1 : 2);
        int bitrate = 1000 * (version == 1 ? Mpeg1Bitrates : LowerBitrates)[bitrateIndex];
        bool padding = (data[2] & 2) != 0;

        // 2.4.3.1: a frame of 1152 samples holds 144 * bitrate / rate bytes; one of 576, half that; a padding slot adds 1.
        int length = ((version == 1 ? 144 : 72) * bitrate / rate) + (padding ? 1 : 0);
        uint raw = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        header = new MpegAudioHeader(version, (data[1] & 1) == 0, bitrate, rate, data[3] >> 6, length, raw);
        return true;
    }

    /// <summary>Whether <paramref name="other"/> could be the next frame of this one's stream, as FFmpeg's raw MP3 demuxer compares them.</summary>
    public bool SameStream(MpegAudioHeader other) => ((Raw ^ other.Raw) & StreamMask) == 0;
}

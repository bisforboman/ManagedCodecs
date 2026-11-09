namespace ManagedCodecs.Containers;

/// <summary>
/// A Layer III frame header (ISO/IEC 11172-3 2.4.1.3; 13818-3 for the lower rates, MPEG-2.5 below them), for finding
/// frames: the decoder (<c>Mp3Decoder</c>) reads the header again itself.
/// </summary>
/// <param name="Raw">The header's four bytes, big-endian.</param>
internal readonly record struct MpegAudioHeader(bool Mpeg1, int SampleRate, int Bitrate, bool Padding, int Channels, uint Raw)
{
    public const int Size = 4;

    // What two frames of one stream share as FFmpeg's raw MP3 demuxer tells its first frame from junk (by its output on
    // altered libmp3lame files: a first frame that differs from the next in any of these is no packet): the sync word,
    // version, layer and sampling frequency, and the mode, copyright, original and emphasis. Not the protection bit,
    // bitrate, padding, private bit or mode extension, which vary between frames.
    private const uint StreamMask = 0xFFFE0CCF;

    // Layer III bitrates in kbit/s: MPEG-1's, then MPEG-2's and MPEG-2.5's.
    private static ReadOnlySpan<short> Mpeg1Bitrates => [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];

    private static ReadOnlySpan<short> LsfBitrates => [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    public int SamplesPerFrame => Mpeg1 ? 1152 : 576;

    /// <summary>The frame's length in bytes, header included.</summary>
    public int FrameLength => ((Mpeg1 ? 144 : 72) * Bitrate * 1000 / SampleRate) + (Padding ? 1 : 0);

    /// <summary>The side information's length in bytes, after the header (and a CRC's 2 bytes, which an info frame has not).</summary>
    public int SideInfoLength => Mpeg1 ? (Channels == 1 ? 17 : 32) : (Channels == 1 ? 9 : 17);

    /// <summary>Whether <paramref name="other"/> could be the next frame of this one's stream, as FFmpeg's raw MP3 demuxer compares them.</summary>
    public bool SameStream(MpegAudioHeader other) => ((Raw ^ other.Raw) & StreamMask) == 0;

    /// <summary>A Layer III header; false for anything else (another layer, a reserved field, free format).</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out MpegAudioHeader header)
    {
        header = default;
        if (data.Length < Size || data[0] != 0xFF || (data[1] & 0xE0) != 0xE0)
        {
            return false;
        }

        int version = (data[1] >> 3) & 3; // 0 MPEG-2.5, 1 reserved, 2 MPEG-2, 3 MPEG-1
        int layer = (data[1] >> 1) & 3; // 1 is Layer III
        int bitrateIndex = data[2] >> 4;
        int rate = (data[2] >> 2) & 3;
        if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || rate == 3)
        {
            return false;
        }

        int baseRate = rate switch { 0 => 44100, 1 => 48000, _ => 32000 };
        int sampleRate = version switch { 3 => baseRate, 2 => baseRate / 2, _ => baseRate / 4 };
        bool mpeg1 = version == 3;
        header = new MpegAudioHeader(
            mpeg1,
            sampleRate,
            (mpeg1 ? Mpeg1Bitrates : LsfBitrates)[bitrateIndex],
            (data[2] & 2) != 0,
            data[3] >> 6 == 3 ? 1 : 2,
            (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]));
        return true;
    }
}

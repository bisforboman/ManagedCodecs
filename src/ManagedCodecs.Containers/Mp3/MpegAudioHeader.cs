namespace ManagedCodecs.Containers;

/// <summary>
/// A Layer III frame header (ISO/IEC 11172-3 2.4.1.3; 13818-3 for the lower rates, MPEG-2.5 below them), for finding
/// frames: the decoder (<c>Mp3Decoder</c>) reads the header again itself.
/// </summary>
internal readonly record struct MpegAudioHeader(bool Mpeg1, int SampleRate, int Bitrate, bool Padding, int Channels)
{
    public const int Size = 4;

    // Layer III bitrates in kbit/s: MPEG-1's, then MPEG-2's and MPEG-2.5's.
    private static ReadOnlySpan<short> Mpeg1Bitrates => [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];

    private static ReadOnlySpan<short> LsfBitrates => [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    public int SamplesPerFrame => Mpeg1 ? 1152 : 576;

    /// <summary>The frame's length in bytes, header included.</summary>
    public int FrameLength => ((Mpeg1 ? 144 : 72) * Bitrate * 1000 / SampleRate) + (Padding ? 1 : 0);

    /// <summary>The side information's length in bytes, after the header (and a CRC's 2 bytes, which an info frame has not).</summary>
    public int SideInfoLength => Mpeg1 ? (Channels == 1 ? 17 : 32) : (Channels == 1 ? 9 : 17);

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
            data[3] >> 6 == 3 ? 1 : 2);
        return true;
    }
}

namespace ManagedCodecs.Mp3;

/// <summary>An MPEG audio frame header (ISO/IEC 11172-3 2.4.1.3; 13818-3 for the lower rates, MPEG-2.5 below them).</summary>
internal readonly struct FrameHeader
{
    /// <summary>The header's length in bytes.</summary>
    public const int Length = 4;

    /// <summary>Single channel mode (mode 3).</summary>
    public const int Mono = 3;

    /// <summary>Joint stereo mode (mode 1).</summary>
    public const int JointStereo = 1;

    private FrameHeader(bool lsf, int rateIndex, int bitrate, bool hasCrc, bool padding, int mode, int modeExtension)
    {
        Lsf = lsf;
        RateIndex = rateIndex;
        Bitrate = bitrate;
        HasCrc = hasCrc;
        Padding = padding;
        Mode = mode;
        ModeExtension = modeExtension;
    }

    /// <summary>A lower sampling frequency (MPEG-2 or MPEG-2.5): one granule a frame, 576 samples.</summary>
    public bool Lsf { get; }

    /// <summary>The rate's index in <see cref="Mp3Tables.SampleRates"/> (0 to 8).</summary>
    public int RateIndex { get; }

    /// <summary>The bitrate in kbit/s.</summary>
    public int Bitrate { get; }

    /// <summary>Whether a CRC-16 follows the header (protection_bit 0).</summary>
    public bool HasCrc { get; }

    public bool Padding { get; }

    /// <summary>0 stereo, 1 joint stereo, 2 dual channel, 3 single channel.</summary>
    public int Mode { get; }

    /// <summary>In joint stereo, bit 1 mid/side and bit 0 intensity stereo (Layer III).</summary>
    public int ModeExtension { get; }

    public int SampleRate => Mp3Tables.SampleRates[RateIndex];

    public int Channels => Mode == Mono ? 1 : 2;

    public int Granules => Lsf ? 1 : 2;

    public int SamplesPerFrame => 576 * Granules;

    public bool MidSide => Mode == JointStereo && (ModeExtension & 2) != 0;

    public bool Intensity => Mode == JointStereo && (ModeExtension & 1) != 0;

    /// <summary>The frame's length in bytes, header included: 144 (72 at the lower rates) times bitrate over rate.</summary>
    public int FrameLength => ((Lsf ? 72 : 144) * Bitrate * 1000 / SampleRate) + (Padding ? 1 : 0);

    /// <summary>The side information's length in bytes.</summary>
    public int SideInfoLength => Lsf ? (Channels == 1 ? 9 : 17) : (Channels == 1 ? 17 : 32);

    /// <summary>
    /// Parses a Layer III header at the start of <paramref name="data"/>; false if it isn't one (no sync, another layer,
    /// a reserved field, or a free-format bitrate, which this decoder doesn't take).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out FrameHeader header)
    {
        header = default;
        if (data.Length < Length || data[0] != 0xFF || (data[1] & 0xE0) != 0xE0)
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

        int rateIndex = rate + version switch { 3 => 0, 2 => 3, _ => 6 };
        bool lsf = version != 3;
        header = new FrameHeader(
            lsf,
            rateIndex,
            Mp3Tables.Bitrates[lsf ? 1 : 0][bitrateIndex],
            (data[1] & 1) == 0,
            (data[2] & 2) != 0,
            data[3] >> 6,
            (data[3] >> 4) & 3);
        return true;
    }
}

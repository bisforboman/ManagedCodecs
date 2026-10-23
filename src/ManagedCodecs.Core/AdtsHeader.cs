namespace ManagedCodecs;

/// <summary>
/// The fixed and variable header of an ADTS frame (ISO/IEC 14496-3 1.A.3.2, from ISO/IEC 13818-7): what the raw AAC
/// demuxer reads frames by and the AAC decoder skips at the start of a packet.
/// </summary>
/// <param name="Profile">The MPEG-2 profile, the MPEG-4 audio object type minus one (1 is AAC LC).</param>
/// <param name="FrameLength">The frame's length in bytes, this header included.</param>
/// <param name="RawDataBlocks">number_of_raw_data_blocks_in_frame + 1: the frame's access units of 1024 samples.</param>
internal readonly record struct AdtsHeader(
    int Profile,
    int SampleRateIndex,
    int ChannelConfiguration,
    bool HasCrc,
    int FrameLength,
    int RawDataBlocks)
{
    /// <summary>The fixed and variable header, without the CRC.</summary>
    public const int Size = 7;

    /// <summary>The bytes before the first raw data block: the header, and its CRC when there is one.</summary>
    public int HeaderLength => HasCrc ? Size + 2 : Size;

    /// <summary>
    /// Reads the header at the start of <paramref name="data"/>: false unless it has the sync word, layer 0, a sample rate
    /// index of the table (13 and up are reserved or escape) and a frame length that holds the header.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out AdtsHeader header)
    {
        header = default;
        if (data.Length < Size || data[0] != 0xFF || (data[1] & 0xF6) != 0xF0)
        {
            return false; // syncword 0xFFF, then ID (either), layer 00
        }

        bool hasCrc = (data[1] & 1) == 0;
        int profile = data[2] >> 6;
        int sampleRateIndex = (data[2] >> 2) & 0xF;
        int channelConfiguration = ((data[2] & 1) << 2) | (data[3] >> 6);
        int frameLength = ((data[3] & 3) << 11) | (data[4] << 3) | (data[5] >> 5);
        int rawDataBlocks = (data[6] & 3) + 1;
        header = new AdtsHeader(profile, sampleRateIndex, channelConfiguration, hasCrc, frameLength, rawDataBlocks);
        return sampleRateIndex < 13 && frameLength >= header.HeaderLength;
    }

    /// <summary>
    /// The AudioSpecificConfig (ISO/IEC 14496-3 1.6.2.1) the header stands for: object type, sample rate index, channel
    /// configuration, and a GASpecificConfig of 1024-sample frames without core coder or extension.
    /// </summary>
    public byte[] AudioSpecificConfig()
    {
        int objectType = Profile + 1;
        int bits = (objectType << 11) | (SampleRateIndex << 7) | (ChannelConfiguration << 3);
        return [(byte)(bits >> 8), (byte)bits];
    }
}

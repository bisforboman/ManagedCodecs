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

    /// <summary>
    /// The bytes before the first raw data block: the header, and with CRCs the header's (adts_error_check), after the
    /// positions of the blocks but the first when there are several (adts_header_error_check).
    /// </summary>
    public int HeaderLength => HasCrc ? Size + (2 * RawDataBlocks) : Size;

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

    /// <summary>
    /// For channel configuration 0: the AudioSpecificConfig of <see cref="AudioSpecificConfig()"/> followed by the
    /// program config element (4.4.1.1) that starts the frame's first raw data block, and the channels it describes;
    /// false if the block does not start with one (or it runs past the frame). The element's byte alignment, before its
    /// comment, is redone at its new place in the config.
    /// </summary>
    public bool TryProgramConfig(ReadOnlySpan<byte> frame, out byte[] audioSpecificConfig, out int channels)
    {
        audioSpecificConfig = [];
        channels = 0;
        var r = new BitReader(frame[HeaderLength..Math.Min(frame.Length, FrameLength)].ToArray());
        if (r.Read(3) != 5)
        {
            return false; // ID_PCE
        }

        var w = new BitWriter();
        w.Write((ulong)(Profile + 1), 5);
        w.Write((ulong)SampleRateIndex, 4);
        w.Write(0, 4); // channelConfiguration
        w.Write(0, 3); // frameLengthFlag, dependsOnCoreCoder, extensionFlag

        int Copy(int count)
        {
            int value = r.Read(count);
            w.Write((ulong)value, count);
            return value;
        }

        Copy(4 + 2 + 4); // element_instance_tag, object_type, sampling_frequency_index
        int front = Copy(4);
        int side = Copy(4);
        int back = Copy(4);
        int lfe = Copy(2);
        int assocData = Copy(3);
        int cc = Copy(4);
        for (int i = 0; i < 3; i++)
        {
            if (Copy(1) == 1)
            {
                Copy(i < 2 ? 4 : 3); // mono and stereo mixdown element numbers; matrix_mixdown_idx, pseudo_surround_enable
            }
        }

        for (int i = 0; i < front + side + back; i++)
        {
            channels += Copy(1) + 1; // *_element_is_cpe
            Copy(4);
        }

        channels += lfe;
        for (int i = 0; i < lfe + assocData; i++)
        {
            Copy(4);
        }

        for (int i = 0; i < cc; i++)
        {
            Copy(5);
        }

        r.ByteAlign();
        w.ByteAlign();
        int comment = Copy(8);
        for (int i = 0; i < comment; i++)
        {
            Copy(8);
        }

        if (r.PastEnd || channels == 0)
        {
            return false;
        }

        audioSpecificConfig = w.ToArray();
        return true;
    }

    // MSB-first bits of a span; zeros past its end, which PastEnd then shows.
    private sealed class BitReader(byte[] data)
    {
        private readonly byte[] _data = data;
        private int _position;

        public bool PastEnd => _position > _data.Length * 8;

        public int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++, _position++)
            {
                int bit = _position < _data.Length * 8 ? (_data[_position >> 3] >> (7 - (_position & 7))) & 1 : 0;
                value = (value << 1) | bit;
            }

            return value;
        }

        public void ByteAlign() => _position = (_position + 7) & ~7;
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bits;

        public void Write(ulong value, int count)
        {
            for (int i = count - 1; i >= 0; i--, _bits++)
            {
                if ((_bits & 7) == 0)
                {
                    _bytes.Add(0);
                }

                _bytes[^1] |= (byte)(((value >> i) & 1) << (7 - (_bits & 7)));
            }
        }

        public void ByteAlign() => _bits = (_bits + 7) & ~7;

        public byte[] ToArray() => [.. _bytes];
    }
}

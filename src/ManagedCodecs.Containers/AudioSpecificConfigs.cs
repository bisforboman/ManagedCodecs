namespace ManagedCodecs.Containers;

/// <summary>The AudioSpecificConfig facts the demuxers report (MP4's 'esds', Matroska's CodecPrivate).</summary>
internal static class AudioSpecificConfigs
{
    /// <summary>
    /// The start of an AudioSpecificConfig (14496-3 1.6.2.1): audioObjectType, then the sampling
    /// frequency index (or an explicit 24-bit rate) and channelConfiguration. With spectral band replication the rate
    /// is the SBR rate, as FFmpeg reports it (edit list times convert to the decoder's output at it): after object type
    /// 5 or 29 (hierarchical signalling), or in the sync extension after an AAC LC core's GASpecificConfig. A mono stream
    /// with parametric stereo (object type 29, or its flag in the sync extension) decodes to stereo, and has two channels.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> config, out int sampleRate, out int channels)
    {
        sampleRate = 0;
        channels = 0;
        if (config.Length < 2)
        {
            return false;
        }

        int bit = 0;
        int Read(int count, ReadOnlySpan<byte> data)
        {
            int value = 0;
            for (int i = 0; i < count; i++, bit++)
            {
                int b = bit >> 3 < data.Length ? (data[bit >> 3] >> (7 - (bit & 7))) & 1 : 0;
                value = (value << 1) | b;
            }

            return value;
        }

        int ObjectType(ReadOnlySpan<byte> data)
        {
            int type = Read(5, data);
            return type == 31 ? 32 + Read(6, data) : type; // audioObjectTypeExt
        }

        int Rate(ReadOnlySpan<byte> data)
        {
            int index = Read(4, data);
            ReadOnlySpan<int> rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
            return index == 15 ? Read(24, data) : index < rates.Length ? rates[index] : 0;
        }

        int objectType = ObjectType(config);
        sampleRate = Rate(config);
        int configuration = Read(4, config);
        bool parametricStereo = objectType == 29;
        if (objectType is 5 or 29)
        {
            sampleRate = Rate(config); // then the core's object type
        }
        else if (objectType == 2 && configuration != 0 && config.Length * 8 >= bit + 3 + 16)
        {
            // GASpecificConfig: frameLengthFlag, dependsOnCoreCoder (and a 14-bit delay), extensionFlag.
            Read(1, config);
            if (Read(1, config) == 1)
            {
                Read(14, config);
            }

            Read(1, config);
            if (config.Length * 8 >= bit + 16 && Read(11, config) == 0x2B7 && ObjectType(config) == 5 && Read(1, config) == 1)
            {
                sampleRate = Rate(config);
                parametricStereo = config.Length * 8 >= bit + 12 && Read(11, config) == 0x548 && Read(1, config) == 1;
            }
        }

        channels = configuration switch
        {
            1 when parametricStereo => 2,
            >= 1 and <= 6 => configuration,
            7 => 8,
            _ => 0,
        };

        return sampleRate > 0;
    }
}

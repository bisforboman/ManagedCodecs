namespace ManagedCodecs.Aac;

/// <summary>AudioSpecificConfig (ISO/IEC 14496-3 1.6.2.1) with the GASpecificConfig of AAC LC.</summary>
internal sealed record AudioSpecificConfig(int ObjectType, int SampleRateIndex, int SampleRate, int ChannelConfiguration, int Channels)
{
    public const int AacLc = 2;

    public static AudioSpecificConfig Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            throw new InvalidDataException("AudioSpecificConfig is too short.");
        }

        var r = new AacBitReader(data);
        int objectType = ReadObjectType(r);
        var (index, rate) = ReadSampleRate(r);
        int channelConfiguration = r.Read(4);

        if (objectType is 5 or 29)
        {
            throw new NotSupportedException("HE-AAC (SBR / PS) is not supported.");
        }

        if (objectType != AacLc)
        {
            throw new NotSupportedException($"AAC object type {objectType} is not supported (AAC LC only).");
        }

        // GASpecificConfig (4.4.1).
        if (r.ReadFlag())
        {
            throw new NotSupportedException("960-sample frames are not supported.");
        }

        if (r.ReadFlag())
        {
            r.Read(14); // coreCoderDelay
        }

        r.ReadFlag(); // extensionFlag: no extension fields for AAC LC

        int channels = channelConfiguration switch
        {
            >= 1 and <= 6 => channelConfiguration,
            7 => 8,
            _ => 0,
        };

        if (channelConfiguration == 0)
        {
            channels = ProgramConfigElement.Parse(r).Channels;
            if (channels == 0)
            {
                throw new InvalidDataException("The program config element has no channels.");
            }
        }
        else if (channels == 0)
        {
            throw new NotSupportedException($"channelConfiguration {channelConfiguration} is not supported.");
        }

        return new AudioSpecificConfig(objectType, index, rate, channelConfiguration, channels);
    }

    private static int ReadObjectType(AacBitReader r)
    {
        int type = r.Read(5);
        return type == 31 ? 32 + r.Read(6) : type;
    }

    /// <summary>
    /// samplingFrequencyIndex, or an explicit rate mapped to the index whose tables it uses
    /// (Table 4.82).
    /// </summary>
    private static (int Index, int Rate) ReadSampleRate(AacBitReader r)
    {
        int index = r.Read(4);
        if (index < 13)
        {
            return (index, AacTables.SampleRates[index]);
        }

        if (index != 15)
        {
            throw new InvalidDataException($"Reserved samplingFrequencyIndex {index}.");
        }

        int rate = r.Read(24);
        if (rate <= 0)
        {
            throw new InvalidDataException("Invalid explicit sampling frequency.");
        }

        ReadOnlySpan<int> thresholds = [92017, 75132, 55426, 46009, 37566, 27713, 23004, 18783, 13856, 11502, 9391];
        int mapped = 11;
        for (int i = 0; i < thresholds.Length; i++)
        {
            if (rate >= thresholds[i])
            {
                mapped = i;
                break;
            }
        }

        return (mapped, rate);
    }
}

/// <summary>program_config_element (4.4.1.1); only the channel count is used.</summary>
internal sealed record ProgramConfigElement(int Channels)
{
    public static ProgramConfigElement Parse(AacBitReader r)
    {
        r.Read(4); // element_instance_tag
        r.Read(2); // object_type
        r.Read(4); // sampling_frequency_index
        int front = r.Read(4);
        int side = r.Read(4);
        int back = r.Read(4);
        int lfe = r.Read(2);
        int assocData = r.Read(3);
        int cc = r.Read(4);
        if (r.ReadFlag())
        {
            r.Read(4); // mono_mixdown_element_number
        }

        if (r.ReadFlag())
        {
            r.Read(4); // stereo_mixdown_element_number
        }

        if (r.ReadFlag())
        {
            r.Read(3); // matrix_mixdown_idx, pseudo_surround_enable
        }

        int channels = 0;
        foreach (int count in new[] { front, side, back })
        {
            for (int i = 0; i < count; i++)
            {
                channels += r.ReadFlag() ? 2 : 1; // *_element_is_cpe
                r.Read(4);
            }
        }

        channels += lfe;
        r.Skip((lfe * 4) + (assocData * 4) + (cc * 5));
        r.ByteAlign();
        int commentBytes = r.Read(8);
        r.Skip(commentBytes * 8);
        return new ProgramConfigElement(channels);
    }
}

namespace ManagedCodecs.Aac.Sbr;

/// <summary>
/// The SBR state of one channel element (an SCE or a CPE): its latest header and the frequency tables from it, and its
/// channels' data of the current frame, read from the element's sbr_extension_data (ISO/IEC 14496-3 4.4.2.8).
/// </summary>
internal sealed class SbrElement(int channels, int sampleRate)
{
    /// <summary>EXT_SBR_DATA and EXT_SBR_DATA_CRC (Table 4.121): the extension_payload types that carry SBR.</summary>
    public const int ExtensionSbr = 13;
    public const int ExtensionSbrCrc = 14;

    /// <summary>bs_extension_id of parametric stereo (Table 4.118).</summary>
    private const int ExtensionPs = 2;

    public SbrHeader? Header { get; private set; }

    public SbrFrequencies? Frequencies { get; private set; }

    public SbrChannelData[] Channels { get; } = [.. Enumerable.Range(0, channels).Select(_ => new SbrChannelData())];

    /// <summary>Whether the pair's envelopes are coded as a level and a balance (bs_coupling).</summary>
    public bool Coupling { get; private set; }

    /// <summary>Whether this frame's data was read: SBR waits for a header after a start or reset.</summary>
    public bool HasFrame { get; private set; }

    /// <summary>
    /// Reads sbr_extension_data of <paramref name="bits"/> bits, after its extension_type (a fill element's payload past
    /// its 4-bit type); the bits after the SBR data, which pad it to whole bytes, are left for the caller to skip.
    /// </summary>
    public void Read(AacBitReader r, int bits, bool crc, AacStatistics statistics)
    {
        int end = r.Position + bits;
        if (crc)
        {
            r.Read(10); // bs_sbr_crc_bits, not checked
        }

        if (r.ReadFlag())
        {
            var header = SbrHeader.Parse(r);
            statistics.SbrHeaders++;
            if (header.NeedsReset(Header))
            {
                Frequencies = new SbrFrequencies(header, sampleRate);
                statistics.SbrResets++;
                statistics.SbrFrequencyScales |= 1 << header.FrequencyScale;
            }

            Header = header;
        }

        HasFrame = Header is not null;
        if (Header is null || Frequencies is null)
        {
            return; // no header yet: the data cannot be read
        }

        if (channels == 1)
        {
            ReadSingle(r, Header, Frequencies);
        }
        else
        {
            ReadPair(r, Header, Frequencies);
        }

        ReadExtendedData(r, statistics);
        if (r.Position > end)
        {
            throw new InvalidDataException($"SBR data runs {r.Position - end} bits past its extension payload.");
        }

        statistics.SbrFrames++;
        statistics.SbrMostUnreadBits = Math.Max(statistics.SbrMostUnreadBits, end - r.Position);
        statistics.SbrCoupledFrames += Coupling ? 1 : 0;
        foreach (var channel in Channels)
        {
            statistics.SbrFrameClasses[channel.FrameClass]++;
            statistics.SbrFineAmpEnvelopes += channel.AmpResolution == 0 ? channel.NumEnvelopes : 0;
            for (int k = 0; k < Frequencies.Bands(1); k++)
            {
                statistics.SbrHarmonics += channel.AddHarmonic[k] ? 1 : 0;
            }
        }
    }

    // sbr_single_channel_element (Table 4.64).
    private void ReadSingle(AacBitReader r, SbrHeader header, SbrFrequencies frequencies)
    {
        if (r.ReadFlag())
        {
            r.Read(4); // bs_reserved
        }

        var channel = Channels[0];
        channel.ReadGrid(r);
        channel.ReadDeltaDirections(r);
        channel.ReadInverseFiltering(r, frequencies.NoiseBands);
        channel.ReadEnvelopes(r, frequencies, header.AmpResolution, balance: false);
        channel.ReadNoiseFloors(r, frequencies, balance: false);
        channel.ReadHarmonics(r, frequencies);
        Coupling = false;
    }

    // sbr_channel_pair_element (Table 4.65): coupled, the right channel shares the left one's grid and inverse filtering
    // and codes its envelopes and noise floors as balances; else each channel is coded as alone.
    private void ReadPair(AacBitReader r, SbrHeader header, SbrFrequencies frequencies)
    {
        if (r.ReadFlag())
        {
            r.Read(8); // bs_reserved, twice
        }

        var (left, right) = (Channels[0], Channels[1]);
        Coupling = r.ReadFlag();
        if (Coupling)
        {
            left.ReadGrid(r);
            right.CopyGrid(left);
            left.ReadDeltaDirections(r);
            right.ReadDeltaDirections(r);
            left.ReadInverseFiltering(r, frequencies.NoiseBands);
            left.InverseFilteringMode.CopyTo(right.InverseFilteringMode, 0);
            left.ReadEnvelopes(r, frequencies, header.AmpResolution, balance: false);
            left.ReadNoiseFloors(r, frequencies, balance: false);
            right.ReadEnvelopes(r, frequencies, header.AmpResolution, balance: true);
            right.ReadNoiseFloors(r, frequencies, balance: true);
        }
        else
        {
            left.ReadGrid(r);
            right.ReadGrid(r);
            left.ReadDeltaDirections(r);
            right.ReadDeltaDirections(r);
            left.ReadInverseFiltering(r, frequencies.NoiseBands);
            right.ReadInverseFiltering(r, frequencies.NoiseBands);
            left.ReadEnvelopes(r, frequencies, header.AmpResolution, balance: false);
            right.ReadEnvelopes(r, frequencies, header.AmpResolution, balance: false);
            left.ReadNoiseFloors(r, frequencies, balance: false);
            right.ReadNoiseFloors(r, frequencies, balance: false);
        }

        left.ReadHarmonics(r, frequencies);
        right.ReadHarmonics(r, frequencies);
    }

    // bs_extended_data (Table 4.64): extensions of a counted number of bytes; parametric stereo is noted and skipped.
    private static void ReadExtendedData(AacBitReader r, AacStatistics statistics)
    {
        if (!r.ReadFlag())
        {
            return;
        }

        int count = r.Read(4);
        if (count == 15)
        {
            count += r.Read(8);
        }

        int bits = 8 * count;
        if (bits >= 2 && r.Peek(2) == ExtensionPs)
        {
            statistics.SbrParametricStereoFrames++;
        }

        r.Skip(bits);
    }
}

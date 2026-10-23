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

    /// <summary>A mono element's parametric stereo data, once any comes; <see cref="HasPs"/> says whether this frame's did.</summary>
    public PsData? Ps { get; private set; }

    public bool HasPs { get; private set; }

    /// <summary>Whether this frame's header changed the frequency tables, which restarts the HF adjustment's state.</summary>
    public bool Reset { get; private set; }

    /// <summary>Clears <see cref="HasFrame"/> and <see cref="Reset"/> before an access unit, for an element whose SBR data may not come.</summary>
    public void StartFrame()
    {
        HasFrame = false;
        Reset = false;
        HasPs = false;
    }

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
            statistics.SbrLimiterBands |= 1 << header.LimiterBands;
            statistics.SbrLimiterGains |= 1 << header.LimiterGains;
            statistics.SbrUninterpolatedHeaders += header.InterpolateFrequency ? 0 : 1;
            if (header.NeedsReset(Header))
            {
                Frequencies = new SbrFrequencies(header, sampleRate);
                Reset = true;
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

        ReadExtendedData(r, end, statistics);
        if (r.Position > end)
        {
            throw new InvalidDataException($"SBR data runs {r.Position - end} bits past its extension payload.");
        }

        Dequantize(Frequencies);

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

    // 4.6.18.3.5: an envelope value v is an energy of 64 * 2^(v / a) (a = 2 at 1.5 dB steps, 1 at 3 dB), a noise floor
    // value q a noise-to-signal ratio of 2^(6 - q). A coupled pair's left channel codes the level, its right one the
    // balance b (in the level's steps, 12 for equal), whose ratio r = 2^(b / a - 12) splits the pair's energy as
    // r / (1 + r) and 1 / (1 + r); its noise floors likewise, with r = 2^(b - 12). Powers of 2 by ScaleB, so that the
    // result is the same on every runtime.
    private void Dequantize(SbrFrequencies frequencies)
    {
        for (int ch = 0; ch < Channels.Length; ch++)
        {
            Channels[ch].DecodeValues(frequencies, balance: Coupling && ch == 1);
        }

        if (!Coupling)
        {
            foreach (var channel in Channels)
            {
                bool fine = channel.AmpResolution == 0;
                for (int l = 0; l < channel.NumEnvelopes; l++)
                {
                    for (int k = 0; k < frequencies.Bands(channel.FrequencyResolution[l]); k++)
                    {
                        channel.EnvelopeEnergy[l][k] = 64 * Power(channel.EnvelopeValue[l][k], fine);
                    }
                }

                for (int l = 0; l < channel.NumNoiseFloors; l++)
                {
                    for (int k = 0; k < frequencies.NoiseBands; k++)
                    {
                        channel.NoiseRatio[l][k] = Math.ScaleB(1.0, 6 - channel.NoiseValue[l][k]);
                    }
                }
            }

            return;
        }

        var (left, right) = (Channels[0], Channels[1]);
        bool coupledFine = left.AmpResolution == 0;
        for (int l = 0; l < left.NumEnvelopes; l++)
        {
            for (int k = 0; k < frequencies.Bands(left.FrequencyResolution[l]); k++)
            {
                double level = 128 * Power(left.EnvelopeValue[l][k], coupledFine);
                double ratio = Math.ScaleB(Power(right.EnvelopeValue[l][k], coupledFine), -12);
                left.EnvelopeEnergy[l][k] = level * ratio / (1 + ratio);
                right.EnvelopeEnergy[l][k] = level / (1 + ratio);
            }
        }

        for (int l = 0; l < left.NumNoiseFloors; l++)
        {
            for (int k = 0; k < frequencies.NoiseBands; k++)
            {
                double level = Math.ScaleB(1.0, 7 - left.NoiseValue[l][k]);
                double ratio = Math.ScaleB(1.0, right.NoiseValue[l][k] - 12);
                left.NoiseRatio[l][k] = level * ratio / (1 + ratio);
                right.NoiseRatio[l][k] = level / (1 + ratio);
            }
        }
    }

    // 2^(v / 2) at 1.5 dB steps (an odd v takes a factor of the square root of 2), 2^v at 3 dB.
    private static double Power(int value, bool fine) =>
        fine ? Math.ScaleB((value & 1) != 0 ? Math.Sqrt(2) : 1.0, value >> 1) : Math.ScaleB(1.0, value);

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

    // bs_extended_data (Table 4.64): extensions of a counted number of bytes, each a 2-bit id and its data; a mono
    // element's parametric stereo (id 2) is read, the rest skipped.
    private void ReadExtendedData(AacBitReader r, int end, AacStatistics statistics)
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
        if (r.Position + bits > end)
        {
            throw new InvalidDataException("SBR extended data runs past its extension payload.");
        }

        int extensionEnd = r.Position + bits;
        while (extensionEnd - r.Position > 7)
        {
            int id = r.Read(2);
            if (id == ExtensionPs && channels == 1)
            {
                statistics.SbrParametricStereoFrames++;
                Ps ??= new PsData();
                HasPs = Ps.Read(r, extensionEnd - r.Position, statistics);
            }
            else
            {
                r.Skip(extensionEnd - r.Position);
            }
        }

        statistics.PsMostUnreadBits = Math.Max(statistics.PsMostUnreadBits, extensionEnd - r.Position);
        r.Skip(extensionEnd - r.Position);
    }
}

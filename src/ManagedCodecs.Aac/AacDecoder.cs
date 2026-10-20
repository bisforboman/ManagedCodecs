namespace ManagedCodecs.Aac;

/// <summary>
/// AAC LC decoder (ISO/IEC 14496-3 subpart 4) for raw access units as stored in MP4: one
/// packet in, 1024 samples per channel out.
/// </summary>
/// <remarks>
/// Supports single and channel pair elements, LFE, M/S and intensity stereo, perceptual noise
/// substitution, TNS, pulse data, long and short windows with sine and KBD shapes, and channel
/// configurations 0 (program config element) to 7. Output is in the WAVE / FFmpeg channel order
/// (for 5.1: FL FR FC LFE BL BR). Not supported: other object types (Main, SSR, LTP, HE-AAC),
/// coupling channel elements, 960-sample frames. Every packet yields 1024 samples, encoder
/// priming included; the container's timestamps (negative for priming) and the last packet's
/// duration say what to trim.
/// </remarks>
public sealed class AacDecoder : IAudioDecoder
{
    private const int SceElement = 0;
    private const int CpeElement = 1;
    private const int CceElement = 2;
    private const int LfeElement = 3;
    private const int DseElement = 4;
    private const int PceElement = 5;
    private const int FilElement = 6;
    private const int EndElement = 7;

    private readonly AudioSpecificConfig _config;
    private readonly Filterbank[] _filterbanks;
    private readonly ChannelStream[] _streams;
    private readonly int[] _outputOrder;

    // Seed of the noise generator for PNS.
    private uint _noiseState = 0x1F2E3D4C;

    public AacDecoder(ReadOnlySpan<byte> audioSpecificConfig)
    {
        _config = AudioSpecificConfig.Parse(audioSpecificConfig);
        int channels = _config.Channels;
        _filterbanks = [.. Enumerable.Range(0, channels).Select(_ => new Filterbank())];
        _streams = [.. Enumerable.Range(0, channels).Select(_ => new ChannelStream())];
        _outputOrder = OutputOrder(_config.ChannelConfiguration, channels);
    }

    public AacDecoder(AudioStreamInfo stream)
        : this(stream.CodecPrivate.Span)
    {
    }

    public int SampleRate => _config.SampleRate;

    public int Channels => _config.Channels;

    /// <summary>How often each coding tool was used, so tests can check samples exercise them.</summary>
    internal AacStatistics Statistics { get; } = new();

    public AudioFrame Decode(ReadOnlySpan<byte> packet)
    {
        var r = new AacBitReader(packet);
        int channel = 0;
        while (true)
        {
            int element = r.Read(3);
            if (element == EndElement)
            {
                break;
            }

            switch (element)
            {
                case SceElement or LfeElement:
                    r.Read(4); // element_instance_tag
                    CheckChannels(channel + 1);
                    DecodeIndividualStream(r, _streams[channel], commonWindow: false);
                    channel++;
                    break;

                case CpeElement:
                    r.Read(4);
                    CheckChannels(channel + 2);
                    DecodeChannelPair(r, _streams[channel], _streams[channel + 1]);
                    channel += 2;
                    break;

                case CceElement:
                    throw new NotSupportedException("Coupling channel elements are not supported.");

                case DseElement:
                {
                    r.Read(4);
                    bool align = r.ReadFlag();
                    int count = r.Read(8);
                    if (count == 255)
                    {
                        count += r.Read(8);
                    }

                    if (align)
                    {
                        r.ByteAlign();
                    }

                    r.Skip(count * 8);
                    break;
                }

                case PceElement:
                    ProgramConfigElement.Parse(r);
                    break;

                case FilElement:
                {
                    // Extension payloads (SBR, dynamic range, fill) are skipped.
                    int count = r.Read(4);
                    if (count == 15)
                    {
                        count += r.Read(8) - 1;
                    }

                    r.Skip(count * 8);
                    break;
                }
            }
        }

        if (channel != _config.Channels)
        {
            throw new InvalidDataException($"The access unit has {channel} channels, the configuration {_config.Channels}.");
        }

        var frame = new AudioFrame(_config.SampleRate, _config.Channels, 1024);
        for (int output = 0; output < _config.Channels; output++)
        {
            int source = _outputOrder[output];
            var stream = _streams[source];
            ApplyTns(stream);
            _filterbanks[source].Synthesize(stream.Info.WindowSequence, stream.Info.WindowShape, stream.Spectrum, frame.Samples[output]);
        }

        return frame;
    }

    private void CheckChannels(int needed)
    {
        if (needed > _config.Channels)
        {
            throw new InvalidDataException("The access unit has more channels than its configuration.");
        }
    }

    /// <summary>For each output channel (WAVE order), the channel's position in bitstream order.</summary>
    private static int[] OutputOrder(int configuration, int channels) => configuration switch
    {
        3 => [1, 2, 0],                   // C, L R -> FL FR FC
        4 => [1, 2, 0, 3],                // C, L R, Cs -> FL FR FC BC
        5 => [1, 2, 0, 3, 4],             // C, L R, Ls Rs -> FL FR FC BL BR
        6 => [1, 2, 0, 5, 3, 4],          // C, L R, Ls Rs, LFE -> FL FR FC LFE BL BR
        7 => [3, 4, 0, 7, 5, 6, 1, 2],    // C, Lc Rc, L R, Ls Rs, LFE -> FL FR FC LFE BL BR FLC FRC
        _ => [.. Enumerable.Range(0, channels)],
    };

    // ---- Syntax ----

    /// <summary>channel_pair_element (4.4.2.1), then M/S and intensity stereo.</summary>
    private void DecodeChannelPair(AacBitReader r, ChannelStream left, ChannelStream right)
    {
        bool commonWindow = r.ReadFlag();
        int msMaskPresent = 0;
        Span<bool> msUsed = stackalloc bool[8 * 64];
        if (commonWindow)
        {
            ReadIcsInfo(r, left.Info);
            right.Info.CopyFrom(left.Info);
            msMaskPresent = r.Read(2);
            if (msMaskPresent == 3)
            {
                throw new InvalidDataException("Reserved ms_mask_present value.");
            }

            for (int g = 0; g < left.Info.WindowGroups; g++)
            {
                for (int sfb = 0; sfb < left.Info.MaxSfb; sfb++)
                {
                    msUsed[(g * 64) + sfb] = msMaskPresent == 2 || (msMaskPresent == 1 && r.ReadFlag());
                }
            }
        }

        DecodeIndividualStream(r, left, commonWindow);
        DecodeIndividualStream(r, right, commonWindow);

        if (commonWindow)
        {
            ApplyStereo(left, right, msMaskPresent, msUsed);
        }
    }

    /// <summary>individual_channel_stream (4.4.2.7), dequantized into the stream's spectrum.</summary>
    private void DecodeIndividualStream(AacBitReader r, ChannelStream stream, bool commonWindow)
    {
        var info = stream.Info;
        int globalGain = r.Read(8);
        if (!commonWindow)
        {
            ReadIcsInfo(r, info);
        }

        ReadSectionData(r, stream);
        ReadScalefactors(r, stream, globalGain);

        stream.PulsePresent = r.ReadFlag();
        if (stream.PulsePresent)
        {
            if (info.WindowSequence == WindowSequence.EightShort)
            {
                throw new InvalidDataException("Pulse data with short windows.");
            }

            ReadPulseData(r, stream);
            Statistics.PulseFrames++;
        }

        stream.Tns.Present = r.ReadFlag();
        if (stream.Tns.Present)
        {
            ReadTnsData(r, stream);
        }

        if (r.ReadFlag())
        {
            throw new NotSupportedException("Gain control data (AAC SSR) is not supported.");
        }

        ReadSpectralData(r, stream);
        Dequantize(stream);

        switch (info.WindowSequence)
        {
            case WindowSequence.EightShort: Statistics.ShortFrames++; break;
            case WindowSequence.LongStart: Statistics.StartFrames++; break;
            case WindowSequence.LongStop: Statistics.StopFrames++; break;
            default: Statistics.LongFrames++; break;
        }

        if (info.WindowShape == 1)
        {
            Statistics.KbdFrames++;
        }
    }

    /// <summary>ics_info (4.4.2.6) and the window grouping derived from it (4.5.2.3.4).</summary>
    private void ReadIcsInfo(AacBitReader r, IcsInfo info)
    {
        r.Read(1); // ics_reserved_bit
        info.WindowSequence = (WindowSequence)r.Read(2);
        info.WindowShape = r.Read(1);
        int rate = _config.SampleRateIndex;

        if (info.WindowSequence == WindowSequence.EightShort)
        {
            info.MaxSfb = r.Read(4);
            int grouping = r.Read(7);
            info.SwbOffset = AacTables.SwbOffsetShort[rate];
            info.WindowGroups = 1;
            info.GroupLength[0] = 1;
            for (int i = 0; i < 7; i++)
            {
                if ((grouping & (1 << (6 - i))) != 0)
                {
                    info.GroupLength[info.WindowGroups - 1]++;
                }
                else
                {
                    info.GroupLength[info.WindowGroups++] = 1;
                }
            }
        }
        else
        {
            info.MaxSfb = r.Read(6);
            if (r.ReadFlag())
            {
                throw new NotSupportedException("Prediction (AAC Main / LTP) is not supported.");
            }

            info.SwbOffset = AacTables.SwbOffsetLong[rate];
            info.WindowGroups = 1;
            info.GroupLength[0] = 1;
        }

        if (info.MaxSfb > info.NumSwb)
        {
            throw new InvalidDataException($"max_sfb {info.MaxSfb} exceeds the {info.NumSwb} bands of the window.");
        }
    }

    /// <summary>section_data (4.4.2.7): a codebook for every band of every window group.</summary>
    private static void ReadSectionData(AacBitReader r, ChannelStream stream)
    {
        var info = stream.Info;
        bool isShort = info.WindowSequence == WindowSequence.EightShort;
        int lengthBits = isShort ? 3 : 5;
        int escape = (1 << lengthBits) - 1;

        for (int g = 0; g < info.WindowGroups; g++)
        {
            int k = 0;
            while (k < info.MaxSfb)
            {
                int book = r.Read(4);
                if (book == 12)
                {
                    throw new InvalidDataException("Reserved section codebook 12.");
                }

                int length = 0;
                int increment;
                while ((increment = r.Read(lengthBits)) == escape)
                {
                    length += escape;
                }

                length += increment;
                if (length == 0 || k + length > info.MaxSfb)
                {
                    throw new InvalidDataException("Section runs past max_sfb.");
                }

                for (int sfb = k; sfb < k + length; sfb++)
                {
                    stream.Codebook[(g * 64) + sfb] = book;
                }

                k += length;
            }

            for (int sfb = info.MaxSfb; sfb < 64; sfb++)
            {
                stream.Codebook[(g * 64) + sfb] = ChannelStream.ZeroBook;
            }
        }
    }

    /// <summary>
    /// scale_factor_data (4.4.2.7, 4.6.2.3): three DPCM chains, for scalefactors (from
    /// global_gain), intensity positions (from 0) and noise energies (from global_gain - 90,
    /// the first one sent as 9 raw bits).
    /// </summary>
    private void ReadScalefactors(AacBitReader r, ChannelStream stream, int globalGain)
    {
        var info = stream.Info;
        int scalefactor = globalGain;
        int intensity = 0;
        int noise = globalGain - 90;
        bool firstNoise = true;

        for (int g = 0; g < info.WindowGroups; g++)
        {
            for (int sfb = 0; sfb < info.MaxSfb; sfb++)
            {
                int i = (g * 64) + sfb;
                switch (stream.Codebook[i])
                {
                    case ChannelStream.ZeroBook:
                        stream.Scalefactor[i] = 0;
                        break;

                    case ChannelStream.IntensityBook or ChannelStream.IntensityBook2:
                        intensity += Huffman.ReadScalefactorDelta(r);
                        stream.Scalefactor[i] = intensity;
                        Statistics.IntensityBands++;
                        break;

                    case ChannelStream.NoiseBook:
                        noise += firstNoise ? r.Read(9) - 256 : Huffman.ReadScalefactorDelta(r);
                        firstNoise = false;
                        stream.Scalefactor[i] = noise;
                        Statistics.NoiseBands++;
                        break;

                    default:
                        scalefactor += Huffman.ReadScalefactorDelta(r);
                        if (scalefactor is < 0 or > 255)
                        {
                            throw new InvalidDataException($"Scalefactor {scalefactor} is out of range.");
                        }

                        stream.Scalefactor[i] = scalefactor;
                        break;
                }
            }
        }
    }

    /// <summary>pulse_data (4.4.2.7): up to four pulses added to quantized values of a long window.</summary>
    private static void ReadPulseData(AacBitReader r, ChannelStream stream)
    {
        stream.PulseCount = r.Read(2) + 1;
        int startSfb = r.Read(6);
        if (startSfb >= stream.Info.NumSwb)
        {
            throw new InvalidDataException("pulse_start_sfb is out of range.");
        }

        int k = stream.Info.SwbOffset[startSfb];
        for (int i = 0; i < stream.PulseCount; i++)
        {
            k += r.Read(5);
            stream.PulseOffset[i] = k;
            stream.PulseAmplitude[i] = r.Read(4);
        }

        if (k >= 1024)
        {
            throw new InvalidDataException("Pulse position is out of range.");
        }
    }

    /// <summary>tns_data (4.4.2.7) for AAC LC.</summary>
    private void ReadTnsData(AacBitReader r, ChannelStream stream)
    {
        var info = stream.Info;
        var tns = stream.Tns;
        bool isShort = info.WindowSequence == WindowSequence.EightShort;
        int windows = isShort ? 8 : 1;

        for (int w = 0; w < windows; w++)
        {
            tns.FilterCount[w] = r.Read(isShort ? 1 : 2);
            if (tns.FilterCount[w] == 0)
            {
                continue;
            }

            int coefRes = r.Read(1);
            for (int f = 0; f < tns.FilterCount[w]; f++)
            {
                var filter = tns.Filters[(w * 4) + f];
                filter.Length = r.Read(isShort ? 4 : 6);
                filter.Order = r.Read(isShort ? 3 : 5);
                filter.CoefResolutionBits = coefRes + 3;
                if (filter.Order == 0)
                {
                    continue;
                }

                filter.Downward = r.ReadFlag();
                int compress = r.Read(1);
                int bits = filter.CoefResolutionBits - compress;
                for (int i = 0; i < filter.Order; i++)
                {
                    int raw = r.Read(bits);
                    filter.Coefficients[i] = raw >= 1 << (bits - 1) ? raw - (1 << bits) : raw; // two's complement
                }

                Statistics.TnsFilters++;
            }
        }
    }

    /// <summary>
    /// spectral_data (4.4.2.7). For short windows the bitstream runs group by group, band by
    /// band, window by window; values are stored per window (window * 128 + k).
    /// </summary>
    private static void ReadSpectralData(AacBitReader r, ChannelStream stream)
    {
        var info = stream.Info;
        var quantized = stream.Quantized;
        Array.Clear(quantized);
        Span<int> values = stackalloc int[4];
        int windowBase = 0;

        for (int g = 0; g < info.WindowGroups; g++)
        {
            int groupLength = info.GroupLength[g];
            for (int sfb = 0; sfb < info.MaxSfb; sfb++)
            {
                int book = stream.Codebook[(g * 64) + sfb];
                if (book is ChannelStream.ZeroBook or ChannelStream.NoiseBook or ChannelStream.IntensityBook or ChannelStream.IntensityBook2)
                {
                    continue;
                }

                int start = info.SwbOffset[sfb];
                int end = info.SwbOffset[sfb + 1];
                int step = book <= 4 ? 4 : 2;
                for (int w = 0; w < groupLength; w++)
                {
                    int window = (windowBase + w) * 128;
                    for (int k = start; k < end; k += step)
                    {
                        Huffman.ReadSpectrum(r, book, values);
                        for (int j = 0; j < step; j++)
                        {
                            quantized[window + k + j] = values[j];
                        }
                    }
                }
            }

            windowBase += groupLength;
        }
    }

    // ---- Reconstruction ----

    /// <summary>
    /// Pulse data, then inverse quantization sign(q) |q|^(4/3) and scaling by
    /// 2^(0.25 (sf - 100)) (4.6.1, 4.6.2); noise bands get scaled random values (4.6.13).
    /// </summary>
    private void Dequantize(ChannelStream stream)
    {
        var info = stream.Info;
        var q = stream.Quantized;
        var spectrum = stream.Spectrum;
        Array.Clear(spectrum);

        if (stream.PulsePresent)
        {
            for (int i = 0; i < stream.PulseCount; i++)
            {
                int k = stream.PulseOffset[i];
                q[k] += q[k] > 0 ? stream.PulseAmplitude[i] : -stream.PulseAmplitude[i];
            }
        }

        int windowBase = 0;
        for (int g = 0; g < info.WindowGroups; g++)
        {
            for (int sfb = 0; sfb < info.MaxSfb; sfb++)
            {
                int index = (g * 64) + sfb;
                int book = stream.Codebook[index];
                int start = info.SwbOffset[sfb];
                int end = info.SwbOffset[sfb + 1];

                if (book == ChannelStream.NoiseBook)
                {
                    for (int w = 0; w < info.GroupLength[g]; w++)
                    {
                        FillNoise(spectrum.AsSpan(((windowBase + w) * 128) + start, end - start), stream.Scalefactor[index]);
                    }

                    continue;
                }

                if (book is ChannelStream.ZeroBook or ChannelStream.IntensityBook or ChannelStream.IntensityBook2)
                {
                    continue;
                }

                double gain = Math.Pow(2, 0.25 * (stream.Scalefactor[index] - 100));
                for (int w = 0; w < info.GroupLength[g]; w++)
                {
                    int window = (windowBase + w) * 128;
                    for (int k = start; k < end; k++)
                    {
                        int value = q[window + k];
                        if (value != 0)
                        {
                            spectrum[window + k] = InverseQuantize(value) * gain;
                        }
                    }
                }
            }

            windowBase += info.GroupLength[g];
        }
    }

    private static readonly double[] PowFourThirds = [.. Enumerable.Range(0, 8192).Select(i => Math.Pow(i, 4.0 / 3))];

    private static double InverseQuantize(int value)
    {
        int magnitude = Math.Abs(value);
        if (magnitude >= PowFourThirds.Length)
        {
            throw new InvalidDataException("Quantized value is out of range.");
        }

        return value < 0 ? -PowFourThirds[magnitude] : PowFourThirds[magnitude];
    }

    /// <summary>Random values with energy 2^(0.5 * energy) (4.6.13.3).</summary>
    private void FillNoise(Span<double> band, int energy)
    {
        double sum = 0;
        for (int i = 0; i < band.Length; i++)
        {
            _noiseState = (_noiseState * 1664525) + 1013904223;
            band[i] = (int)_noiseState;
            sum += band[i] * band[i];
        }

        double scale = Math.Pow(2, 0.25 * energy) / Math.Sqrt(sum);
        for (int i = 0; i < band.Length; i++)
        {
            band[i] *= scale;
        }
    }

    /// <summary>
    /// M/S (4.6.8.1) where signalled and neither channel uses intensity or noise in the band;
    /// intensity stereo (4.6.8.2.3), where the right channel copies the left, scaled; and
    /// correlated noise where both channels use noise and ms_used is set.
    /// </summary>
    private void ApplyStereo(ChannelStream left, ChannelStream right, int msMaskPresent, ReadOnlySpan<bool> msUsed)
    {
        var info = left.Info;
        int windowBase = 0;
        for (int g = 0; g < info.WindowGroups; g++)
        {
            for (int sfb = 0; sfb < info.MaxSfb; sfb++)
            {
                int index = (g * 64) + sfb;
                int rightBook = right.Codebook[index];
                bool intensity = rightBook is ChannelStream.IntensityBook or ChannelStream.IntensityBook2;
                int start = info.SwbOffset[sfb];
                int end = info.SwbOffset[sfb + 1];

                if (intensity)
                {
                    int sign = rightBook == ChannelStream.IntensityBook ? 1 : -1;
                    if (msMaskPresent == 1 && msUsed[index])
                    {
                        sign = -sign;
                    }

                    double scale = sign * Math.Pow(0.5, 0.25 * right.Scalefactor[index]);
                    for (int w = 0; w < info.GroupLength[g]; w++)
                    {
                        int window = (windowBase + w) * 128;
                        for (int k = start; k < end; k++)
                        {
                            right.Spectrum[window + k] = left.Spectrum[window + k] * scale;
                        }
                    }

                    continue;
                }

                bool noise = left.Codebook[index] == ChannelStream.NoiseBook || rightBook == ChannelStream.NoiseBook;
                if (msUsed[index] && left.Codebook[index] == ChannelStream.NoiseBook && rightBook == ChannelStream.NoiseBook)
                {
                    // Correlated noise (4.6.13.3): the right channel reuses the left channel's
                    // random vector, scaled to its own energy.
                    double ratio = Math.Pow(2, 0.25 * (right.Scalefactor[index] - left.Scalefactor[index]));
                    for (int w = 0; w < info.GroupLength[g]; w++)
                    {
                        int window = (windowBase + w) * 128;
                        for (int k = start; k < end; k++)
                        {
                            right.Spectrum[window + k] = left.Spectrum[window + k] * ratio;
                        }
                    }

                    Statistics.CorrelatedNoiseBands++;
                    continue;
                }

                if (!msUsed[index] || noise)
                {
                    continue;
                }

                Statistics.MsBands++;
                for (int w = 0; w < info.GroupLength[g]; w++)
                {
                    int window = (windowBase + w) * 128;
                    for (int k = start; k < end; k++)
                    {
                        double m = left.Spectrum[window + k];
                        double s = right.Spectrum[window + k];
                        left.Spectrum[window + k] = m + s;
                        right.Spectrum[window + k] = m - s;
                    }
                }
            }

            windowBase += info.GroupLength[g];
        }
    }

    /// <summary>Temporal noise shaping (4.6.9): all-pole filters over ranges of the spectrum.</summary>
    private void ApplyTns(ChannelStream stream)
    {
        var tns = stream.Tns;
        if (!tns.Present)
        {
            return;
        }

        var info = stream.Info;
        bool isShort = info.WindowSequence == WindowSequence.EightShort;
        int windows = isShort ? 8 : 1;
        int maxOrder = isShort ? 7 : 12;
        int rate = _config.SampleRateIndex;
        int maxBands = isShort ? AacTables.TnsMaxBandsShort[rate] : AacTables.TnsMaxBandsLong[rate];
        Span<double> lpc = stackalloc double[13];
        Span<double> state = stackalloc double[12];

        for (int w = 0; w < windows; w++)
        {
            int bottom = info.NumSwb;
            for (int f = 0; f < tns.FilterCount[w]; f++)
            {
                var filter = tns.Filters[(w * 4) + f];
                int top = bottom;
                bottom = Math.Max(top - filter.Length, 0);
                int order = Math.Min(filter.Order, maxOrder);
                if (order == 0)
                {
                    continue;
                }

                ComputeLpc(filter, order, lpc);

                int limit = Math.Min(maxBands, info.MaxSfb);
                int start = info.SwbOffset[Math.Min(bottom, limit)];
                int end = info.SwbOffset[Math.Min(top, limit)];
                if (end <= start)
                {
                    continue;
                }

                var spectrum = stream.Spectrum.AsSpan(w * 128);
                int increment = filter.Downward ? -1 : 1;
                int position = filter.Downward ? end - 1 : start;

                // y[n] = x[n] - sum a[i] y[n - i], over the range in the filter's direction.
                state.Clear();
                for (int n = 0; n < end - start; n++, position += increment)
                {
                    double y = spectrum[position];
                    for (int i = 0; i < order; i++)
                    {
                        y -= lpc[i + 1] * state[i];
                    }

                    for (int i = order - 1; i > 0; i--)
                    {
                        state[i] = state[i - 1];
                    }

                    state[0] = y;
                    spectrum[position] = y;
                }
            }
        }
    }

    /// <summary>Inverse quantization of the reflection coefficients and conversion to LPC (4.6.9.3).</summary>
    private static void ComputeLpc(TnsFilter filter, int order, Span<double> a)
    {
        int bits = filter.CoefResolutionBits;
        double iqfac = ((1 << (bits - 1)) - 0.5) / (Math.PI / 2);
        double iqfacMinus = ((1 << (bits - 1)) + 0.5) / (Math.PI / 2);

        Span<double> previous = stackalloc double[13];
        a.Clear();
        a[0] = 1;
        for (int m = 1; m <= order; m++)
        {
            int coef = filter.Coefficients[m - 1];
            double k = Math.Sin(coef / (coef >= 0 ? iqfac : iqfacMinus));
            a[..m].CopyTo(previous);
            a[m] = k;
            for (int i = 1; i < m; i++)
            {
                a[i] = previous[i] + (k * previous[m - i]);
            }
        }
    }

    // ---- State ----

    private sealed class IcsInfo
    {
        public WindowSequence WindowSequence { get; set; }

        public int WindowShape { get; set; }

        public int MaxSfb { get; set; }

        public int WindowGroups { get; set; } = 1;

        public int[] GroupLength { get; } = new int[8];

        public short[] SwbOffset { get; set; } = AacTables.SwbOffsetLong[4];

        public int NumSwb => SwbOffset.Length - 1;

        public void CopyFrom(IcsInfo other)
        {
            WindowSequence = other.WindowSequence;
            WindowShape = other.WindowShape;
            MaxSfb = other.MaxSfb;
            WindowGroups = other.WindowGroups;
            other.GroupLength.CopyTo(GroupLength, 0);
            SwbOffset = other.SwbOffset;
        }
    }

    private sealed class TnsFilter
    {
        public int Length { get; set; }

        public int Order { get; set; }

        public int CoefResolutionBits { get; set; }

        public bool Downward { get; set; }

        public int[] Coefficients { get; } = new int[32];
    }

    private sealed class TnsData
    {
        public bool Present { get; set; }

        public int[] FilterCount { get; } = new int[8];

        public TnsFilter[] Filters { get; } = [.. Enumerable.Range(0, 32).Select(_ => new TnsFilter())];
    }

    /// <summary>One channel's data for the current access unit.</summary>
    private sealed class ChannelStream
    {
        public const int ZeroBook = 0;
        public const int NoiseBook = 13;
        public const int IntensityBook2 = 14;
        public const int IntensityBook = 15;

        public IcsInfo Info { get; } = new();

        /// <summary>Codebook per (group * 64 + band).</summary>
        public int[] Codebook { get; } = new int[8 * 64];

        /// <summary>Scalefactor, intensity position or noise energy per (group * 64 + band).</summary>
        public int[] Scalefactor { get; } = new int[8 * 64];

        public int[] Quantized { get; } = new int[1024];

        public double[] Spectrum { get; } = new double[1024];

        public bool PulsePresent { get; set; }

        public int PulseCount { get; set; }

        public int[] PulseOffset { get; } = new int[4];

        public int[] PulseAmplitude { get; } = new int[4];

        public TnsData Tns { get; } = new();
    }
}

/// <summary>Counts of coding tools seen while decoding.</summary>
internal sealed class AacStatistics
{
    public int LongFrames { get; set; }

    public int StartFrames { get; set; }

    public int ShortFrames { get; set; }

    public int StopFrames { get; set; }

    public int KbdFrames { get; set; }

    public int MsBands { get; set; }

    public int IntensityBands { get; set; }

    public int NoiseBands { get; set; }

    /// <summary>Bands where both channels of a pair use noise and ms_used is set.</summary>
    public int CorrelatedNoiseBands { get; set; }

    public int TnsFilters { get; set; }

    public int PulseFrames { get; set; }
}

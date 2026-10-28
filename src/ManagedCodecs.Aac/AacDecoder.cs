using System.Numerics;
using System.Runtime.ExceptionServices;
using ManagedCodecs.Aac.Sbr;

namespace ManagedCodecs.Aac;

/// <summary>
/// AAC LC and HE-AAC decoder (ISO/IEC 14496-3 subpart 4, and spectral band replication of 4.6.18) for raw access units
/// as stored in MP4, or ADTS frames: one packet in, 1024 samples per channel out for each raw data block, or with SBR 2048 at twice the rate
/// (downsampled SBR, which the config signals by giving SBR the core's own rate: 1024 at the core's).
/// </summary>
/// <remarks>
/// Supports single and channel pair elements, LFE, M/S and intensity stereo, perceptual noise
/// substitution, TNS, pulse data, long and short windows with sine and KBD shapes, and channel
/// configurations 0 (a program config element) to 7. Output is in the WAVE / FFmpeg channel order (5.1: FL FR FC LFE
/// BL BR; configuration 7: FL FR FC LFE BL BR SL SR; a program config element as
/// <see cref="ProgramConfigElement.OutputOrder"/> says). Not supported: other object types (Main, SSR, LTP),
/// coupling channel elements and 960-sample frames. Spectral band replication is signalled in the
/// config or only by its data (from the first access unit on, which <see cref="SampleRate"/> then follows); every channel
/// goes through it, an LFE without a high band. Parametric stereo (HE-AAC v2) makes a mono stream stereo
/// (<see cref="Channels"/>). Every packet yields its samples whole, encoder priming included; the container's timestamps (negative
/// for priming) and the last packet's duration say what to trim, and <see cref="Decode(Packet)"/> drops what
/// <see cref="Packet.SkipSamples"/> says, running on into the next packets if it is more than one packet's. Without one,
/// it drops FAAC's encoder delay, 1024 samples, after an access unit that names FAAC as its encoder, as FFmpeg does.
/// </remarks>
public sealed class AacDecoder : IAudioDecoder
{
    private const int SceElement = 0;
    private const int CpeElement = 1;
    private const int CceElement = 2;
    private const int ExtensionFill = 0;
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

    // SBR state of each SCE and CPE (by their order in the access unit), once SBR data comes.
    private readonly List<SbrElement> _sbr = [];

    // With SBR: each channel's (in bitstream order) QMF banks and HF state, and the SCE or CPE it is in this access unit
    // (-1 for an LFE) and its place there.
    private SbrChannel[]? _sbrChannels;
    private readonly int[] _channelElement;
    private readonly int[] _channelInElement;
    private readonly float[] _core = new float[1024];

    // With Threads above 1: each channel's core output and SBR counters, and the options of the parallel loop.
    private float[][]? _channelCores;
    private AacStatistics[]? _channelStatistics;
    private ParallelOptions? _parallel;
    private readonly int _threads = 1;

    // The packets' time base, for frames' timestamps: the stream's, or without one the output's sample rate.
    private readonly Rational? _timeBase;

    // Samples still to drop at the start of the next packets, from a Packet.SkipSamples larger than its packet's output.
    private int _pendingSkip;

    // FAAC's encoder delay, which it signals only by naming itself in a fill element of its first access unit; and
    // whether the access unit just decoded did.
    private const int FaacDelay = 1024;
    private bool _faacSignature;

    /// <summary>A decoder for the stream <paramref name="audioSpecificConfig"/> describes (ISO/IEC 14496-3 1.6.2.1, as MP4's 'esds' holds it); packet timestamps are then taken as they come.</summary>
    /// <exception cref="InvalidDataException">The config is malformed.</exception>
    /// <exception cref="NotSupportedException">The config names an object type or layout the decoder does not decode.</exception>
    public AacDecoder(ReadOnlySpan<byte> audioSpecificConfig)
    {
        _config = AudioSpecificConfig.Parse(audioSpecificConfig);
        int channels = _config.Channels;
        _filterbanks = [.. Enumerable.Range(0, channels).Select(_ => new Filterbank())];
        _streams = [.. Enumerable.Range(0, channels).Select(_ => new ChannelStream())];
        _outputOrder = _config.Pce?.OutputOrder() ?? OutputOrder(_config.ChannelConfiguration, channels);
        _channelElement = new int[channels];
        _channelInElement = new int[channels];
    }

    /// <summary>A decoder for a demuxer's audio stream: its <see cref="AudioStreamInfo.CodecPrivate"/> is the AudioSpecificConfig, and its time base converts the samples dropped at the start into timestamps.</summary>
    /// <inheritdoc cref="AacDecoder(ReadOnlySpan{byte})" path="/exception"/>
    public AacDecoder(AudioStreamInfo stream)
        : this(stream.CodecPrivate.Span)
    {
        _timeBase = stream.TimeBase;
    }

    /// <summary>The output's sample rate: with SBR twice the core's (or the rate the config gives it), known for SBR signalled only by its data once the first access unit is decoded.</summary>
    public int SampleRate => HasSbr ? _config.SbrRate : _config.SampleRate;

    /// <summary>
    /// The output's channels: the config's, but two for a mono stream with parametric stereo (HE-AAC v2), which its
    /// config says or, signalled only by its data, the first access unit shows.
    /// </summary>
    public int Channels => HasParametricStereo ? 2 : _config.Channels;

    /// <summary>Whether a mono stream is decoded as stereo by its parametric stereo data.</summary>
    private bool HasParametricStereo =>
        _config.Channels == 1 && HasSbr && (_config.PsPresent || (_sbr.Count > 0 && _sbr[0]?.Ps is not null));

    /// <summary>How often each coding tool was used, so tests can check samples exercise them.</summary>
    internal AacStatistics Statistics { get; } = new();

    /// <summary>
    /// Whether the stream has spectral band replication (HE-AAC): its config says so, or SBR data came in a fill
    /// element (implicit signalling).
    /// </summary>
    public bool HasSbr => _config.SbrPresent || _sbr.Count > 0;

    /// <summary>
    /// How many channels of a frame with spectral band replication to put through the filterbank and SBR at once, each
    /// on a thread of the pool: they share nothing, so more threads decode a multichannel HE-AAC stream faster on more
    /// cores and give the same samples (on a 4-core VM, 4 decoded 5.1 about 1.3 times as fast, stereo no faster: a
    /// channel's work, about 70 µs, is not much more than handing it to another core). 1, the default, decodes
    /// on the calling thread, as WebAssembly in a browser needs. AAC LC, whose channels take a fraction of the time, and
    /// parametric stereo (one channel) decode on the calling thread either way.
    /// </summary>
    public int Threads
    {
        get => _threads;
        init => _threads = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "At least one thread is needed.");
    }

    /// <summary>For tests: read SBR data and output the core's samples without it, rather than refusing SBR.</summary>
    internal bool SbrParseOnly { get; init; }

    /// <inheritdoc/>
    public AudioFrame Decode(Packet packet)
    {
        _faacSignature = false;
        var frame = Decode(packet.Data.Span);
        int dropped;
        if (packet.Discard)
        {
            dropped = frame.SampleCount;
        }
        else
        {
            // An access unit that names FAAC as its encoder starts FAAC's delay, as FFmpeg reads it, unless the
            // container says what to skip (an edit list's priming).
            int skip = _pendingSkip + (packet.SkipSamples > 0 ? packet.SkipSamples : _faacSignature ? FaacDelay : 0);
            dropped = Math.Min(skip, frame.SampleCount);
            _pendingSkip = skip - dropped;
        }

        var output = frame.DropStart(dropped);
        output.Pts = packet.Pts + SamplesToTime(dropped);
        return output;
    }

    // Output samples as a duration in the packets' time base, rounded to nearest (halves away from zero).
    private long SamplesToTime(int samples)
    {
        if (samples == 0)
        {
            return 0;
        }

        var timeBase = _timeBase ?? new Rational(1, SampleRate);
        long numerator = (long)samples * timeBase.Denominator;
        long denominator = (long)SampleRate * timeBase.Numerator;
        return (numerator + (denominator / 2)) / denominator;
    }

    /// <summary>
    /// Decodes one access unit: a raw one as stored in MP4, or an ADTS frame (header included, as the ADTS demuxer gives
    /// them), whose header must match the decoder's configuration; a frame of several raw data blocks gives all their
    /// samples in one frame.
    /// </summary>
    public AudioFrame Decode(ReadOnlySpan<byte> packet)
    {
        // A raw access unit can't pass for an ADTS frame: one starting with 0xFF starts with an END element.
        if (!AdtsHeader.TryParse(packet, out var adts) || adts.FrameLength != packet.Length)
        {
            return DecodeBlock(new AacBitReader(packet));
        }

        if (adts.Profile + 1 != _config.ObjectType || adts.SampleRateIndex != _config.SampleRateIndex || adts.ChannelConfiguration != _config.ChannelConfiguration)
        {
            throw new NotSupportedException("An ADTS header that changes the stream's configuration is not supported.");
        }

        var r = new AacBitReader(packet[adts.HeaderLength..]);
        if (adts.RawDataBlocks == 1)
        {
            return DecodeBlock(r);
        }

        // Several raw data blocks (1.A.3.2.1): each ends byte-aligned, followed by its CRC when the frame has them (the
        // header's then gives the blocks' positions, which reading them in turn makes unneeded).
        var blocks = new AudioFrame[adts.RawDataBlocks];
        for (int i = 0; i < blocks.Length; i++)
        {
            blocks[i] = DecodeBlock(r);
            r.ByteAlign();
            r.Skip(adts.HasCrc ? 16 : 0);
        }

        int count = blocks[0].SampleCount;
        var frame = new AudioFrame(blocks[0].SampleRate, blocks[0].Channels, count * blocks.Length);
        for (int i = 0; i < blocks.Length; i++)
        {
            if (blocks[i].SampleCount != count || blocks[i].Channels != frame.Channels)
            {
                throw new NotSupportedException("SBR signalled only by its data, first inside a frame of several raw data blocks, is not supported.");
            }

            for (int c = 0; c < frame.Channels; c++)
            {
                blocks[i].Samples[c].CopyTo(frame.Samples[c].AsSpan(i * count));
            }
        }

        return frame;
    }

    // raw_data_block (4.4.2.1): its elements up to END.
    private AudioFrame DecodeBlock(AacBitReader r)
    {
        foreach (var sbr in _sbr)
        {
            sbr?.StartFrame();
        }

        int channel = 0;
        int element = -1; // the SCE or CPE an SBR payload belongs to: the one before it
        int elementChannels = 0;
        int elements = 0;
        while (true)
        {
            int elementType = r.Read(3);
            if (elementType == EndElement)
            {
                break;
            }

            if (elementType != FilElement)
            {
                element = elementType is SceElement or CpeElement ? elements++ : -1;
            }

            switch (elementType)
            {
                case SceElement or LfeElement:
                    r.Read(4); // element_instance_tag
                    CheckChannels(channel + 1);
                    DecodeIndividualStream(r, _streams[channel], commonWindow: false);
                    (_channelElement[channel], _channelInElement[channel]) = (element, 0);
                    channel++;
                    elementChannels = 1;
                    break;

                case CpeElement:
                    r.Read(4);
                    CheckChannels(channel + 2);
                    DecodeChannelPair(r, _streams[channel], _streams[channel + 1]);
                    (_channelElement[channel], _channelInElement[channel]) = (element, 0);
                    (_channelElement[channel + 1], _channelInElement[channel + 1]) = (element, 1);
                    channel += 2;
                    elementChannels = 2;
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
                    if (ProgramConfigElement.Parse(r).Channels != _config.Channels)
                    {
                        throw new NotSupportedException("A program config element that changes the stream's channels is not supported.");
                    }

                    break;

                case FilElement:
                    {
                        // Extension payloads: SBR data (for the SCE or CPE before it) is read, the others (dynamic
                        // range, fill) skipped.
                        int count = r.Read(4);
                        if (count == 15)
                        {
                            count += r.Read(8) - 1;
                        }

                        int end = r.Position + (count * 8);
                        if (count > 0 && r.Peek(4) is SbrElement.ExtensionSbr or SbrElement.ExtensionSbrCrc && element >= 0)
                        {
                            int type = r.Read(4);
                            SbrFor(element, elementChannels).Read(r, end - r.Position, type == SbrElement.ExtensionSbrCrc, Statistics);
                        }
                        else if (count > 0 && r.Peek(4) == ExtensionFill)
                        {
                            _faacSignature |= NamesFaac(r, end);
                        }

                        r.Skip(end - r.Position);
                        break;
                    }
            }
        }

        if (channel != _config.Channels)
        {
            throw new InvalidDataException($"The access unit has {channel} channels, the configuration {_config.Channels}.");
        }

        if (HasSbr && !SbrParseOnly)
        {
            return DecodeWithSbr();
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

    /// <summary>
    /// Whether a fill extension payload (EXT_FILL, 4.4.2.7), from its type on to <paramref name="end"/>, holds FAAC's
    /// encoder name: "libfaac" and a version, as text 13 bits after the type where FAAC writes it (and FFmpeg reads it;
    /// the spec has fill bits there). It reads only within the payload, which the caller then skips to its end.
    /// </summary>
    private static bool NamesFaac(AacBitReader r, int end)
    {
        const int Offset = 4 + 13;
        if (end - r.Position < Offset + (7 * 8))
        {
            return false;
        }

        r.Skip(Offset);
        Span<byte> text = stackalloc byte[Math.Min((end - r.Position) / 8, 255)];
        for (int i = 0; i < text.Length; i++)
        {
            text[i] = (byte)r.Read(8);
        }

        // "libfaac %d.%d" as FFmpeg scans it: the name, white space, digits, a point, digits.
        if (!text.StartsWith("libfaac"u8))
        {
            return false;
        }

        int p = 7;
        while (p < text.Length && text[p] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\v' or (byte)'\f')
        {
            p++;
        }

        int digits = p;
        while (p < text.Length && char.IsAsciiDigit((char)text[p]))
        {
            p++;
        }

        if (p == digits || p >= text.Length || text[p] != '.')
        {
            return false;
        }

        p++;
        return p < text.Length && char.IsAsciiDigit((char)text[p]);
    }

    // Each channel's core output through SBR, 2048 samples at its rate: an SCE's or CPE's with its SBR data of this access
    // unit, an LFE's (and any channel's without SBR data) with no high band.
    private AudioFrame DecodeWithSbr()
    {
        _sbrChannels ??= [.. Enumerable.Range(0, _config.Channels).Select(_ => new SbrChannel(_config.DownsampledSbr))];
        var frame = new AudioFrame(SampleRate, Channels, _config.DownsampledSbr ? 1024 : 2048);
        if (HasParametricStereo)
        {
            var stream = _streams[0];
            ApplyTns(stream);
            _filterbanks[0].Synthesize(stream.Info.WindowSequence, stream.Info.WindowShape, stream.Spectrum, _core);
            var sbr = _sbr.Count > 0 ? _sbr[0] : null;
            _sbrChannels[0].Process(sbr, 0, _core, frame.Samples[0], frame.Samples[1], Statistics);
            return frame;
        }

        if (_threads == 1 || _config.Channels == 1)
        {
            for (int output = 0; output < _config.Channels; output++)
            {
                SynthesizeWithSbr(output, _core, frame, Statistics);
            }

            return frame;
        }

        // Each channel's own core buffer and counters, the counters added up after: nothing else is shared.
        _channelCores ??= [.. Enumerable.Range(0, _config.Channels).Select(_ => new float[1024])];
        _channelStatistics ??= [.. Enumerable.Range(0, _config.Channels).Select(_ => new AacStatistics())];
        _parallel ??= new ParallelOptions { MaxDegreeOfParallelism = _threads };
        try
        {
            Parallel.For(0, _config.Channels, _parallel, output => SynthesizeWithSbr(output, _channelCores[output], frame, _channelStatistics[output]));
        }
        catch (AggregateException e) when (e.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Throw(e.InnerExceptions[0]); // as on one thread
        }

        foreach (var statistics in _channelStatistics)
        {
            Statistics.AddSbrChannel(statistics);
        }

        return frame;
    }

    // One output channel's core samples (into core) and SBR output (into the frame).
    private void SynthesizeWithSbr(int output, float[] core, AudioFrame frame, AacStatistics statistics)
    {
        int source = _outputOrder[output];
        var stream = _streams[source];
        ApplyTns(stream);
        _filterbanks[source].Synthesize(stream.Info.WindowSequence, stream.Info.WindowShape, stream.Spectrum, core);
        int element = _channelElement[source];
        var sbr = element >= 0 && element < _sbr.Count ? _sbr[element] : null;
        _sbrChannels![source].Process(sbr, _channelInElement[source], core, frame.Samples[output], statistics);
    }

    // The SBR state of SCE or CPE number index, made when its first SBR data comes; at the rate the config gives SBR,
    // else twice the core's.
    private SbrElement SbrFor(int index, int channels)
    {
        while (_sbr.Count <= index)
        {
            _sbr.Add(null!);
        }

        if (_sbr[index] is not { } sbr || sbr.Channels.Length != channels)
        {
            _sbr[index] = sbr = new SbrElement(channels, _config.DownsampledSbr ? 2 * _config.SampleRate : _config.SbrRate);
        }

        return sbr;
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

        // The spec calls configuration 7's second pair "outside front"; FFmpeg reads the pairs as
        // front, side and back and outputs 7.1, and so do we, so FFmpeg's 7.1 files round-trip.
        7 => [1, 2, 0, 7, 5, 6, 3, 4],    // C, L R, Ls Rs, Lb Rb, LFE -> FL FR FC LFE BL BR SL SR
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

                double gain = Gains[stream.Scalefactor[index]];
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

    // 2^0, 2^0.25, 2^0.5 and 2^0.75, correctly rounded (before Gains, which they make).
    private static readonly double[] QuarterPowers = [1.0, 1.1892071150027210667, 1.4142135623730950488, 1.6817928305074290861];

    private static readonly double[] PowFourThirds = [.. Enumerable.Range(0, 8192).Select(FourThirdsPower)];

    // 2^(0.25 (sf - 100)) for scalefactors 0..255.
    private static readonly double[] Gains = [.. Enumerable.Range(0, 256).Select(sf => QuarterPowerOfTwo(sf - 100))];

    /// <summary>
    /// x^(4/3) as x cbrt(x), the cube root by Newton's method from a power of two above it, in additions,
    /// multiplications and divisions alone: the same bits on every runtime, within 1.5 units in the last place of the
    /// exact value. Math.Pow(x, 4.0 / 3) is not (WebAssembly's differs from glibc's in the last bit at 7 of the 8192
    /// values, so a stream reaching one decodes differently there), and nor is its exponent quite 4/3.
    /// </summary>
    internal static double FourThirdsPower(int x)
    {
        if (x == 0)
        {
            return 0;
        }

        double y = Math.ScaleB(1.0, (32 - BitOperations.LeadingZeroCount((uint)x) + 2) / 3);
        for (int i = 0; i < 8; i++)
        {
            y -= ((y * y * y) - x) / (3 * y * y);
        }

        return x * y;
    }

    private static double InverseQuantize(int value)
    {
        int magnitude = Math.Abs(value);
        if (magnitude >= PowFourThirds.Length)
        {
            throw new InvalidDataException("Quantized value is out of range.");
        }

        return value < 0 ? -PowFourThirds[magnitude] : PowFourThirds[magnitude];
    }

    /// <summary>2^(<paramref name="quarters"/> / 4), correctly rounded and the same bits on every runtime, without Math.Pow.</summary>
    internal static double QuarterPowerOfTwo(int quarters) => Math.ScaleB(QuarterPowers[quarters & 3], quarters >> 2);

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

        double scale = QuarterPowerOfTwo(energy) / Math.Sqrt(sum);
        for (int i = 0; i < band.Length; i++)
        {
            band[i] *= scale;
        }
    }

    /// <summary>
    /// M/S (4.6.8.1) where signalled and neither channel uses intensity or noise in the band;
    /// intensity stereo (4.6.8.2.3), where the right channel copies the left, scaled. Where both
    /// channels use noise and ms_used is set, the spec has the right channel reuse the left's
    /// random vector (correlated noise, 4.6.13.3); FFmpeg keeps the right channel's own, and so do we.
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

                    double scale = sign * QuarterPowerOfTwo(-right.Scalefactor[index]);
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
                    Statistics.CorrelatedNoiseBands++;
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
    /// <summary>Adds what one channel's SBR processing (<see cref="Sbr.SbrChannel.Process(Sbr.SbrElement?, int, ReadOnlySpan{float}, Span{float}, AacStatistics)"/>) counted to these, and clears them there.</summary>
    public void AddSbrChannel(AacStatistics channel)
    {
        SbrChirpBands += channel.SbrChirpBands;
        SbrTransientEnvelopes += channel.SbrTransientEnvelopes;
        SbrSmoothedEnvelopes += channel.SbrSmoothedEnvelopes;
        SbrSinusoids += channel.SbrSinusoids;
        SbrLimitedGains += channel.SbrLimitedGains;
        channel.SbrChirpBands = channel.SbrTransientEnvelopes = channel.SbrSmoothedEnvelopes = channel.SbrSinusoids = channel.SbrLimitedGains = 0;
    }

    public int LongFrames { get; set; }

    public int StartFrames { get; set; }

    public int ShortFrames { get; set; }

    public int StopFrames { get; set; }

    public int KbdFrames { get; set; }

    public int MsBands { get; set; }

    public int IntensityBands { get; set; }

    public int NoiseBands { get; set; }

    /// <summary>
    /// Bands where both channels of a pair use noise and ms_used is set: the spec's correlated noise, decoded as FFmpeg
    /// decodes it, each channel with noise of its own.
    /// </summary>
    public int CorrelatedNoiseBands { get; set; }

    public int TnsFilters { get; set; }

    public int PulseFrames { get; set; }

    /// <summary>SBR: frames read, headers, resets (frequency tables made), frames of each class, coupled pairs.</summary>
    public int SbrFrames { get; set; }

    public int SbrHeaders { get; set; }

    public int SbrResets { get; set; }

    public int[] SbrFrameClasses { get; } = new int[4];

    public int SbrCoupledFrames { get; set; }

    /// <summary>Envelopes coded at 1.5 dB, bands with a sinusoid added, frames with parametric stereo data.</summary>
    public int SbrFineAmpEnvelopes { get; set; }

    public int SbrHarmonics { get; set; }

    public int SbrParametricStereoFrames { get; set; }

    /// <summary>The most bits of an SBR payload left after its data: the fill to a whole byte, so under 8.</summary>
    public int SbrMostUnreadBits { get; set; }

    /// <summary>The bs_freq_scale values seen, as bits.</summary>
    public int SbrFrequencyScales { get; set; }

    /// <summary>The bs_limiter_bands and bs_limiter_gains values seen, as bits, and headers without frequency interpolation.</summary>
    public int SbrLimiterBands { get; set; }

    public int SbrLimiterGains { get; set; }

    public int SbrUninterpolatedHeaders { get; set; }

    /// <summary>Noise bands (per channel and frame) whose chirp factor is not 0, so that the HF generator filters them.</summary>
    public int SbrChirpBands { get; set; }

    /// <summary>Envelopes that start on a transient or right after one (no noise, no smoothing).</summary>
    public int SbrTransientEnvelopes { get; set; }

    /// <summary>Envelopes whose gains are smoothed over time.</summary>
    public int SbrSmoothedEnvelopes { get; set; }

    /// <summary>Sinusoids added, per subband and envelope.</summary>
    public int SbrSinusoids { get; set; }

    /// <summary>Gains the limiter cut, per subband and envelope.</summary>
    public int SbrLimitedGains { get; set; }

    /// <summary>Parametric stereo: frames and headers read, the iid_mode and icc_mode values seen (as bits), envelopes, frames with variable borders, values coded in time, extensions skipped, and the most bits left after the data in an SBR extension (its padding).</summary>
    public int PsFrames { get; set; }

    public int PsHeaders { get; set; }

    public int PsIidModes { get; set; }

    public int PsIccModes { get; set; }

    public int PsEnvelopes { get; set; }

    public int PsVariableBorderFrames { get; set; }

    public int PsTimeDeltas { get; set; }

    public int PsExtensions { get; set; }

    public int PsMostUnreadBits { get; set; }

    /// <summary>Frames mixed into two channels by parametric stereo (with this frame's data or the last).</summary>
    public int PsAppliedFrames { get; set; }

    /// <summary>Of those, frames mixed by procedure Rb (icc_mode 3 to 5) rather than Ra.</summary>
    public int PsRbFrames { get; set; }

    /// <summary>Time slots and bands whose decorrelated signal a transient attenuated.</summary>
    public int PsDuckedSlots { get; set; }
}

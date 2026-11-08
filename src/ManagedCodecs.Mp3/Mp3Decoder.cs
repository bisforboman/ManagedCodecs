namespace ManagedCodecs.Mp3;

/// <summary>
/// Decodes MPEG audio Layer III (MP3, ISO/IEC 11172-3; its lower sample rates from 13818-3, and MPEG-2.5's) to
/// 32-bit float samples: mono, stereo, dual channel and joint stereo (mid/side and intensity), long, short and mixed
/// blocks, and the bit reservoir. Checked against FFmpeg's decoder sample by sample.
/// </summary>
/// <remarks>
/// Each packet holds whole frames (MP4 and Matroska give one a packet), of 1152 samples (576 at 24 kHz and below). A
/// frame's main data may start in earlier frames' (the bit reservoir), which the decoder keeps; where it reaches back
/// past what came before (the first packet of a stream cut from a longer one, or after a seek), the granules whose data
/// is missing come out silent, as FFmpeg's do. Every packet yields its samples whole, the encoder's delay included;
/// <see cref="Decode(Packet)"/> drops what <see cref="Packet.SkipSamples"/> says (an MP4 edit list's priming), running
/// on into the next packets, and the last <see cref="Packet.SkipEndSamples"/>. Not supported: free-format bitrates and
/// Layers I and II.
/// </remarks>
public sealed class Mp3Decoder : IAudioDecoder
{
    // FFmpeg keeps at most this much of the reservoir (main_data_begin reaches back at most 511 bytes).
    private const int ReservoirLimit = 512;

    private readonly Rational? _timeBase;
    private readonly Granule[,] _granules = { { new(), new() }, { new(), new() } };

    // Per channel: the scalefactors of granules 0 and 1 (scfsi copies granule 0's), the intensity channel's illegal
    // positions, the Huffman-coded lines, the spectrum, the hybrid filterbank's overlap and output, and the synthesis.
    private readonly int[][][] _scalefactors = [[new int[40], new int[40]], [new int[40], new int[40]]];
    private readonly bool[] _illegal = new bool[40];
    private readonly bool[] _scratchIllegal = new bool[40];
    private readonly int[] _lines = new int[576];
    private readonly double[][] _spectra = [new double[576], new double[576]];
    private readonly double[][] _overlap = [new double[576], new double[576]];
    private readonly double[] _hybrid = new double[576];
    private readonly Synthesis[] _synthesis = [new(), new()];

    // The main data after the last granule read, which the next frame's main_data_begin may reach into.
    private byte[] _reservoir = new byte[ReservoirLimit + 2048];
    private int _reservoirLength;

    // Samples still to drop at the start of the next packets, from a Packet.SkipSamples larger than its packet's output.
    private int _pendingSkip;

    /// <summary>A decoder; the rate and channels come from the first frame (MP3 has no codec configuration).</summary>
    public Mp3Decoder()
    {
    }

    /// <summary>A decoder for a demuxer's audio stream, whose time base converts the samples dropped at the start into timestamps.</summary>
    public Mp3Decoder(AudioStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _timeBase = stream.TimeBase;
    }

    /// <summary>The sample rate, from the first frame decoded (0 before one).</summary>
    public int SampleRate { get; private set; }

    /// <summary>The channel count (1 or 2), from the first frame decoded (0 before one).</summary>
    public int Channels { get; private set; }

    /// <summary>For tests: what the frames decoded so far used.</summary>
    internal Mp3Statistics Statistics { get; } = new();

    /// <inheritdoc/>
    public AudioFrame Decode(Packet packet)
    {
        var frame = Decode(packet.Data.Span);
        int dropped;
        if (packet.Discard)
        {
            dropped = frame.SampleCount;
        }
        else
        {
            int skip = _pendingSkip + Math.Max(0, packet.SkipSamples);
            dropped = Math.Min(skip, frame.SampleCount);
            _pendingSkip = skip - dropped;
        }

        var output = frame.Drop(dropped, packet.Discard ? 0 : packet.SkipEndSamples);
        output.Pts = packet.Pts + SamplesToTime(dropped);
        return output;
    }

    /// <summary>
    /// Decodes the frames of one packet into one audio frame of all their samples; every frame of a stream must have the
    /// same rate and channel count.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not Layer III frames, a CRC doesn't match, or a frame is damaged.</exception>
    /// <exception cref="NotSupportedException">The rate or channel count changes, or a frame has a free-format bitrate.</exception>
    public AudioFrame Decode(ReadOnlySpan<byte> packet)
    {
        var frames = new List<float[][]>(1);
        int offset = 0;
        while (offset < packet.Length)
        {
            if (!FrameHeader.TryParse(packet[offset..], out var header))
            {
                if (offset > 0 && packet[offset..].IndexOfAnyExcept((byte)0) < 0)
                {
                    break; // padding after the last frame
                }

                throw new InvalidDataException(packet.Length - offset >= 4 && packet[offset] == 0xFF && (packet[offset + 1] & 0xE0) == 0xE0 && ((packet[offset + 2] >> 4) == 0)
                    ? "A free-format MP3 bitrate is not supported."
                    : "Not an MPEG audio Layer III frame.");
            }

            int length = header.FrameLength;
            if (length > packet.Length - offset)
            {
                throw new InvalidDataException("An MP3 frame is cut short.");
            }

            if (Channels == 0)
            {
                SampleRate = header.SampleRate;
                Channels = header.Channels;
            }
            else if (header.SampleRate != SampleRate || header.Channels != Channels)
            {
                throw new NotSupportedException("An MP3 stream whose sample rate or channel count changes is not supported.");
            }

            frames.Add(DecodeFrame(header, packet.Slice(offset, length)));
            offset += length;
        }

        if (frames.Count == 0)
        {
            throw new InvalidDataException("An MP3 packet holds no frame.");
        }

        int samples = frames[0][0].Length;
        var output = new AudioFrame(SampleRate, Channels, samples * frames.Count);
        for (int f = 0; f < frames.Count; f++)
        {
            for (int c = 0; c < Channels; c++)
            {
                frames[f][c].CopyTo(output.Samples[c], f * samples);
            }
        }

        return output;
    }

    private float[][] DecodeFrame(FrameHeader header, ReadOnlySpan<byte> frame)
    {
        int channels = header.Channels;
        int sideStart = FrameHeader.Length + (header.HasCrc ? 2 : 0);
        int mainStart = sideStart + header.SideInfoLength;
        if (mainStart > frame.Length)
        {
            throw new InvalidDataException("An MP3 frame is shorter than its side information.");
        }

        if (header.HasCrc && Crc16(frame, header.SideInfoLength) != ((frame[4] << 8) | frame[5]))
        {
            throw new InvalidDataException("An MP3 frame's CRC does not match.");
        }

        Statistics.Count(header);
        var side = frame.Slice(sideStart, header.SideInfoLength).ToArray();
        var r = new BitReader(side, side.Length);
        int mainDataBegin = r.Read(header.Lsf ? 8 : 9);
        r.Position += header.Lsf ? (channels == 1 ? 1 : 2) : (channels == 1 ? 5 : 3); // private bits
        if (!header.Lsf)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                _granules[0, ch].Scfsi = _granules[1, ch].Scfsi = r.Read(4);
            }
        }

        for (int gr = 0; gr < header.Granules; gr++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                _granules[gr, ch].Read(ref r, header.Lsf, header.RateIndex);
                Statistics.Count(_granules[gr, ch]);
            }
        }

        // The main data: the reservoir's last main_data_begin bytes, then this frame's after its side information. Where
        // the reservoir is shorter, the granules whose data would lie in the missing part are skipped, silent, by their
        // part2_3_length (FFmpeg's decoder does this).
        var main = frame[mainStart..];
        EnsureReservoir(_reservoirLength + main.Length);
        main.CopyTo(_reservoir.AsSpan(_reservoirLength));
        int available = _reservoirLength + main.Length;
        int startBit = (_reservoirLength - mainDataBegin) * 8;
        int firstGranule = 0;
        if (startBit < 0)
        {
            Statistics.MissingReservoir++;
            int bits = _reservoirLength * 8;
            while (firstGranule < header.Granules && bits < mainDataBegin * 8)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    bits += _granules[firstGranule, ch].Part23Length;
                }

                firstGranule++;
            }

            startBit = Math.Max(0, bits - (mainDataBegin * 8)); // 0 if every granule's data is missing
        }
        else if (mainDataBegin > 0)
        {
            Statistics.ReservoirFrames++;
        }

        var reader = new BitReader(_reservoir, available, startBit);
        var output = new float[channels][];
        for (int ch = 0; ch < channels; ch++)
        {
            output[ch] = new float[header.SamplesPerFrame];
        }

        Span<double> slot = stackalloc double[32];
        for (int gr = 0; gr < header.Granules; gr++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                if (gr < firstGranule)
                {
                    Array.Clear(_spectra[ch]);
                }
                else
                {
                    DecodeChannel(header, gr, ch, ref reader, available * 8);
                }
            }

            if (gr >= firstGranule && channels == 2 && header.Mode == FrameHeader.JointStereo)
            {
                var right = _granules[gr, 1];
                Stereo.Process(header, right, _scalefactors[1][gr], _illegal, _spectra[0], _spectra[1]);
            }

            for (int ch = 0; ch < channels; ch++)
            {
                var g = _granules[gr, ch];
                Hybrid.Process(g, header.Lsf, header.RateIndex, _spectra[ch], _overlap[ch], _hybrid);
                for (int t = 0; t < 18; t++)
                {
                    for (int sb = 0; sb < 32; sb++)
                    {
                        slot[sb] = _hybrid[(18 * sb) + t];
                    }

                    _synthesis[ch].Process(slot, output[ch].AsSpan((576 * gr) + (32 * t), 32));
                }
            }
        }

        // Keep the main data after the last granule read, at most the reservoir's limit of it.
        int used = Math.Min((reader.Position + 7) >> 3, available);
        int keep = Math.Min(available - used, ReservoirLimit);
        _reservoir.AsSpan(available - keep, keep).CopyTo(_reservoir);
        _reservoirLength = keep;
        return output;
    }

    private void DecodeChannel(FrameHeader header, int gr, int ch, ref BitReader reader, int availableBits)
    {
        var g = _granules[gr, ch];
        int start = reader.Position;
        int end = start + g.Part23Length;
        if (end > availableBits)
        {
            throw new InvalidDataException("An MP3 granule's data runs past its frame.");
        }

        var scalefactors = _scalefactors[ch][gr];
        if (header.Lsf)
        {
            bool intensityChannel = header.Intensity && ch == 1;
            Spectrum.ReadLsfScalefactors(ref reader, g, intensityChannel, scalefactors, intensityChannel ? _illegal : _scratchIllegal);
        }
        else
        {
            Spectrum.ReadMpeg1Scalefactors(ref reader, g, gr, scalefactors, _scalefactors[ch][0]);
        }

        if (reader.Position > end)
        {
            throw new InvalidDataException("An MP3 granule's scalefactors run past its length.");
        }

        int count = Spectrum.ReadLines(ref reader, g, end, _lines, Statistics);
        Spectrum.Requantize(g, header.Lsf, header.RateIndex, _lines, count, scalefactors, _spectra[ch]);
    }

    private void EnsureReservoir(int length)
    {
        if (_reservoir.Length < length)
        {
            Array.Resize(ref _reservoir, Math.Max(length, _reservoir.Length * 2));
        }
    }

    // CRC-16 (x^16 + x^15 + x^2 + 1, from all ones) over the header's last two bytes and the side information
    // (2.4.3.1).
    internal static int Crc16(ReadOnlySpan<byte> frame, int sideInfoLength)
    {
        int crc = 0xFFFF;
        void Add(byte b)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                int top = ((crc >> 15) ^ (b >> bit)) & 1;
                crc = ((crc << 1) & 0xFFFF) ^ (top != 0 ? 0x8005 : 0);
            }
        }

        Add(frame[2]);
        Add(frame[3]);
        foreach (byte b in frame.Slice(6, sideInfoLength))
        {
            Add(b);
        }

        return crc;
    }

    // Output samples as a duration in the packets' time base, rounded to nearest (halves away from zero).
    private long SamplesToTime(int samples)
    {
        if (samples == 0 || SampleRate == 0)
        {
            return 0;
        }

        var timeBase = _timeBase ?? new Rational(1, SampleRate);
        long numerator = (long)samples * timeBase.Denominator;
        long denominator = (long)SampleRate * timeBase.Numerator;
        return (numerator + (denominator / 2)) / denominator;
    }
}

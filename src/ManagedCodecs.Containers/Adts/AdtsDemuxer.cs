namespace ManagedCodecs.Containers;

/// <summary>
/// Raw AAC in ADTS frames (ISO/IEC 14496-3 1.A.3, the usual .aac file): one audio stream, a packet per frame, the ADTS
/// header included as in FFmpeg's packets (<c>AacDecoder</c> skips it), 1024 samples long for each of its raw data blocks.
/// </summary>
/// <remarks>
/// <para>
/// ID3v2 tags before the first frame are skipped, and anything after the last frame that does not start a frame (an
/// ID3v1 or APE tag) ends the stream. Bytes between frames that are not a frame are skipped up to the next header that
/// is followed by another one (or by the end of the file), as FFmpeg resynchronizes. A file that breaks off inside a
/// frame gives the packets before it, then <see cref="InvalidDataException"/> (FFmpeg passes the partial frame on).
/// </para>
/// <para>
/// Timestamps count samples (<see cref="AudioStreamInfo.TimeBase"/> is one over the sample rate; FFmpeg's is 1/28224000,
/// which fits every rate, for the same times). <see cref="AudioStreamInfo.CodecPrivate"/> is the AudioSpecificConfig
/// the first header stands for, where FFmpeg has none (its decoder reads the headers); with channel configuration 0, the
/// program config element at the start of the first frame's data follows it there, as in an MP4 file (that frame must
/// start with one, as both encoders' first frames do).
/// </para>
/// </remarks>
public sealed class AdtsDemuxer : IDemuxer
{
    private const int SamplesPerBlock = 1024;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    // The frames found when opening (position, length, raw data blocks), and where one that runs past the end of the file
    // starts (-1 if none does).
    private readonly List<(long Position, int Length, int Blocks)> _frames = [];
    private readonly long _truncatedFrame = -1;

    /// <summary>Reads the file's headers (and finds every frame); packets are read from the stream later, by <see cref="ReadPackets"/>.</summary>
    /// <param name="stream">A seekable stream holding the file.</param>
    /// <param name="leaveOpen">Whether <see cref="Dispose"/> leaves <paramref name="stream"/> open.</param>
    /// <exception cref="ArgumentException">The stream is not seekable.</exception>
    /// <exception cref="InvalidDataException">The file is not ADTS, or is malformed.</exception>
    public AdtsDemuxer(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;

        try
        {
            long start = SkipId3v2(_stream);
            if (!TryReadHeader(_stream, start, out var first) || !FollowedByFrame(_stream, start, first))
            {
                throw new InvalidDataException("Not an ADTS file.");
            }

            int channels = first.ChannelConfiguration == 7 ? 8 : first.ChannelConfiguration;
            byte[] config = first.AudioSpecificConfig();
            if (first.ChannelConfiguration == 0)
            {
                // The channels come from the program config element that starts the first frame's data.
                var frame = new byte[first.FrameLength];
                _stream.Position = start;
                _stream.ReadAtLeast(frame, frame.Length, throwOnEndOfStream: false);
                if (!first.TryProgramConfig(frame, out config, out channels))
                {
                    throw new NotSupportedException("ADTS with channel configuration 0 and no program config element at the start of its first frame is not supported.");
                }
            }

            for (long pos = start; FindFrame(ref pos, out var header); pos += header.FrameLength)
            {
                if (pos + header.FrameLength > _stream.Length)
                {
                    _truncatedFrame = pos;
                    break;
                }

                _frames.Add((pos, header.FrameLength, header.RawDataBlocks));
            }

            long frames = _frames.Count;

            int sampleRate = SampleRates[first.SampleRateIndex];
            Audio = new AudioStreamInfo(CodecIds.Aac, sampleRate, channels, frames, new Rational(1, sampleRate), config, 0);
        }
        catch (Exception e) when (Malformed.IsMalformedInput(e))
        {
            throw Malformed.Wrap(e, "ADTS");
        }
    }

    /// <summary>Always null: an ADTS file is audio only.</summary>
    public VideoStreamInfo? Video => null;

    /// <summary>The AAC stream; its AudioSpecificConfig is made from the first frame's header.</summary>
    public AudioStreamInfo Audio { get; }

    // Table 1.18's samplingFrequencyIndex.
    private static ReadOnlySpan<int> SampleRates => [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// <summary>Opens the file at <paramref name="path"/>, which the demuxer disposes with itself.</summary>
    public static AdtsDemuxer Open(string path) => new(File.OpenRead(path));

    /// <inheritdoc cref="AdtsDemuxer"/>
    public IEnumerable<Packet> ReadPackets() => ReadSteps.Run(_stream, Steps(), "ADTS");

    /// <inheritdoc cref="AviDemuxer.ReadPacketsAsync"/>
    public IAsyncEnumerable<Packet> ReadPacketsAsync(CancellationToken cancellationToken = default) => ReadSteps.RunAsync(_stream, Steps(), "ADTS", cancellationToken);

    /// <summary>Disposes the stream, unless the demuxer was made to leave it open.</summary>
    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    /// <summary>
    /// Whether <paramref name="stream"/> starts (after any ID3v2 tags) with an ADTS frame followed by another one or by
    /// the end of the file. Leaves the position at 0.
    /// </summary>
    internal static bool Probe(Stream stream)
    {
        try
        {
            long start = SkipId3v2(stream);
            return TryReadHeader(stream, start, out var header) && FollowedByFrame(stream, start, header);
        }
        catch (Exception e) when (Malformed.IsMalformedInput(e))
        {
            return false;
        }
        finally
        {
            stream.Position = 0;
        }
    }

    // Each frame's read and packet, then the error for a frame that runs past the end of the file.
    private IEnumerable<ReadStep> Steps()
    {
        long sequence = 0;
        long time = 0;
        foreach (var (pos, length, blocks) in _frames)
        {
            var data = new byte[length];
            yield return ReadStep.Read(pos, data);
            long duration = (long)SamplesPerBlock * blocks;
            yield return ReadStep.Yield(new Packet(0, sequence++, data, IsKeyframe: true, time, time, duration));
            time += duration;
        }

        if (_truncatedFrame >= 0)
        {
            throw new InvalidDataException($"The frame at byte {_truncatedFrame} runs past the end of the file.");
        }
    }

    // The position after the ID3v2 tags at the start of the stream (ID3v2.4 3.1: "ID3", version, flags, a syncsafe
    // size, and a footer of 10 bytes more when flag 0x10 is set).
    private static long SkipId3v2(Stream stream)
    {
        Span<byte> tag = stackalloc byte[10];
        long pos = 0;
        while (true)
        {
            stream.Position = pos;
            if (stream.ReadAtLeast(tag, tag.Length, throwOnEndOfStream: false) < tag.Length || !tag[..3].SequenceEqual("ID3"u8)
                || ((tag[6] | tag[7] | tag[8] | tag[9]) & 0x80) != 0)
            {
                return pos;
            }

            long size = (tag[6] << 21) | (tag[7] << 14) | (tag[8] << 7) | tag[9];
            pos += 10 + size + ((tag[5] & 0x10) != 0 ? 10 : 0);
        }
    }

    /// <summary>
    /// The frame at <paramref name="pos"/>, or else the next one past bytes that are not a frame: a header followed by
    /// another one or by the end of the file. False at the end of the frames.
    /// </summary>
    private bool FindFrame(ref long pos, out AdtsHeader header)
    {
        if (TryReadHeader(_stream, pos, out header))
        {
            return true;
        }

        var buffer = new byte[4096];
        for (long chunk = pos + 1; chunk < _stream.Length; chunk += buffer.Length - 1)
        {
            _stream.Position = chunk;
            int read = _stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            for (int i = 0; i + 1 < read; i++)
            {
                if (buffer[i] == 0xFF && (buffer[i + 1] & 0xF6) == 0xF0 && TryReadHeader(_stream, chunk + i, out header) && FollowedByFrame(_stream, chunk + i, header))
                {
                    pos = chunk + i;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadHeader(Stream stream, long pos, out AdtsHeader header)
    {
        Span<byte> bytes = stackalloc byte[AdtsHeader.Size];
        stream.Position = pos;
        if (stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false) < bytes.Length)
        {
            header = default;
            return false;
        }

        return AdtsHeader.TryParse(bytes, out header);
    }

    /// <summary>Whether the frame at <paramref name="pos"/> ends at the end of the file or where another one starts.</summary>
    private static bool FollowedByFrame(Stream stream, long pos, AdtsHeader header)
    {
        long next = pos + header.FrameLength;
        return next == stream.Length || TryReadHeader(stream, next, out _);
    }
}

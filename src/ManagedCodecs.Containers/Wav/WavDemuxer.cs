using System.Buffers.Binary;

namespace ManagedCodecs.Containers;

/// <summary>
/// A .wav file (RIFF WAVE): one audio stream, its 'fmt ' chunk a WAVEFORMATEX named as AVI's audio is (integer and
/// float PCM by their depth, WAVE_FORMAT_EXTENSIBLE by its sub-format, IMA and Microsoft ADPCM, MP3; any other format by its tag in
/// hex), its 'data' chunk the packets, as FFmpeg's wav demuxer gives them.
/// </summary>
/// <remarks>
/// <para>
/// Packets are 4096 bytes, rounded down to whole blocks (nBlockAlign; one block where a block is longer), the last one
/// what the data has left; timed in samples (<see cref="AudioStreamInfo.TimeBase"/> is one over the sample rate): PCM's
/// a sample a block, IMA ADPCM's wSamplesPerBlock a block (a block cut short its whole groups of 4 bytes a channel, as
/// <see cref="ImaAdpcmDecoder"/> decodes it), Microsoft ADPCM's likewise (a block cut short its header's two samples
/// and two codes a byte over the channels, as <see cref="MsAdpcmDecoder"/> decodes it). MP3 is cut into frames by their
/// headers instead, as FFmpeg's parser cuts it, skipping bytes that start none, each frame its samples long. Other formats' packets are timed by the bytes
/// before them over nAvgBytesPerSec, an estimate no sample checks.
/// </para>
/// <para>
/// Chunks other than 'fmt ' and 'data' are skipped (a 'LIST' or 'fact', each padded to an even length). A 'data' chunk
/// whose size is 0, 0xFFFFFFFF (a recording that was never closed) or past the end of the file runs to the end of the
/// file. RF64 and Wave64 files are not read.
/// </para>
/// </remarks>
public sealed class WavDemuxer : IDemuxer
{
    // FFmpeg's wav demuxer reads up to this many bytes a packet.
    private const int PacketBytes = 4096;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly List<PacketInfo> _packets = [];
    private readonly List<long> _positions = [];

    // The first packet a read gives: 0 until a Seek moves it.
    private int _start;

    /// <summary>Reads the file's chunks (and, for MP3, finds every frame); packets are read from the stream later, by <see cref="ReadPackets"/>.</summary>
    /// <param name="stream">A seekable stream holding the file.</param>
    /// <param name="leaveOpen">Whether <see cref="Dispose"/> leaves <paramref name="stream"/> open.</param>
    /// <exception cref="ArgumentException">The stream is not seekable.</exception>
    /// <exception cref="InvalidDataException">The file is not RIFF WAVE, has no 'fmt ' chunk before its 'data', or is malformed.</exception>
    public WavDemuxer(Stream stream, bool leaveOpen = false)
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
            if (!Probe(stream))
            {
                throw new InvalidDataException("Not a RIFF WAVE file.");
            }

            var (format, dataStart, dataEnd) = ReadChunks();
            if (format.SampleRate <= 0 || format.Channels <= 0)
            {
                throw new InvalidDataException($"A WAVE format of {format.SampleRate} Hz and {format.Channels} channels.");
            }

            if (format.Codec == CodecIds.Mp3)
            {
                FindMp3Frames(dataStart, dataEnd);
            }
            else
            {
                CutData(format, dataStart, dataEnd);
            }

            Audio = new AudioStreamInfo(format.Codec, format.SampleRate, format.Channels, _packets.Count, new Rational(1, format.SampleRate), format.CodecPrivate, 0);
        }
        catch (Exception e)
        {
            if (!Malformed.IsMalformedInput(e))
            {
                throw;
            }

            throw Malformed.Wrap(e, "WAVE");
        }
    }

    /// <summary>Always null: a .wav file is audio only.</summary>
    public VideoStreamInfo? Video => null;

    /// <summary>The audio stream, from the 'fmt ' chunk.</summary>
    public AudioStreamInfo Audio { get; }

    /// <summary>Opens the file at <paramref name="path"/>, which the demuxer disposes with itself.</summary>
    public static WavDemuxer Open(string path) => new(File.OpenRead(path));

    /// <inheritdoc cref="WavDemuxer"/>
    public IEnumerable<Packet> ReadPackets() => ReadSteps.Run(_stream, Steps(), "WAVE");

    /// <inheritdoc cref="AviDemuxer.ReadPacketsAsync"/>
    public IAsyncEnumerable<Packet> ReadPacketsAsync(CancellationToken cancellationToken = default) => ReadSteps.RunAsync(_stream, Steps(), "WAVE", cancellationToken);

    /// <summary>The packets <see cref="ReadPackets"/> gives, in its order and with its values, without their data and with no read of the file.</summary>
    public IEnumerable<PacketInfo> ReadPacketInfo() => _packets.Skip(_start);

    /// <inheritdoc/>
    /// <remarks>
    /// Every packet is a keyframe: reading starts at the last packet whose pts is at or before <paramref name="pts"/>
    /// (in samples), found among the packets listed when opening, with no read of the file; before the first at the
    /// first, past the last at the last.
    /// </remarks>
    public void Seek(long pts, int streamIndex)
    {
        if (streamIndex != Audio.StreamIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(streamIndex), streamIndex, "A .wav file has one stream, of index 0.");
        }

        int start = 0;
        for (int i = 1; i < _packets.Count && _packets[i].Pts <= pts; i++)
        {
            start = i;
        }

        _start = start;
    }

    /// <summary>Disposes the stream, unless the demuxer was made to leave it open.</summary>
    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    /// <summary>Whether <paramref name="stream"/> starts with "RIFF", a size and "WAVE". Leaves the position at 0.</summary>
    internal static bool Probe(Stream stream)
    {
        Span<byte> head = stackalloc byte[12];
        stream.Position = 0;
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        return read == head.Length && head[..4].SequenceEqual("RIFF"u8) && head[8..].SequenceEqual("WAVE"u8);
    }

    // Each packet's read and packet.
    private IEnumerable<ReadStep> Steps()
    {
        for (int i = _start; i < _packets.Count; i++)
        {
            var info = _packets[i];
            var data = new byte[info.Size];
            yield return ReadStep.Read(_positions[i], data);
            yield return ReadStep.Yield(new Packet(info.StreamIndex, info.Sequence, data, info.IsKeyframe, info.Dts, info.Pts, info.Duration));
        }
    }

    // The chunks after "WAVE": the 'fmt ' chunk's WAVEFORMATEX, and where the 'data' chunk's bytes start and end.
    private (WaveFormat Format, long DataStart, long DataEnd) ReadChunks()
    {
        Span<byte> header = stackalloc byte[8];
        WaveFormat? format = null;
        for (long pos = 12; pos + 8 <= _stream.Length;)
        {
            _stream.Position = pos;
            _stream.ReadExactly(header);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long start = pos + 8;
            if (header[..4].SequenceEqual("fmt "u8))
            {
                var bytes = new byte[Math.Min(size, 4096)];
                _stream.ReadExactly(bytes);
                if (!WaveFormat.TryParse(bytes, out var parsed))
                {
                    throw new InvalidDataException($"A 'fmt ' chunk of {size} bytes, shorter than a WAVEFORMAT.");
                }

                format = parsed;
            }
            else if (header[..4].SequenceEqual("data"u8))
            {
                if (format is not { } found)
                {
                    throw new InvalidDataException("The 'data' chunk comes before any 'fmt ' chunk.");
                }

                // FFmpeg reads a size of 0 to the end of the file, as one past it (0xFFFFFFFF, a recording never closed).
                long end = size == 0 || start + size > _stream.Length ? _stream.Length : start + size;
                return (found, start, end);
            }

            pos = start + size + (size & 1);
        }

        throw new InvalidDataException("The file has no 'data' chunk.");
    }

    // PCM, ADPCM and the rest: 4096 bytes rounded down to whole blocks, the last piece what is left.
    private void CutData(WaveFormat format, long start, long end)
    {
        int block = Math.Max(format.BlockAlign, 1);
        int size = block <= PacketBytes ? PacketBytes / block * block : block;
        int samplesPerBlock = format.Codec is CodecIds.AdpcmImaWav or CodecIds.AdpcmMs && format.CodecPrivate.Length >= 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(format.CodecPrivate.Span)
            : 0;
        long time = 0;
        long bytes = 0;
        for (long pos = start; pos < end; pos += size)
        {
            int length = (int)Math.Min(size, end - pos);
            long duration;
            if (PcmDecoder.CanDecode(format.Codec))
            {
                duration = length / block;
            }
            else if (format.Codec == CodecIds.AdpcmImaWav && samplesPerBlock > 0)
            {
                // Whole blocks, then a block cut short: its header's sample and 8 for each whole group of 4 bytes a channel.
                int headers = 4 * format.Channels;
                int rest = length % block;
                duration = ((long)(length / block) * samplesPerBlock) + (rest >= headers ? 1 + ((rest - headers) / headers * 8) : 0);
            }
            else if (format.Codec == CodecIds.AdpcmMs && samplesPerBlock > 0)
            {
                // Whole blocks, then a block cut short: its header's two samples and 2 codes a byte over the channels.
                int headers = 7 * format.Channels;
                int rest = length % block;
                duration = ((long)(length / block) * samplesPerBlock) + (rest >= headers ? 2 + ((rest - headers) * 2 / format.Channels) : 0);
            }
            else
            {
                bytes += length;
                long next = format.AverageBytesPerSecond > 0 ? bytes * format.SampleRate / format.AverageBytesPerSecond : 0;
                duration = next - time;
            }

            Add(pos, length, time, duration);
            time += duration;
        }
    }

    // MP3: whole frames by their headers, bytes that start none skipped, a frame past the data's end dropped.
    private void FindMp3Frames(long start, long end)
    {
        Span<byte> head = stackalloc byte[MpegAudioHeader.Size];
        long time = 0;
        for (long pos = start; pos + MpegAudioHeader.Size <= end;)
        {
            _stream.Position = pos;
            _stream.ReadExactly(head);
            if (!MpegAudioHeader.TryParse(head, out var header) || pos + header.FrameLength > end)
            {
                pos++;
                continue;
            }

            Add(pos, header.FrameLength, time, header.SamplesPerFrame);
            time += header.SamplesPerFrame;
            pos += header.FrameLength;
        }
    }

    private void Add(long position, int length, long time, long duration)
    {
        _positions.Add(position);
        _packets.Add(new PacketInfo(0, _packets.Count, length, IsKeyframe: true, time, time, duration));
    }
}

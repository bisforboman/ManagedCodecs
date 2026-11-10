namespace ManagedCodecs.Containers;

/// <summary>
/// Demuxer for raw H.264 files (Annex B elementary streams: <c>.h264</c>, <c>.264</c>): the stream cut into access
/// units, a picture a packet (a field a packet in field pictures), as FFmpeg's H.264 parser cuts it, the packets' bytes
/// as they are (start codes and all; <c>H264Decoder</c> reads Annex B without a configuration record).
/// </summary>
/// <remarks>
/// <para>
/// A new access unit starts at an access unit delimiter, at an SPS, PPS or SEI (or NAL unit types 14 to 18) after a
/// picture's slices, or at a slice whose first_mb_in_slice is 0 (7.4.1.2.3); IDR pictures, and those after a recovery
/// point SEI, are keyframes. The stream is scanned once when opening, for its access units' places.
/// </para>
/// <para>
/// The file has no timestamps (FFmpeg gives its packets none): packets are a tick each in decoding order, in a time
/// base of 1 / the frame rate, the SPS's VUI timing's (time_scale / 2 num_units_in_tick) or 25 without it, as FFmpeg's
/// raw H.264 demuxer assumes; the decoder numbers its frames from 0 in output order, as FFmpeg's command line does
/// (docs/decisions/2026-11-09-raw-h264.md). The size is the first SPS's. <see cref="Seek"/> starts at an IDR picture
/// from the access units found when opening.
/// </para>
/// </remarks>
public sealed class H264Demuxer : IDemuxer
{
    // Frames a second where the SPS gives no timing (FFmpeg's raw demuxers' -framerate default).
    private const int DefaultFrameRate = 25;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    // The access units found when opening: position, length, whether it is a keyframe, and whether an IDR picture.
    private readonly List<(long Position, int Length, bool Keyframe, bool Idr)> _units = [];

    // The first access unit a read gives: 0 until a Seek moves it.
    private int _start;

    /// <summary>Reads the stream to find its SPS and access units; packets are read from the stream later.</summary>
    /// <param name="stream">A seekable stream holding the file.</param>
    /// <param name="leaveOpen">Whether <see cref="Dispose"/> leaves <paramref name="stream"/> open.</param>
    /// <exception cref="ArgumentException">The stream is not seekable.</exception>
    /// <exception cref="InvalidDataException">The stream is not Annex B H.264 with a readable SPS.</exception>
    public H264Demuxer(Stream stream, bool leaveOpen = false)
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
            var nals = ScanNalUnits(_stream);
            if (nals.Count == 0)
            {
                throw new InvalidDataException("Not an H.264 stream: no start code.");
            }

            SpsInfo? sps = null;
            for (int i = 0; i < nals.Count && sps is null; i++)
            {
                if (nals[i].Type == 7)
                {
                    long end = i + 1 < nals.Count ? nals[i + 1].StartCode : _stream.Length;
                    long start = nals[i].StartCode + 3;
                    var data = new byte[(int)Math.Min(end - start, 1024)];
                    _stream.Position = start;
                    _stream.ReadExactly(data);
                    sps = SpsInfo.Parse(data);
                }
            }

            if (sps is not { } info)
            {
                throw new InvalidDataException("Not an H.264 stream: no sequence parameter set it can read.");
            }

            CutAccessUnits(nals, _stream.Length);
            var rate = info.FrameRate ?? new Rational(DefaultFrameRate, 1);
            Video = new VideoStreamInfo(CodecIds.H264, info.Width, info.Height, rate.ToDouble(), _units.Count, new Rational(rate.Denominator, rate.Numerator), ReadOnlyMemory<byte>.Empty, 0)
            {
                FramesFromZero = true,
            };
        }
        catch (Exception e)
        {
            if (!Malformed.IsMalformedInput(e))
            {
                throw;
            }

            throw Malformed.Wrap(e, "H.264");
        }
    }

    /// <summary>The stream: H.264, its size and frame rate from the first SPS, a tick a picture.</summary>
    public VideoStreamInfo Video { get; }

    /// <summary>Always null: a raw H.264 stream has no audio.</summary>
    public AudioStreamInfo? Audio => null;

    /// <summary>Opens the file at <paramref name="path"/>, which the demuxer disposes with itself.</summary>
    public static H264Demuxer Open(string path) => new(File.OpenRead(path));

    /// <inheritdoc/>
    public IEnumerable<Packet> ReadPackets() => ReadSteps.Run(_stream, Steps(), "H.264");

    /// <summary>
    /// <see cref="ReadPackets"/> as an asynchronous sequence: every packet's read is awaited, and cancellation checked
    /// between them.
    /// </summary>
    public IAsyncEnumerable<Packet> ReadPacketsAsync(CancellationToken cancellationToken = default) => ReadSteps.RunAsync(_stream, Steps(), "H.264", cancellationToken);

    /// <summary>The packets <see cref="ReadPackets"/> gives, without their data, from the access units found when opening: no read of the file.</summary>
    public IEnumerable<PacketInfo> ReadPacketInfo() => Units().Select(u => u.Info);

    /// <inheritdoc/>
    /// <remarks>
    /// Reading starts at the last IDR picture whose packet is at or before <paramref name="pts"/> (the access units found
    /// when opening: no read of the file), and before the first IDR picture at the stream's start. A recovery point,
    /// though a keyframe, is no place to start: the pictures shown before it in an open GOP need the ones before it,
    /// and the decoder times its frames by their place in output order from the start of the read. Frame k from the
    /// start is at time k, so the frames at and after the time follow an IDR picture at or before it. FFmpeg can't
    /// seek in a raw stream at all (<c>-ss</c> before <c>-i</c> fails): its <c>-ss</c> after <c>-i</c>, which decodes from
    /// the start and drops the frames before the time, shows the same frame.
    /// </remarks>
    public void Seek(long pts, int streamIndex)
    {
        if (streamIndex != Video.StreamIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(streamIndex), streamIndex, "A raw H.264 stream has one stream, of index 0.");
        }

        int start = 0;
        for (int i = 0; i < _units.Count && i <= pts; i++)
        {
            if (_units[i].Idr)
            {
                start = i;
            }
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

    /// <summary>
    /// Whether the stream starts as Annex B H.264: a start code at its start (after zero bytes), then a NAL unit of an
    /// SPS, an access unit delimiter or an SEI, with an SPS among the first units.
    /// </summary>
    internal static bool Probe(Stream stream)
    {
        try
        {
            Span<byte> head = stackalloc byte[4096];
            stream.Position = 0;
            int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            head = head[..read];
            int zeros = 0;
            while (zeros < head.Length && head[zeros] == 0)
            {
                zeros++;
            }

            if (zeros < 2 || zeros + 1 >= head.Length || head[zeros] != 1 || (head[zeros + 1] & 0x80) != 0
                || (head[zeros + 1] & 0x1F) is not (7 or 9 or 6))
            {
                return false;
            }

            for (int i = zeros + 1; i + 3 < head.Length; i++)
            {
                if (head[i] == 0 && head[i + 1] == 0 && head[i + 2] == 1 && (head[i + 3] & 0x1F) == 7)
                {
                    return true;
                }
            }

            return (head[zeros + 1] & 0x1F) == 7;
        }
        finally
        {
            stream.Position = 0;
        }
    }

    // Each access unit's read and packet.
    private IEnumerable<ReadStep> Steps()
    {
        foreach (var (position, info) in Units())
        {
            var data = new byte[info.Size];
            yield return ReadStep.Read(position, data);
            yield return ReadStep.Yield(new Packet(0, info.Sequence, data, info.IsKeyframe, info.Dts, info.Pts, info.Duration));
        }
    }

    // Each access unit's position and packet description, from where a Seek put the start: a tick each, in decoding
    // order, numbered from the stream's start.
    private IEnumerable<(long Position, PacketInfo Info)> Units()
    {
        for (int i = _start; i < _units.Count; i++)
        {
            var (position, length, keyframe, _) = _units[i];
            yield return (position, new PacketInfo(0, i, length, keyframe, i, i, 1));
        }
    }

    // The access units from the NAL units (7.4.1.2.3), each from the start code of its first unit (a four-byte one's
    // zero included, as FFmpeg cuts them) to the next's; the first from the stream's start.
    private void CutAccessUnits(List<Nal> nals, long length)
    {
        var starts = new List<(long Position, int First)> { (0, 0) };
        bool slices = false;
        for (int i = 0; i < nals.Count; i++)
        {
            int type = nals[i].Type;
            bool vcl = type is 1 or 5;
            bool starter = type == 9
                || (slices && (type is 6 or 7 or 8 or (>= 14 and <= 18)))
                || (slices && vcl && (nals[i].NextByte & 0x80) != 0); // first_mb_in_slice ue(v) 0: its first bit is 1
            if (starter && i > 0)
            {
                long at = nals[i].StartCode > 0 && nals[i].ZeroBefore ? nals[i].StartCode - 1 : nals[i].StartCode;
                starts.Add((at, i));
                slices = false;
            }

            slices |= vcl;
        }

        for (int u = 0; u < starts.Count; u++)
        {
            var (position, first) = starts[u];
            long end = u + 1 < starts.Count ? starts[u + 1].Position : length;
            int last = u + 1 < starts.Count ? starts[u + 1].First : nals.Count;
            bool idr = false;
            bool recovery = false;
            for (int i = first; i < last; i++)
            {
                // An IDR slice, or an SEI whose first message is a recovery point (payloadType 6).
                idr |= nals[i].Type == 5;
                recovery |= nals[i].Type == 6 && nals[i].NextByte == 6;
            }

            if (end > position)
            {
                _units.Add((position, checked((int)(end - position)), idr || recovery, idr));
            }
        }
    }

    // A NAL unit's start code position (its 0x000001), whether a zero byte comes before it, its type and the byte after
    // its header (a slice's first_mb_in_slice starts there, an SEI's first payloadType).
    private readonly record struct Nal(long StartCode, bool ZeroBefore, int Type, byte NextByte);

    // Every NAL unit of the stream, read once in blocks.
    private static List<Nal> ScanNalUnits(Stream stream)
    {
        var nals = new List<Nal>();
        var buffer = new byte[1 << 16];
        const int Overlap = 5;
        long offset = 0;
        int kept = 0;
        int before = -1; // the byte before the block, -1 at the stream's start
        stream.Position = 0;
        while (true)
        {
            int read = stream.ReadAtLeast(buffer.AsSpan(kept), buffer.Length - kept, throwOnEndOfStream: false);
            int length = kept + read;
            bool last = read == 0 || offset + length >= stream.Length;
            int limit = last ? length : length - Overlap;
            for (int i = 0; i + 2 < limit; i++)
            {
                if (buffer[i] == 0 && buffer[i + 1] == 0 && buffer[i + 2] == 1 && i + 3 < length)
                {
                    byte header = buffer[i + 3];
                    byte next = i + 4 < length ? buffer[i + 4] : (byte)0;
                    bool zero = i > 0 ? buffer[i - 1] == 0 : before == 0;
                    nals.Add(new Nal(offset + i, zero, header & 0x1F, next));
                    i += 2;
                }
            }

            if (last)
            {
                return nals;
            }

            // The last bytes again at the next block's start, so that no start code is missed across the boundary.
            before = buffer[limit - 1];
            buffer.AsSpan(limit, length - limit).CopyTo(buffer);
            offset += limit;
            kept = length - limit;
        }
    }
}

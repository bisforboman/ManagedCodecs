using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ManagedCodecs.Containers.Avi;

/// <summary>
/// Minimal AVI (RIFF) demuxer: finds the first video stream and yields its packets from the
/// 'movi' list, including chunks grouped in 'rec ' lists.
/// </summary>
/// <remarks>
/// Not yet supported: OpenDML / AVI 2.0 files over 1 GB (RIFF 'AVIX' extensions), the idx1 or
/// indx index (so <see cref="Packet.IsKeyframe"/> is always true, which is correct for MJPEG),
/// and audio streams.
/// </remarks>
public sealed class AviDemuxer : IDemuxer
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long _moviStart;
    private readonly long _moviEnd;
    private readonly int _videoStreamIndex;

    public AviDemuxer(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;

        _stream.Position = 0;
        var (riff, riffSize) = ReadChunkHeader(_stream);
        if (riff != "RIFF" || ReadFourCC(_stream) != "AVI ")
        {
            throw new InvalidDataException("Not an AVI file.");
        }

        long riffEnd = Math.Min(8 + (long)riffSize, _stream.Length);
        HeaderInfo? header = null;

        long pos = 12;
        while (pos + 8 <= riffEnd)
        {
            _stream.Position = pos;
            var (id, size) = ReadChunkHeader(_stream);
            long dataStart = pos + 8;
            long dataEnd = Math.Min(dataStart + size, riffEnd);

            if (id == "LIST" && size >= 4)
            {
                string type = ReadFourCC(_stream);
                if (type == "hdrl")
                {
                    header = ParseHeaderList(dataStart + 4, dataEnd);
                }
                else if (type == "movi" && _moviStart == 0)
                {
                    _moviStart = dataStart + 4;
                    _moviEnd = dataEnd;
                }
            }

            pos = NextChunk(pos, size);
        }

        if (header is null)
        {
            throw new InvalidDataException("AVI file has no 'hdrl' header list.");
        }

        if (header.VideoStreamIndex < 0)
        {
            throw new InvalidDataException("AVI file has no video stream.");
        }

        if (_moviStart == 0)
        {
            throw new InvalidDataException("AVI file has no 'movi' list.");
        }

        _videoStreamIndex = header.VideoStreamIndex;

        // Like FFmpeg, take the time base from the stream header (dwScale / dwRate) and fall
        // back to the main header's frame duration. Each chunk is one tick.
        var timeBase = header.Scale > 0 && header.Rate > 0
            ? Rational.Reduce(header.Scale, header.Rate)
            : Rational.Reduce(header.MicrosecondsPerFrame, 1_000_000);
        double fps = timeBase.Numerator > 0 ? 1 / timeBase.ToDouble() : 0;
        Video = new VideoStreamInfo(NormalizeCodec(header.Codec), header.Width, header.Height, fps, header.TotalFrames, timeBase, ReadOnlyMemory<byte>.Empty);
    }

    public VideoStreamInfo Video { get; }

    public static AviDemuxer Open(string path) => new(File.OpenRead(path));

    public IEnumerable<Packet> ReadPackets()
    {
        string prefix = _videoStreamIndex.ToString("00", CultureInfo.InvariantCulture);
        long sequence = 0;

        // Explicit stack instead of recursion so nested 'rec ' lists work inside an iterator.
        var ranges = new Stack<(long Pos, long End)>();
        ranges.Push((_moviStart, _moviEnd));

        while (ranges.Count > 0)
        {
            var (pos, end) = ranges.Pop();
            while (pos + 8 <= end)
            {
                _stream.Position = pos;
                var (id, size) = ReadChunkHeader(_stream);
                long next = NextChunk(pos, size);

                if (id == "LIST")
                {
                    // Descend into the list first, then resume after it.
                    ranges.Push((next, end));
                    ranges.Push((pos + 12, Math.Min(pos + 8 + size, end)));
                    break;
                }

                bool isVideoChunk = id.StartsWith(prefix, StringComparison.Ordinal)
                    && (id.EndsWith("dc", StringComparison.Ordinal) || id.EndsWith("db", StringComparison.Ordinal));

                if (isVideoChunk && size > 0)
                {
                    var data = new byte[size];
                    _stream.ReadExactly(data);
                    yield return new Packet(_videoStreamIndex, sequence, data, IsKeyframe: true, Dts: sequence, Pts: sequence, Duration: 1);
                    sequence++;
                }

                pos = next;
            }
        }
    }

    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private HeaderInfo ParseHeaderList(long start, long end)
    {
        var info = new HeaderInfo();
        int streamCount = 0;
        Span<byte> avih = stackalloc byte[40];

        long pos = start;
        while (pos + 8 <= end)
        {
            _stream.Position = pos;
            var (id, size) = ReadChunkHeader(_stream);

            if (id == "avih" && size >= 40)
            {
                _stream.ReadExactly(avih);
                info.MicrosecondsPerFrame = BinaryPrimitives.ReadUInt32LittleEndian(avih[0..]);
                info.TotalFrames = BinaryPrimitives.ReadUInt32LittleEndian(avih[16..]);
                info.Width = BinaryPrimitives.ReadInt32LittleEndian(avih[32..]);
                info.Height = BinaryPrimitives.ReadInt32LittleEndian(avih[36..]);
            }
            else if (id == "LIST" && size >= 4 && ReadFourCC(_stream) == "strl")
            {
                if (info.VideoStreamIndex < 0 && TryReadVideoStream(pos + 12, Math.Min(pos + 8 + size, end), out string codec, out uint scale, out uint rate))
                {
                    info.VideoStreamIndex = streamCount;
                    info.Codec = codec;
                    info.Scale = scale;
                    info.Rate = rate;
                }

                streamCount++;
            }

            pos = NextChunk(pos, size);
        }

        return info;
    }

    private bool TryReadVideoStream(long start, long end, out string codec, out uint scale, out uint rate)
    {
        codec = "????";
        scale = 0;
        rate = 0;
        bool isVideo = false;
        Span<byte> bitmapInfo = stackalloc byte[20];
        Span<byte> streamHeader = stackalloc byte[28];

        long pos = start;
        while (pos + 8 <= end)
        {
            _stream.Position = pos;
            var (id, size) = ReadChunkHeader(_stream);

            if (id == "strh" && size >= 28)
            {
                // AVISTREAMHEADER: fccType at 0, dwScale at 20, dwRate at 24.
                _stream.ReadExactly(streamHeader);
                isVideo = streamHeader[..4].SequenceEqual("vids"u8);
                scale = BinaryPrimitives.ReadUInt32LittleEndian(streamHeader[20..]);
                rate = BinaryPrimitives.ReadUInt32LittleEndian(streamHeader[24..]);
            }
            else if (id == "strf" && isVideo && size >= 20)
            {
                // BITMAPINFOHEADER: biCompression is the FourCC at offset 16.
                _stream.ReadExactly(bitmapInfo);
                codec = Encoding.ASCII.GetString(bitmapInfo.Slice(16, 4));
            }

            pos = NextChunk(pos, size);
        }

        return isVideo;
    }

    private static string NormalizeCodec(string fourCC) => fourCC.ToUpperInvariant() switch
    {
        "MJPG" => CodecIds.Mjpeg,
        "H264" or "X264" or "AVC1" => CodecIds.H264,
        _ => fourCC,
    };

    // RIFF chunks are padded to an even number of bytes.
    private static long NextChunk(long chunkStart, uint size) => chunkStart + 8 + size + (size & 1);

    private static (string Id, uint Size) ReadChunkHeader(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[8];
        stream.ReadExactly(buffer);
        return (Encoding.ASCII.GetString(buffer[..4]), BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]));
    }

    private static string ReadFourCC(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return Encoding.ASCII.GetString(buffer);
    }

    private sealed class HeaderInfo
    {
        public uint MicrosecondsPerFrame { get; set; }

        public uint TotalFrames { get; set; }

        public uint Scale { get; set; }

        public uint Rate { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public int VideoStreamIndex { get; set; } = -1;

        public string Codec { get; set; } = "????";
    }
}

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ManagedCodecs.Containers;

/// <summary>
/// Minimal AVI (RIFF) demuxer: finds the first video stream and yields its packets from the
/// 'movi' list, including chunks grouped in 'rec ' lists.
/// </summary>
/// <remarks>
/// Not yet supported: OpenDML / AVI 2.0 files over 1 GB (RIFF 'AVIX' extensions), the idx1 or
/// indx index (so <see cref="Packet.IsKeyframe"/> is always true, which is correct for MJPEG),
/// and audio streams (<see cref="Audio"/> is always null).
/// </remarks>
public sealed class AviDemuxer : IDemuxer
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long _moviStart;
    private readonly long _moviEnd;
    private readonly int _videoStreamIndex;

    // The first packet a read gives, 0 until a Seek moves it.
    private long _startSequence;

    /// <summary>Reads the file's headers; packets are read from the stream later, by <see cref="ReadPackets"/>.</summary>
    /// <param name="stream">A seekable stream holding the file.</param>
    /// <param name="leaveOpen">Whether <see cref="Dispose"/> leaves <paramref name="stream"/> open.</param>
    /// <exception cref="ArgumentException">The stream is not seekable.</exception>
    /// <exception cref="InvalidDataException">The file is not an AVI file with a video stream, or is malformed.</exception>
    public AviDemuxer(Stream stream, bool leaveOpen = false)
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
            // back to the main header's frame duration. Each chunk is one tick, an empty one too.
            var timeBase = header.Scale > 0 && header.Rate > 0
                ? Rational.Reduce(header.Scale, header.Rate)
                : Rational.Reduce(header.MicrosecondsPerFrame, 1_000_000);
            double fps = timeBase.Numerator > 0 ? 1 / timeBase.ToDouble() : 0;
            Video = new VideoStreamInfo(NormalizeCodec(header.Codec), header.Width, header.Height, fps, header.TotalFrames, timeBase, ReadOnlyMemory<byte>.Empty, _videoStreamIndex)
            {
                SampleAspectRatio = header.AspectRatio,
            };
        }
        catch (Exception e) when (Malformed.IsMalformedInput(e))
        {
            throw Malformed.Wrap(e, "AVI");
        }
    }

    /// <summary>The first video stream (an AVI file without one is refused).</summary>
    public VideoStreamInfo Video { get; }

    /// <summary>Always null: audio in AVI is not exposed.</summary>
    public AudioStreamInfo? Audio => null;

    /// <summary>Opens the file at <paramref name="path"/>, which the demuxer disposes with itself.</summary>
    public static AviDemuxer Open(string path) => new(File.OpenRead(path));

    /// <remarks>
    /// Packets come one at a time, so a file that breaks off inside a chunk gives the packets before it, then
    /// <see cref="InvalidDataException"/> (FFmpeg stops there without an error).
    /// </remarks>
    public IEnumerable<Packet> ReadPackets() => ReadSteps.Run(_stream, Steps(), "AVI");

    /// <summary>
    /// <see cref="ReadPackets"/> as an asynchronous sequence: every read of the file (chunk headers and packets) is
    /// awaited, and cancellation checked between them. The stream's own ReadAsync does the reading: a FileStream opened
    /// without <see cref="FileOptions.Asynchronous"/> (as <see cref="Open"/> opens it) does it on the thread pool; pass
    /// a stream opened with it for overlapped I/O.
    /// </summary>
    public IAsyncEnumerable<Packet> ReadPacketsAsync(CancellationToken cancellationToken = default) => ReadSteps.RunAsync(_stream, Steps(), "AVI", cancellationToken);

    /// <summary>
    /// The packets <see cref="ReadPackets"/> gives, in its order and with its values, without their data: the walk
    /// through the 'movi' list reads each chunk's 8-byte header and skips its data.
    /// </summary>
    /// <exception cref="InvalidDataException">A chunk runs past the end of the file (after the packets before it).</exception>
    public IEnumerable<PacketInfo> ReadPacketInfo() => ReadSteps.RunInfo(_stream, Steps(withData: false), "AVI");

    /// <inheritdoc/>
    /// <remarks>
    /// MJPEG only: every frame is a keyframe, so reading starts at the frame shown at <paramref name="pts"/> (or the one
    /// before it, after a dropped frame). The walk to it reads the chunk headers before it, not their data. Other
    /// codecs need the keyframes, which this demuxer does not read from the 'idx1' index yet, and throw.
    /// </remarks>
    public void Seek(long pts, int streamIndex)
    {
        if (streamIndex != _videoStreamIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(streamIndex), streamIndex, "The file's video stream has another index.");
        }

        if (Video.Codec != CodecIds.Mjpeg)
        {
            throw new NotSupportedException($"Seeking in {Video.Codec} in AVI is not supported: only MJPEG, whose every frame is a keyframe.");
        }

        long start = 0;
        foreach (var info in ReadSteps.RunInfo(_stream, Steps(withData: false, from: 0), "AVI"))
        {
            if (info.Pts > pts)
            {
                break;
            }

            start = info.Sequence;
        }

        _startSequence = start;
    }

    // The walk through the movi list: each chunk header's read, and each video chunk's read and packet (or, without
    // the data, its description), from packet number from on (the ones before it are walked over, their data unread).
    private IEnumerable<ReadStep> Steps(bool withData = true, long? from = null)
    {
        long first = from ?? _startSequence;
        string prefix = _videoStreamIndex.ToString("00", CultureInfo.InvariantCulture);
        long sequence = 0;
        long frame = 0;
        var header = new byte[8];

        // Explicit stack instead of recursion so nested 'rec ' lists work inside an iterator.
        var ranges = new Stack<(long Pos, long End)>();
        ranges.Push((_moviStart, _moviEnd));

        while (ranges.Count > 0)
        {
            var (pos, end) = ranges.Pop();
            while (pos + 8 <= end)
            {
                yield return ReadStep.Read(pos, header);
                var (id, size) = ParseChunkHeader(header);
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

                // An empty chunk is a dropped frame: no packet, but like FFmpeg it takes its tick, so the next
                // packet's pts is one later.
                if (isVideoChunk && size == 0)
                {
                    frame++;
                }
                else if (isVideoChunk)
                {
                    if (pos + 8 + size > _stream.Length)
                    {
                        throw new InvalidDataException($"The chunk at offset {pos} runs past the end of the file.");
                    }

                    if (sequence < first)
                    {
                        // Before where a Seek put the start: walked over.
                    }
                    else if (withData)
                    {
                        var data = new byte[size];
                        yield return ReadStep.Read(pos + 8, data);
                        yield return ReadStep.Yield(new Packet(_videoStreamIndex, sequence, data, IsKeyframe: true, Dts: frame, Pts: frame, Duration: 1));
                    }
                    else
                    {
                        yield return ReadStep.Describe(new PacketInfo(_videoStreamIndex, sequence, checked((int)size), IsKeyframe: true, Dts: frame, Pts: frame, Duration: 1));
                    }

                    sequence++;
                    frame++;
                }

                pos = next;
            }
        }
    }

    /// <summary>Disposes the stream, unless the demuxer was made to leave it open.</summary>
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
                if (info.VideoStreamIndex < 0 && TryReadVideoStream(pos + 12, Math.Min(pos + 8 + size, end), out string codec, out uint scale, out uint rate, out var aspectRatio))
                {
                    info.VideoStreamIndex = streamCount;
                    info.Codec = codec;
                    info.Scale = scale;
                    info.Rate = rate;
                    info.AspectRatio = aspectRatio;
                }

                streamCount++;
            }

            pos = NextChunk(pos, size);
        }

        return info;
    }

    private bool TryReadVideoStream(long start, long end, out string codec, out uint scale, out uint rate, out Rational aspectRatio)
    {
        codec = "????";
        scale = 0;
        rate = 0;
        aspectRatio = new(0, 1);
        bool isVideo = false;
        Span<byte> bitmapInfo = stackalloc byte[20];
        Span<byte> streamHeader = stackalloc byte[28];
        Span<byte> properties = stackalloc byte[32];

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
            else if (id == "vprp" && size > 36)
            {
                // VIDEOPROPHEADER (OpenDML): dwFrameAspectRatio at 20 (the shown picture's, Y in its low word and X in its
                // high), dwFrameWidthInPixels at 24 and dwFrameHeightInLines at 28. The sample aspect ratio is the
                // picture's over the frame's, as FFmpeg's avidec reads it (a zero anywhere: none), in lowest terms.
                _stream.ReadExactly(properties);
                int aspectY = BinaryPrimitives.ReadUInt16LittleEndian(properties[20..]);
                int aspectX = BinaryPrimitives.ReadUInt16LittleEndian(properties[22..]);
                uint width = BinaryPrimitives.ReadUInt32LittleEndian(properties[24..]);
                uint height = BinaryPrimitives.ReadUInt32LittleEndian(properties[28..]);
                aspectRatio = aspectX > 0 && aspectY > 0 && width > 0 && height > 0 && width <= int.MaxValue && height <= int.MaxValue
                    ? ColorCodes.AspectRatio((long)aspectX * height, (long)aspectY * width)
                    : new(0, 1);
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
        return ParseChunkHeader(buffer);
    }

    private static (string Id, uint Size) ParseChunkHeader(ReadOnlySpan<byte> header) =>
        (Encoding.ASCII.GetString(header[..4]), BinaryPrimitives.ReadUInt32LittleEndian(header[4..]));

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

        /// <summary>The video stream's 'vprp' sample aspect ratio, 0:1 without one.</summary>
        public Rational AspectRatio { get; set; } = new(0, 1);
    }
}

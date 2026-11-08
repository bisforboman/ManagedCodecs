using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ManagedCodecs.Containers;

/// <summary>
/// Minimal AVI (RIFF) demuxer: finds the first video stream and the first audio stream and yields their packets from the
/// 'movi' list, including chunks grouped in 'rec ' lists, as FFmpeg's demuxer and parsers give them.
/// </summary>
/// <remarks>
/// <para>
/// Not yet supported: OpenDML / AVI 2.0 files over 1 GB (RIFF 'AVIX' extensions), and the idx1 or indx index (so a
/// video packet's <see cref="Packet.IsKeyframe"/> is always true, which is correct for MJPEG).
/// </para>
/// <para>
/// Audio comes in the stream header's ticks (dwScale / dwRate), its packets as FFmpeg's (docs/decisions/2026-11-08-avi-audio.md):
/// with a dwSampleSize of 0 (MP3 and AAC as FFmpeg's muxer writes them) one packet a chunk, one tick each, an empty chunk
/// none; otherwise (PCM, ADPCM, MP3 as VirtualDub writes it) the bytes count, dwSampleSize a tick: a chunk is cut into
/// packets of 1024 blocks (one block from 32 bytes up, the whole chunk at 1 byte), each timed by the bytes before it.
/// MP3 in that layout, whose chunks are cut anywhere in a frame, is cut again into whole frames as FFmpeg's parser
/// cuts them: a frame that starts in a packet whose time no frame has taken yet takes it, moved on by the bytes before
/// the frame at the frame's rate; the others follow the frame before.
/// </para>
/// </remarks>
public sealed class AviDemuxer : IDemuxer
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long _moviStart;
    private readonly long _moviEnd;
    private readonly int _videoStreamIndex;
    private readonly AudioHeader? _audio;

    // The first packet of each stream a read gives, 0 until a Seek moves them.
    private long _startSequence;
    private long _audioStartSequence;

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
                Duration = header.Length,
            };

            if (header.Audio is { } audio)
            {
                _audio = audio;
                Audio = new AudioStreamInfo(audio.Codec, audio.SampleRate, audio.Channels, audio.SampleSize == 0 ? audio.Length : 0, audio.TimeBase, audio.Extra, audio.StreamIndex)
                {
                    Duration = audio.SampleSize == 0 ? audio.Length : 0,
                };
            }
        }
        catch (Exception e)
        {
            if (!Malformed.IsMalformedInput(e))
            {
                throw;
            }

            throw Malformed.Wrap(e, "AVI");
        }
    }

    /// <summary>The first video stream (an AVI file without one is refused).</summary>
    public VideoStreamInfo Video { get; }

    /// <summary>
    /// The first audio stream, or null without one. <see cref="AudioStreamInfo.Codec"/> is <see cref="CodecIds.Mp3"/>
    /// (format tag 0x55) or <see cref="CodecIds.Aac"/> (0xFF), else the WAVEFORMATEX format tag in hex (<c>"0x0001"</c> for
    /// PCM, <c>"0x0011"</c> for IMA ADPCM); the rate and channels are the WAVEFORMATEX's, <see cref="AudioStreamInfo.CodecPrivate"/>
    /// its extra bytes (AAC's AudioSpecificConfig). With a dwSampleSize of 0, <see cref="AudioStreamInfo.FrameCount"/>
    /// and <see cref="AudioStreamInfo.Duration"/> are the stream header's dwLength (which counts empty chunks too); else 0,
    /// as FFmpeg gives no stream duration there.
    /// </summary>
    public AudioStreamInfo? Audio { get; }

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
    /// through the 'movi' list reads each chunk's 8-byte header and skips its data (but for MP3 audio whose chunks are cut
    /// anywhere in a frame, which are read to find the frames).
    /// </summary>
    /// <exception cref="InvalidDataException">A chunk runs past the end of the file (after the packets before it).</exception>
    public IEnumerable<PacketInfo> ReadPacketInfo() => ReadSteps.RunInfo(_stream, Steps(withData: false), "AVI");

    /// <inheritdoc/>
    /// <remarks>
    /// MJPEG only: every frame is a keyframe (and every audio packet), so each stream's reading starts at its packet
    /// shown at the time (or the one before it, after a dropped frame), the other stream's time rounded down into its
    /// time base. The walk to it reads the chunk headers, not their data (but for MP3 audio cut anywhere in a frame).
    /// Other codecs need the keyframes, which this demuxer does not read from the 'idx1' index yet, and throw.
    /// </remarks>
    public void Seek(long pts, int streamIndex)
    {
        if (streamIndex != _videoStreamIndex && (_audio is null || streamIndex != _audio.StreamIndex))
        {
            throw new ArgumentOutOfRangeException(nameof(streamIndex), streamIndex, "The file has no video or audio stream of that index.");
        }

        if (Video.Codec != CodecIds.Mjpeg)
        {
            throw new NotSupportedException($"Seeking in {Video.Codec} in AVI is not supported: only MJPEG, whose every frame is a keyframe.");
        }

        var seekBase = streamIndex == _videoStreamIndex ? Video.TimeBase : _audio!.TimeBase;
        long videoTime = streamIndex == _videoStreamIndex ? pts : RescaleDown(pts, seekBase, Video.TimeBase);
        long audioTime = _audio is null || streamIndex == _audio.StreamIndex ? pts : RescaleDown(pts, seekBase, _audio.TimeBase);
        long videoStart = 0;
        long audioStart = 0;
        foreach (var info in ReadSteps.RunInfo(_stream, Steps(withData: false, from: (0, 0)), "AVI"))
        {
            if (info.StreamIndex == _videoStreamIndex && info.Pts <= videoTime)
            {
                videoStart = info.Sequence;
            }
            else if (info.StreamIndex != _videoStreamIndex && info.Pts <= audioTime)
            {
                audioStart = info.Sequence;
            }
        }

        _startSequence = videoStart;
        _audioStartSequence = audioStart;
    }

    // The walk through the movi list: each chunk header's read, and each packet's read and packet (or, without the
    // data, its description), from each stream's packet number in from on (the ones before are walked over, their data
    // unread but for MP3 audio cut anywhere in a frame, which the frames need).
    private IEnumerable<ReadStep> Steps(bool withData = true, (long Video, long Audio)? from = null)
    {
        var (first, audioFirst) = from ?? (_startSequence, _audioStartSequence);
        string prefix = _videoStreamIndex.ToString("00", CultureInfo.InvariantCulture);
        string? audioId = _audio?.StreamIndex.ToString("00", CultureInfo.InvariantCulture) + "wb";
        var audio = _audio is null ? null : new AviAudioWalk(_audio, audioFirst, withData);
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

                if (id == audioId && size > 0)
                {
                    if (pos + 8 + size > _stream.Length)
                    {
                        throw new InvalidDataException($"The chunk at offset {pos} runs past the end of the file.");
                    }

                    foreach (var step in audio!.Chunk(pos + 8, size))
                    {
                        yield return step;
                    }
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
                if (info.Audio is null && TryReadAudioStream(pos + 12, Math.Min(pos + 8 + size, end), streamCount) is { } audio)
                {
                    info.Audio = audio;
                }
                else if (info.VideoStreamIndex < 0 && TryReadVideoStream(pos + 12, Math.Min(pos + 8 + size, end), out string codec, out uint scale, out uint rate, out uint length, out var aspectRatio))
                {
                    info.VideoStreamIndex = streamCount;
                    info.Codec = codec;
                    info.Scale = scale;
                    info.Rate = rate;
                    info.Length = length;
                    info.AspectRatio = aspectRatio;
                }

                streamCount++;
            }

            pos = NextChunk(pos, size);
        }

        return info;
    }

    private bool TryReadVideoStream(long start, long end, out string codec, out uint scale, out uint rate, out uint length, out Rational aspectRatio)
    {
        codec = "????";
        scale = 0;
        rate = 0;
        length = 0;
        aspectRatio = new(0, 1);
        bool isVideo = false;
        Span<byte> bitmapInfo = stackalloc byte[20];
        Span<byte> streamHeader = stackalloc byte[36];
        Span<byte> properties = stackalloc byte[32];

        long pos = start;
        while (pos + 8 <= end)
        {
            _stream.Position = pos;
            var (id, size) = ReadChunkHeader(_stream);

            if (id == "strh" && size >= 28)
            {
                // AVISTREAMHEADER: fccType at 0, dwScale at 20, dwRate at 24, dwLength (in dwScale / dwRate ticks) at 32.
                _stream.ReadExactly(streamHeader[..28]);
                isVideo = streamHeader[..4].SequenceEqual("vids"u8);
                scale = BinaryPrimitives.ReadUInt32LittleEndian(streamHeader[20..]);
                rate = BinaryPrimitives.ReadUInt32LittleEndian(streamHeader[24..]);
                if (size >= 36)
                {
                    _stream.ReadExactly(streamHeader[28..36]);
                    length = BinaryPrimitives.ReadUInt32LittleEndian(streamHeader[32..]);
                }
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

    // An 'auds' stream list's strh and strf (WAVEFORMATEX: wFormatTag, nChannels, nSamplesPerSec, nAvgBytesPerSec,
    // nBlockAlign, wBitsPerSample, cbSize and its extra bytes), or null for another kind of stream.
    private AudioHeader? TryReadAudioStream(long start, long end, int streamIndex)
    {
        Span<byte> streamHeader = stackalloc byte[48];
        Span<byte> format = stackalloc byte[18];
        bool isAudio = false;
        uint scale = 0;
        uint rate = 0;
        uint length = 0;
        uint sampleSize = 0;
        int formatTag = -1;
        int channels = 0;
        int sampleRate = 0;
        byte[] extra = [];

        long pos = start;
        while (pos + 8 <= end)
        {
            _stream.Position = pos;
            var (id, size) = ReadChunkHeader(_stream);

            if (id == "strh" && size >= 48)
            {
                // AVISTREAMHEADER: fccType at 0, dwScale at 20, dwRate at 24, dwLength at 32, dwSampleSize at 44.
                _stream.ReadExactly(streamHeader);
                isAudio = streamHeader[..4].SequenceEqual("auds"u8);
                scale = BinaryPrimitives.ReadUInt32LittleEndian(streamHeader[20..]);
                rate = BinaryPrimitives.ReadUInt32LittleEndian(streamHeader[24..]);
                length = BinaryPrimitives.ReadUInt32LittleEndian(streamHeader[32..]);
                sampleSize = BinaryPrimitives.ReadUInt32LittleEndian(streamHeader[44..]);
            }
            else if (id == "strf" && isAudio && size >= 16)
            {
                _stream.ReadExactly(format[..(int)Math.Min(size, 18)]);
                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(format);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(format[4..]);
                int extraSize = size >= 18 ? Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(format[16..]), (int)size - 18) : 0;
                extra = new byte[extraSize];
                _stream.ReadExactly(extra);
            }

            pos = NextChunk(pos, size);
        }

        if (!isAudio || formatTag < 0)
        {
            return null;
        }

        // The stream header's ticks; without them (a broken header), one a sample.
        var timeBase = scale > 0 && rate > 0 && scale <= int.MaxValue && rate <= int.MaxValue
            ? Rational.Reduce(scale, rate)
            : new Rational(1, Math.Max(sampleRate, 1));
        string codec = formatTag switch
        {
            0x55 => CodecIds.Mp3,
            0xFF => CodecIds.Aac,
            _ => "0x" + formatTag.ToString("X4", CultureInfo.InvariantCulture),
        };
        return new AudioHeader(streamIndex, codec, sampleRate, channels, timeBase, length, sampleSize, extra);
    }

    // value in from's ticks in to's, rounded down.
    private static long RescaleDown(long value, Rational from, Rational to)
    {
        var n = (System.Numerics.BigInteger)value * from.Numerator * to.Denominator;
        var d = (System.Numerics.BigInteger)from.Denominator * to.Numerator;
        var q = System.Numerics.BigInteger.DivRem(n, d, out var r);
        var result = r.IsZero || (n.Sign < 0) == (d.Sign < 0) ? q : q - 1;
        return result > long.MaxValue / 2 ? long.MaxValue / 2 : result < long.MinValue / 2 ? long.MinValue / 2 : (long)result;
    }

    // The MJPEG FourCCs are those FFmpeg's AVI demuxer reads as MJPEG, ignoring case as it does (each checked with
    // `ffmpeg -c copy -tag:v X` and ffprobe): capture cards, editors and cameras write others than MJPG.
    private static string NormalizeCodec(string fourCC) => fourCC.ToUpperInvariant() switch
    {
        "MJPG" or "AVI1" or "DMB1" or "JPGL" or "LJPG" or "IJPG" or "ACDV" or "QIVG" or "SLMJ" or "CJPG" or "JPEG" or "MMJP"
            or "MVJP" or "MJPA" => CodecIds.Mjpeg,
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

        /// <summary>The video stream header's dwLength, in its ticks.</summary>
        public uint Length { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public int VideoStreamIndex { get; set; } = -1;

        public string Codec { get; set; } = "????";

        /// <summary>The video stream's 'vprp' sample aspect ratio, 0:1 without one.</summary>
        public Rational AspectRatio { get; set; } = new(0, 1);

        /// <summary>The first audio stream, null without one.</summary>
        public AudioHeader? Audio { get; set; }
    }
}

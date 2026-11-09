using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ManagedCodecs.Containers;

/// <summary>
/// Minimal AVI (RIFF) demuxer: finds the first video stream and the first audio stream and yields their packets from
/// the 'movi' list in file order, including chunks grouped in 'rec ' lists.
/// </summary>
/// <remarks>
/// <para>
/// Audio comes as FFmpeg's demuxer gives it, timed by the stream header (dwScale / dwRate): a packet a chunk where
/// dwSampleSize is 0; with one (PCM, ADPCM), pieces of each chunk (1024 blocks below 32 bytes a block, one block from
/// 32 up, the whole chunk at 1 byte), each timed by the bytes before it over dwSampleSize. Except MP3 in the "CBR" layout (dwSampleSize set: chunks of any length, cut
/// anywhere in a frame), which is re-split into whole frames, each timed at its sample position (time base 1 / the
/// sample rate; FFmpeg's parser interpolates there, up to a millisecond off: docs/decisions/2026-11-08-avi-audio.md).
/// MP3 is <see cref="CodecIds.Mp3"/> and AAC <see cref="CodecIds.Aac"/> (its AudioSpecificConfig as the codec private
/// data); other audio is named by its WAVEFORMATEX format tag in hex ("0x0001" for PCM).
/// </para>
/// <para>
/// Not yet supported: OpenDML / AVI 2.0 files over 1 GB (RIFF 'AVIX' extensions), the idx1 or indx index (so
/// <see cref="Packet.IsKeyframe"/> is always true, which is correct for MJPEG and audio).
/// </para>
/// </remarks>
public sealed class AviDemuxer : IDemuxer
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long _moviStart;
    private readonly long _moviEnd;
    private readonly int _videoStreamIndex;
    private readonly AudioLayout _audioLayout;
    private readonly uint _audioSampleSize;

    // The first video packet a read gives, 0 until a Seek moves it, and the time the Seek asked for (in its stream's
    // time base): the audio starts at its last packet at or before it.
    private long _startSequence;
    private (long Pts, Rational TimeBase)? _start;

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
            (Audio, _audioLayout) = AudioOf(header.AudioStream);
            _audioSampleSize = header.AudioStream?.SampleSize ?? 0;
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

    /// <summary>The first audio stream, or null without one.</summary>
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
    /// through the 'movi' list reads each chunk's 8-byte header and skips its data.
    /// </summary>
    /// <exception cref="InvalidDataException">A chunk runs past the end of the file (after the packets before it).</exception>
    public IEnumerable<PacketInfo> ReadPacketInfo() => ReadSteps.RunInfo(_stream, Steps(withData: false), "AVI");

    /// <inheritdoc/>
    /// <remarks>
    /// MJPEG only: every frame is a keyframe, so reading starts at the frame shown at <paramref name="pts"/> (or the one
    /// before it, after a dropped frame), and the audio at its last packet at or before that time. The time can be the
    /// audio stream's too. The walk to it reads the chunk headers before it, not their data. Other codecs need the
    /// keyframes, which this demuxer does not read from the 'idx1' index yet, and throw.
    /// </remarks>
    public void Seek(long pts, int streamIndex)
    {
        Rational timeBase;
        if (streamIndex == _videoStreamIndex)
        {
            timeBase = Video.TimeBase;
        }
        else if (Audio is { } audio && streamIndex == audio.StreamIndex)
        {
            timeBase = audio.TimeBase;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(streamIndex), streamIndex, "The file has no stream of that index.");
        }

        if (Video.Codec != CodecIds.Mjpeg)
        {
            throw new NotSupportedException($"Seeking in {Video.Codec} in AVI is not supported: only MJPEG, whose every frame is a keyframe.");
        }

        // The video's frame at or before the time (in its ticks, rounded down).
        long videoPts = streamIndex == _videoStreamIndex ? pts : FloorTicks(pts, timeBase, Video.TimeBase);
        long start = 0;
        foreach (var info in ReadSteps.RunInfo(_stream, Steps(withData: false, from: 0, audio: false), "AVI"))
        {
            if (info.Pts > videoPts)
            {
                break;
            }

            start = info.Sequence;
        }

        _startSequence = start;
        _start = (pts, timeBase);
    }

    // value x from, in to's ticks, rounded down.
    private static long FloorTicks(long value, Rational from, Rational to)
    {
        decimal ticks = (decimal)value * from.Numerator * to.Denominator / ((decimal)from.Denominator * to.Numerator);
        return (long)Math.Floor(ticks);
    }

    // The walk through the movi list: each chunk header's read, and each video chunk's read and packet (or, without
    // the data, its description), from packet number from on (the ones before it are walked over, their data unread);
    // and the audio's, from the one playing at the time a Seek asked for.
    private IEnumerable<ReadStep> Steps(bool withData = true, long? from = null, bool audio = true)
    {
        long first = from ?? _startSequence;
        var start = from is null ? _start : null;
        string prefix = _videoStreamIndex.ToString("00", CultureInfo.InvariantCulture);
        long sequence = 0;
        long frame = 0;
        var header = new byte[8];

        var audioInfo = audio ? Audio : null;
        string audioId = audioInfo is null ? "" : audioInfo.StreamIndex.ToString("00", CultureInfo.InvariantCulture) + "wb";
        var audioState = new AudioState();
        (Packet Packet, int Size)? held = null;

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

                if (audioInfo is not null && id == audioId && size > 0)
                {
                    if (pos + 8 + size > _stream.Length)
                    {
                        throw new InvalidDataException($"The chunk at offset {pos} runs past the end of the file.");
                    }

                    // CBR MP3 is read even for packet descriptions: its frames are found in the data.
                    bool read = withData || _audioLayout == AudioLayout.Mp3Frames;
                    var data = read ? new byte[size] : [];
                    if (read)
                    {
                        yield return ReadStep.Read(pos + 8, data);
                    }

                    foreach (var (packet, packetSize) in AudioPackets(audioInfo, audioState, data, size))
                    {
                        // After a Seek: the last packet at or before its time is held until a later one shows it is
                        // the one to start from (or the walk ends).
                        if (start is { } time && AtOrBefore(packet, audioInfo, time))
                        {
                            held = (packet, packetSize);
                            continue;
                        }

                        if (held is { } kept)
                        {
                            yield return AudioStep(kept.Packet, kept.Size, withData);
                            held = null;
                        }

                        yield return AudioStep(packet, packetSize, withData);
                    }
                }

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

        if (held is { } last)
        {
            yield return AudioStep(last.Packet, last.Size, withData);
        }
    }

    // An audio packet's step: the packet, or without the data its description (of size bytes: its data is not read).
    private static ReadStep AudioStep(Packet packet, int size, bool withData) => withData
        ? ReadStep.Yield(packet)
        : ReadStep.Describe(new PacketInfo(packet.StreamIndex, packet.Sequence, size, true, packet.Dts, packet.Pts, packet.Duration));

    // How the audio's chunks become packets: a packet a chunk, a tick each (dwSampleSize 0, as FFmpeg's muxer writes MP3
    // and AAC), or pieces of chunks timed by the bytes before them (dwSampleSize set: PCM and the like); or CBR MP3
    // re-split into frames.
    private enum AudioLayout
    {
        None,
        Chunks,
        ChunkSamples,
        Mp3Frames,
    }

    /// <summary>The audio stream's description from its 'strh' and 'strf', and how its chunks become packets.</summary>
    private static (AudioStreamInfo? Audio, AudioLayout Layout) AudioOf(AudioStreamHeader? stream)
    {
        // WAVEFORMATEX: wFormatTag, nChannels, nSamplesPerSec, nAvgBytesPerSec, nBlockAlign, wBitsPerSample, cbSize and
        // cbSize bytes more (an AAC stream's AudioSpecificConfig), which FFmpeg takes as the codec's extradata.
        if (stream is null || stream.Format.Length < 16)
        {
            return (null, AudioLayout.None);
        }

        var format = stream.Format;
        int tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2));
        int sampleRate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4)));
        int extra = format.Length >= 18 ? BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(16)) : 0;
        var codecPrivate = extra > 0 && 18 + extra <= format.Length ? format.AsMemory(18, extra) : ReadOnlyMemory<byte>.Empty;
        string codec = tag switch
        {
            0x55 => CodecIds.Mp3,
            0xFF => CodecIds.Aac,
            _ => "0x" + tag.ToString("X4", CultureInfo.InvariantCulture),
        };

        if (codec == CodecIds.Mp3 && stream.SampleSize > 0 && sampleRate > 0)
        {
            return (new AudioStreamInfo(codec, sampleRate, channels, 0, new Rational(1, sampleRate), codecPrivate, stream.Index), AudioLayout.Mp3Frames);
        }

        var timeBase = stream.Scale > 0 && stream.Rate > 0 ? Rational.Reduce(stream.Scale, stream.Rate) : new Rational(1, Math.Max(sampleRate, 1));
        bool samples = stream.SampleSize > 0;
        var info = new AudioStreamInfo(codec, sampleRate, channels, samples ? 0 : stream.Length, timeBase, codecPrivate, stream.Index)
        {
            // FFmpeg gives a stream duration (dwLength ticks) for a packet a chunk only.
            Duration = samples ? 0 : stream.Length,
        };
        return (info, samples ? AudioLayout.ChunkSamples : AudioLayout.Chunks);
    }

    // The audio's place in the walk: packets, and ticks so far (or, with a dwSampleSize, bytes), and CBR MP3's bytes
    // not yet a whole frame.
    private sealed class AudioState
    {
        public long Sequence { get; set; }

        public long Position { get; set; }

        public long Bytes { get; set; }

        public List<byte> Pending { get; } = [];
    }

    // The packets an audio chunk gives, each with its size: itself, its pieces, or the whole MP3 frames it completes.
    // Without its data (packet descriptions) data is empty and size the chunk's.
    private IEnumerable<(Packet Packet, int Size)> AudioPackets(AudioStreamInfo audio, AudioState state, byte[] data, uint size)
    {
        if (_audioLayout == AudioLayout.Chunks)
        {
            yield return (new Packet(audio.StreamIndex, state.Sequence++, data, IsKeyframe: true, Dts: state.Position, Pts: state.Position, Duration: 1), checked((int)size));
            state.Position++;
            yield break;
        }

        if (_audioLayout == AudioLayout.ChunkSamples)
        {
            // As FFmpeg's demuxer cuts them (each case checked with ffprobe on FFmpeg AVIs whose PCM chunks were merged):
            // pieces of 1024 blocks below 32 bytes a block, of one block from 32 up (ADPCM), the whole chunk at 1 byte;
            // each timed by all the bytes before it over dwSampleSize, rounded down, and lasting its own bytes over it.
            long blockSize = _audioSampleSize;
            long piece = blockSize <= 1 ? size : blockSize < 32 ? 1024 * blockSize : blockSize;
            for (long offset = 0; offset < size; offset += piece)
            {
                int length = (int)Math.Min(piece, size - offset);
                long pts = state.Bytes / blockSize;
                state.Bytes += length;
                var bytes = data.Length > 0 ? data.AsMemory((int)offset, length) : ReadOnlyMemory<byte>.Empty;
                yield return (new Packet(audio.StreamIndex, state.Sequence++, bytes, IsKeyframe: true, Dts: pts, Pts: pts, Duration: length / blockSize), length);
            }

            yield break;
        }

        // Whole frames as their headers say, from the bytes so far; bytes that start no Layer III frame are skipped, as
        // FFmpeg's parser resynchronizes.
        var pending = state.Pending;
        pending.AddRange(data);
        int at = 0;
        while (pending.Count - at >= MpegAudioHeader.Size)
        {
            Span<byte> head = [pending[at], pending[at + 1], pending[at + 2], pending[at + 3]];
            if (!MpegAudioHeader.TryParse(head, out var frameHeader))
            {
                at++;
                continue;
            }

            int length = frameHeader.FrameLength;
            if (pending.Count - at < length)
            {
                break;
            }

            var frame = pending.GetRange(at, length).ToArray();
            at += length;
            yield return (new Packet(audio.StreamIndex, state.Sequence++, frame, IsKeyframe: true, Dts: state.Position, Pts: state.Position, Duration: frameHeader.SamplesPerFrame), length);
            state.Position += frameHeader.SamplesPerFrame;
        }

        pending.RemoveRange(0, at);
    }

    // Every audio packet is a keyframe, so after a Seek the audio starts at its last packet at or before the time asked
    // for, as the other demuxers' streams do.
    // Whether an audio packet starts at or before the time a Seek asked for.
    private static bool AtOrBefore(Packet packet, AudioStreamInfo audio, (long Pts, Rational TimeBase) time) =>
        (decimal)packet.Pts * audio.TimeBase.Numerator * time.TimeBase.Denominator <= (decimal)time.Pts * time.TimeBase.Numerator * audio.TimeBase.Denominator;

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
                long listEnd = Math.Min(pos + 8 + size, end);
                if (info.VideoStreamIndex < 0 && TryReadVideoStream(pos + 12, listEnd, out string codec, out uint scale, out uint rate, out uint length, out var aspectRatio))
                {
                    info.VideoStreamIndex = streamCount;
                    info.Codec = codec;
                    info.Scale = scale;
                    info.Rate = rate;
                    info.Length = length;
                    info.AspectRatio = aspectRatio;
                }
                else if (info.AudioStream is null && ReadAudioStream(pos + 12, listEnd, streamCount) is { } audio)
                {
                    info.AudioStream = audio;
                }

                streamCount++;
            }

            pos = NextChunk(pos, size);
        }

        return info;
    }

    // An audio stream's 'strh' (fccType 'auds': dwScale, dwRate, dwLength, dwSampleSize) and 'strf' (its WAVEFORMATEX,
    // kept whole); null for another kind of stream.
    private AudioStreamHeader? ReadAudioStream(long start, long end, int index)
    {
        Span<byte> streamHeader = stackalloc byte[48];
        bool isAudio = false;
        uint scale = 0;
        uint rate = 0;
        uint length = 0;
        uint sampleSize = 0;
        byte[]? format = null;
        long pos = start;
        while (pos + 8 <= end)
        {
            _stream.Position = pos;
            var (id, size) = ReadChunkHeader(_stream);
            if (id == "strh" && size >= 48)
            {
                _stream.ReadExactly(streamHeader);
                isAudio = streamHeader[..4].SequenceEqual("auds"u8);
                scale = BinaryPrimitives.ReadUInt32LittleEndian(streamHeader[20..]);
                rate = BinaryPrimitives.ReadUInt32LittleEndian(streamHeader[24..]);
                length = BinaryPrimitives.ReadUInt32LittleEndian(streamHeader[32..]);
                sampleSize = BinaryPrimitives.ReadUInt32LittleEndian(streamHeader[44..]);
            }
            else if (id == "strf" && size is >= 16 and <= 65_536)
            {
                format = new byte[size];
                _stream.ReadExactly(format);
            }

            pos = NextChunk(pos, size);
        }

        return isAudio && format is not null ? new AudioStreamHeader(index, scale, rate, length, sampleSize, format) : null;
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

        /// <summary>The first audio stream's headers, null without one.</summary>
        public AudioStreamHeader? AudioStream { get; set; }
    }

    // An audio stream's index among the file's streams, its 'strh' fields and its 'strf' (a WAVEFORMATEX).
    private sealed record AudioStreamHeader(int Index, uint Scale, uint Rate, uint Length, uint SampleSize, byte[] Format);
}

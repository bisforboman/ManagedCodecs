using System.Buffers.Binary;
using System.Text;

namespace ManagedCodecs.Containers;

/// <summary>
/// Matroska and WebM (RFC 9559, EBML RFC 8794) demuxer: finds the first video and the first audio track and yields
/// their frames, from the SimpleBlocks and BlockGroups of every Cluster.
/// </summary>
/// <remarks>
/// <para>
/// Opening the file walks it once, reading each block's few header bytes (track, timestamp, flags, lace sizes) and
/// skipping its data, so <see cref="ReadPacketInfo"/>, <see cref="Seek"/> and the frame counts need no further reads.
/// The Cues are not read: the walk finds every keyframe, so a file without Cues (a live recording) seeks as well. A
/// Cluster or Segment of unknown size (a live stream) ends at the next element that cannot be inside it; a file that
/// breaks off ends at the last whole block.
/// </para>
/// <para>
/// Timestamps are as FFmpeg gives them: the Cluster's Timestamp plus the block's, minus the track's CodecDelay (the
/// encoder delay of AAC and Opus: so their first packet is at a negative time), in the TimestampScale's ticks. A
/// block's duration is its BlockDuration, else the track's DefaultDuration (times the frames in a lace); the frames of a
/// lace share it out, each one's timestamp the one before plus its share. An audio frame with no duration at all (FFmpeg
/// writes no DefaultDuration for AAC) runs up to the track's next frame, the last one as long as the one before it.
/// </para>
/// <para>
/// Matroska stores no decode times. <see cref="Packet.Dts"/> is the presentation times sorted, delayed by as many frames
/// as the track reorders them (H.264 with B-frames), so each frame's dts is at or before its pts, as FFmpeg derives
/// them; the first of those frames, which FFmpeg leaves without one, count back from the first presentation time by
/// the frame duration. Without reordering dts equals pts.
/// </para>
/// <para>
/// Header stripping (ContentCompression algorithm 3) is undone. Not supported: other content encodings (zlib, bzlib,
/// LZO and encryption) of the exposed tracks, a TrackTimestampScale other than 1, a Tracks element after the first
/// Cluster, further tracks, and the display rotation (<see cref="VideoStreamInfo.Rotation"/> is 0).
/// </para>
/// </remarks>
public sealed class MatroskaDemuxer : IDemuxer
{
    // Bounds the index built from a file that claims an absurd number of blocks.
    private const int MaxPackets = 1 << 26;

    // Bounds the elements read whole (EBML header, Info, Tracks).
    private const int MaxHeaderElement = 1 << 24;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    // Every exposed track's frames in file order; each track's in its own order (decode order) inside it.
    private readonly Entry[] _entries;

    // The video track (slot 0) and the audio track (slot 1), null where the file has none.
    private readonly TrackEntry?[] _tracks;

    // Where each slot's packets start (a Sequence), 0 until a Seek moves them.
    private readonly long[] _start = new long[2];

    /// <summary>Reads the file's headers and walks its clusters; packets are read from the stream later, by <see cref="ReadPackets"/>.</summary>
    /// <param name="stream">A seekable stream holding the file.</param>
    /// <param name="leaveOpen">Whether <see cref="Dispose"/> leaves <paramref name="stream"/> open.</param>
    /// <exception cref="ArgumentException">The stream is not seekable.</exception>
    /// <exception cref="InvalidDataException">The file is not Matroska or WebM, has no video or audio track, or is malformed.</exception>
    /// <exception cref="NotSupportedException">The file uses what the remarks list as not supported.</exception>
    public MatroskaDemuxer(Stream stream, bool leaveOpen = false)
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
            var walk = new Walk(stream);
            walk.Run();
            _tracks = [walk.VideoTrack, walk.AudioTrack];
            if (walk.VideoTrack is null && walk.AudioTrack is null)
            {
                throw new InvalidDataException("Matroska file has no video or audio track.");
            }

            _entries = [.. walk.Entries];
            var timeBase = Rational.Reduce(walk.TimestampScale, 1_000_000_000);
            long duration = double.IsFinite(walk.Duration) && walk.Duration is > 0 and < long.MaxValue ? (long)Math.Round(walk.Duration) : 0;
            var perSlot = new List<int>[2];
            for (int slot = 0; slot < 2; slot++)
            {
                perSlot[slot] = [];
            }

            for (int i = 0; i < _entries.Length; i++)
            {
                perSlot[_entries[i].Slot].Add(i);
            }

            if (walk.VideoTrack is { } video)
            {
                long frameTicks = video.DefaultDuration / walk.TimestampScale;
                SetDecodeTimes(perSlot[0], frameTicks);
                double fps = video.DefaultDuration > 0 ? 1e9 / video.DefaultDuration : MeasuredRate(perSlot[0], timeBase);
                Video = new VideoStreamInfo(video.Codec, video.Width, video.Height, fps, perSlot[0].Count, timeBase, video.CodecPrivate, video.StreamIndex)
                {
                    ColorRange = video.Range,
                    ColorPrimaries = video.Primaries,
                    ColorTransfer = video.Transfer,
                    ColorMatrix = video.Matrix,
                    SampleAspectRatio = video.AspectRatio,
                    Duration = duration,
                };
            }

            if (walk.AudioTrack is { } audio)
            {
                SetAudioTimes(perSlot[1]);
                Audio = new AudioStreamInfo(audio.Codec, audio.SampleRate, audio.Channels, perSlot[1].Count, timeBase, audio.CodecPrivate, audio.StreamIndex)
                {
                    Duration = duration,
                };
            }
        }
        catch (Exception e)
        {
            if (!Malformed.IsMalformedInput(e))
            {
                throw;
            }

            throw Malformed.Wrap(e, "Matroska");
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The codec is one of <see cref="CodecIds"/> for H.264 (V_MPEG4/ISO/AVC, its CodecPrivate the avcC), MJPEG and
    /// MPEG-4 Visual, otherwise Matroska's CodecID (V_VP9, V_AV1, ...). The sample aspect ratio is the display size's
    /// over the pixel size's (DisplayWidth and DisplayHeight, which default to the pixel size: 1:1 without them, as FFmpeg
    /// reads them; 0:1 for a DisplayUnit of 4, unknown); the colour from the Colour element.
    /// <see cref="VideoStreamInfo.Duration"/> is the Segment's Duration (FFmpeg's duration of the file; it gives the
    /// tracks none), 0 without one.
    /// </remarks>
    public VideoStreamInfo? Video { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// AAC (A_AAC, and the old A_AAC/MPEG2/... and A_AAC/MPEG4/... names, whose AudioSpecificConfig is made from the name
    /// where the CodecPrivate is missing) is <see cref="CodecIds.Aac"/>, its rate and channels from the
    /// AudioSpecificConfig; other codecs keep Matroska's CodecID (A_OPUS, A_VORBIS, ...), with the track's
    /// SamplingFrequency and Channels.
    /// </remarks>
    public AudioStreamInfo? Audio { get; }

    /// <summary>Opens the file at <paramref name="path"/>, which the demuxer disposes with itself.</summary>
    public static MatroskaDemuxer Open(string path) => new(File.OpenRead(path));

    /// <inheritdoc/>
    public IEnumerable<Packet> ReadPackets() => ReadSteps.Run(_stream, Steps(), "Matroska");

    /// <inheritdoc cref="AviDemuxer.ReadPacketsAsync"/>
    public IAsyncEnumerable<Packet> ReadPacketsAsync(CancellationToken cancellationToken = default) => ReadSteps.RunAsync(_stream, Steps(), "Matroska", cancellationToken);

    /// <summary>
    /// The packets <see cref="ReadPackets"/> gives, in its order and with its values, without their data: from the index
    /// built at opening, with no read of the file.
    /// </summary>
    public IEnumerable<PacketInfo> ReadPacketInfo() => Malformed.Guard(Selected().Select(Info), "Matroska");

    /// <inheritdoc/>
    /// <remarks>
    /// The keyframe is the last one in decode order whose pts is at or before <paramref name="pts"/>, as in
    /// <see cref="Mp4Demuxer.Seek"/>; the other track starts the same way at the same instant. Audio frames are
    /// keyframes as their blocks say (every one, as encoders write them).
    /// </remarks>
    public void Seek(long pts, int streamIndex)
    {
        int seeked = _tracks[0] is { } v && v.StreamIndex == streamIndex ? 0
            : _tracks[1] is { } a && a.StreamIndex == streamIndex ? 1
            : throw new ArgumentOutOfRangeException(nameof(streamIndex), streamIndex, "No video or audio stream of the file has this index.");

        // The tracks share the Segment's time base.
        _start[seeked] = KeyframeAtOrBefore(seeked, pts);
        _start[1 - seeked] = KeyframeAtOrBefore(1 - seeked, pts);
    }

    /// <summary>Disposes the stream, unless the demuxer was made to leave it open.</summary>
    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    // The Sequence of the slot's last keyframe in decode order whose pts is at or before pts, 0 without one.
    private long KeyframeAtOrBefore(int slot, long pts)
    {
        long found = 0;
        foreach (var entry in _entries)
        {
            if (entry.Slot == slot && entry.Keyframe && entry.Pts <= pts)
            {
                found = entry.Sequence;
            }
        }

        return found;
    }

    // The entries a read gives: each slot's from where a Seek put its start.
    private IEnumerable<Entry> Selected()
    {
        foreach (var entry in _entries)
        {
            if (entry.Sequence >= _start[entry.Slot])
            {
                yield return entry;
            }
        }
    }

    private PacketInfo Info(Entry entry) =>
        new(_tracks[entry.Slot]!.StreamIndex, entry.Sequence, _tracks[entry.Slot]!.Prefix.Length + entry.Size, entry.Keyframe, entry.Dts, entry.Pts, entry.Duration);

    // Each frame's read (after the bytes header stripping took off, put back), then its packet.
    private IEnumerable<ReadStep> Steps()
    {
        foreach (var entry in Selected())
        {
            var info = Info(entry);
            var prefix = _tracks[entry.Slot]!.Prefix;
            var data = new byte[info.Size];
            prefix.CopyTo(data);
            yield return ReadStep.Read(entry.Offset, data.AsMemory(prefix.Length));
            yield return ReadStep.Yield(new Packet(info.StreamIndex, info.Sequence, data, info.IsKeyframe, info.Dts, info.Pts, info.Duration));
        }
    }

    // Decode times for a video track (entries in decode order): its presentation times sorted, delayed by the fewest
    // frames that keep every dts at or before its pts; the frames before that count back by the frame duration.
    private void SetDecodeTimes(List<int> indices, long frameTicks)
    {
        const int MaxDelay = 16;
        long[] pts = [.. indices.Select(i => _entries[i].Pts)];
        long[] sorted = [.. pts.OrderBy(t => t)];
        int delay = 0;
        while (delay < MaxDelay && delay < pts.Length && !Fits(delay))
        {
            delay++;
        }

        long step = frameTicks > 0 ? frameTicks : 1;
        for (int k = 0; k < indices.Count; k++)
        {
            long dts = k >= delay ? Math.Min(sorted[k - delay], pts[k]) : sorted[0] - ((delay - k) * step);
            _entries[indices[k]] = _entries[indices[k]] with { Dts = dts };
        }

        bool Fits(int d)
        {
            for (int k = d; k < pts.Length; k++)
            {
                if (sorted[k - d] > pts[k])
                {
                    return false;
                }
            }

            return true;
        }
    }

    // Audio: dts is pts, and a frame without a duration runs to the next one (the last as long as the one before).
    private void SetAudioTimes(List<int> indices)
    {
        long previous = 0;
        for (int k = 0; k < indices.Count; k++)
        {
            var entry = _entries[indices[k]];
            long duration = entry.Duration;
            if (duration == 0)
            {
                duration = k + 1 < indices.Count ? Math.Max(0, _entries[indices[k + 1]].Pts - entry.Pts) : previous;
            }

            _entries[indices[k]] = entry with { Dts = entry.Pts, Duration = duration };
            previous = duration;
        }
    }

    // Frames per second from the presentation times, for a video track without a DefaultDuration.
    private double MeasuredRate(List<int> indices, Rational timeBase)
    {
        if (indices.Count < 2)
        {
            return 0;
        }

        long first = indices.Min(i => _entries[i].Pts);
        long last = indices.Max(i => _entries[i].Pts);
        return last > first ? (indices.Count - 1) / ((last - first) * timeBase.ToDouble()) : 0;
    }

    /// <summary>One frame: where its data is, the track slot (0 video, 1 audio), and its packet's values.</summary>
    private readonly record struct Entry(long Offset, int Size, int Slot, long Sequence, long Pts, long Dts, long Duration, bool Keyframe);

    /// <summary>What the demuxer keeps of a TrackEntry.</summary>
    private sealed class TrackEntry
    {
        public ulong Number { get; set; }

        public int StreamIndex { get; set; }

        public ulong Type { get; set; }

        public string CodecId { get; set; } = "";

        public string Codec { get; set; } = "";

        public byte[] CodecPrivate { get; set; } = [];

        /// <summary>DefaultDuration, in nanoseconds; 0 without one.</summary>
        public long DefaultDuration { get; set; }

        /// <summary>CodecDelay, in nanoseconds.</summary>
        public long CodecDelay { get; set; }

        /// <summary>CodecDelay in the time base, rounded to nearest (FFmpeg moves the timestamps back by it).</summary>
        public long CodecDelayTicks { get; set; }

        /// <summary>The bytes header stripping took off the start of every frame.</summary>
        public byte[] Prefix { get; set; } = [];

        /// <summary>Why the track cannot be read (a content encoding), null where it can.</summary>
        public string? Unsupported { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public long DisplayWidth { get; set; }

        public long DisplayHeight { get; set; }

        public ulong DisplayUnit { get; set; }

        public Rational AspectRatio { get; set; } = new(0, 1);

        public ColorRange Range { get; set; }

        public ColorPrimaries Primaries { get; set; } = ColorPrimaries.Unspecified;

        public ColorTransfer Transfer { get; set; } = ColorTransfer.Unspecified;

        public ColorMatrix Matrix { get; set; } = ColorMatrix.Unspecified;

        public double SamplingFrequency { get; set; } = 8000;

        public double OutputSamplingFrequency { get; set; }

        public int SampleRate { get; set; }

        public int Channels { get; set; } = 1;

        public double TimestampScale { get; set; } = 1;
    }

    /// <summary>The walk through the file at opening: its headers, then every Cluster's blocks.</summary>
    private sealed class Walk(Stream stream)
    {
        private readonly Window _window = new(stream);
        private readonly long _length = stream.Length;
        private readonly long[] _sequences = new long[2];
        private bool _hasTracks;

        public List<Entry> Entries { get; } = [];

        public TrackEntry? VideoTrack { get; private set; }

        public TrackEntry? AudioTrack { get; private set; }

        public long TimestampScale { get; private set; } = 1_000_000;

        /// <summary>The Segment's Duration, in TimestampScale ticks; NaN without one.</summary>
        public double Duration { get; private set; } = double.NaN;

        public void Run()
        {
            if (!TryHeader(0, out uint id, out long size, out int headerLength) || id != Ebml.Header || size < 0)
            {
                throw new InvalidDataException("Not a Matroska file.");
            }

            string docType = "matroska";
            ReadOnlySpan<byte> ebml = ReadWhole(headerLength, size);
            while (Ebml.NextChild(ref ebml, out uint child, out var payload))
            {
                if (child == Ebml.DocType)
                {
                    docType = Ebml.String(payload);
                }
            }

            if (docType is not ("matroska" or "webm"))
            {
                throw new InvalidDataException($"Not a Matroska file: its DocType is '{docType}'.");
            }

            // The first Segment (later ones, of a chained file, are not read).
            long pos = headerLength + size;
            long segmentStart = -1;
            long segmentEnd = _length;
            while (TryHeader(pos, out id, out size, out headerLength))
            {
                if (id == Ebml.Segment)
                {
                    segmentStart = pos + headerLength;
                    segmentEnd = size < 0 ? _length : Math.Min(segmentStart + size, _length);
                    break;
                }

                if (size < 0)
                {
                    throw new InvalidDataException($"The element {id:X} at offset {pos} has an unknown size.");
                }

                pos += headerLength + size;
            }

            if (segmentStart < 0)
            {
                throw new InvalidDataException("Matroska file has no Segment.");
            }

            for (pos = segmentStart; pos < segmentEnd && TryHeader(pos, out id, out size, out headerLength);)
            {
                long payloadStart = pos + headerLength;
                if (id == Ebml.Cluster)
                {
                    if (!_hasTracks)
                    {
                        throw new NotSupportedException("A Tracks element after the first Cluster is not supported.");
                    }

                    pos = WalkCluster(payloadStart, size < 0 ? segmentEnd : Math.Min(payloadStart + size, segmentEnd), size < 0);
                    continue;
                }

                if (size < 0)
                {
                    throw new InvalidDataException($"The element {id:X} at offset {pos} has an unknown size.");
                }

                if (id == Ebml.Info)
                {
                    ReadInfo(ReadWhole(payloadStart, size));
                }
                else if (id == Ebml.Tracks && !_hasTracks)
                {
                    ReadTracks(ReadWhole(payloadStart, size));
                    _hasTracks = true;
                }

                pos = payloadStart + size;
            }

            if (!_hasTracks)
            {
                throw new InvalidDataException("Matroska file has no Tracks.");
            }
        }

        // An element's ID and size at pos; false at the end of the file (or where too little of a header is left).
        private bool TryHeader(long pos, out uint id, out long size, out int headerLength)
        {
            id = 0;
            size = 0;
            headerLength = 0;
            var head = _window.At(pos, 12);
            if (head.Length == 0)
            {
                return false;
            }

            if (!Ebml.TryReadId(head, out id, out int idLength) || !Ebml.TryReadSize(head[idLength..], out size, out int sizeLength))
            {
                if (head.Length < 12 && pos + head.Length >= _length)
                {
                    return false; // broken off inside the header
                }

                throw new InvalidDataException($"Invalid EBML element header at offset {pos}.");
            }

            headerLength = idLength + sizeLength;
            return true;
        }

        private byte[] ReadWhole(long pos, long size)
        {
            if (size > MaxHeaderElement || pos + size > _length)
            {
                throw new InvalidDataException($"The element at offset {pos} is too large or runs past the end of the file.");
            }

            var data = new byte[size];
            stream.Position = pos;
            stream.ReadExactly(data);
            return data;
        }

        private void ReadInfo(ReadOnlySpan<byte> info)
        {
            while (Ebml.NextChild(ref info, out uint id, out var payload))
            {
                if (id == Ebml.TimestampScale)
                {
                    ulong scale = Ebml.UInt(payload);
                    TimestampScale = scale is > 0 and <= int.MaxValue ? (long)scale : throw new InvalidDataException($"Invalid TimestampScale {scale}.");
                }
                else if (id == Ebml.Duration)
                {
                    Duration = Ebml.Float(payload);
                }
            }
        }

        private void ReadTracks(ReadOnlySpan<byte> tracks)
        {
            int streamIndex = 0;
            while (Ebml.NextChild(ref tracks, out uint id, out var payload))
            {
                if (id != Ebml.TrackEntry)
                {
                    continue;
                }

                var track = ReadTrackEntry(payload);
                track.StreamIndex = streamIndex++;
                if (track.Type == 1 && VideoTrack is null)
                {
                    VideoTrack = Exposed(track);
                }
                else if (track.Type == 2 && AudioTrack is null)
                {
                    AudioTrack = Exposed(track);
                }
            }
        }

        private TrackEntry Exposed(TrackEntry track)
        {
            if (track.Unsupported is { } reason)
            {
                throw new NotSupportedException(reason);
            }

            if (track.TimestampScale != 1)
            {
                throw new NotSupportedException($"A TrackTimestampScale of {track.TimestampScale} is not supported.");
            }

            // av_rescale_q's rounding (to nearest, halves away from zero) of nanoseconds into the time base.
            track.CodecDelayTicks = (track.CodecDelay + (TimestampScale / 2)) / TimestampScale;
            track.Codec = NormalizeCodec(track.CodecId);
            if (track.Type == 1)
            {
                // The display size's ratio over the pixels' (in any DisplayUnit but 4, unknown, only the ratio counts), as
                // FFmpeg reads it; its default is the pixel size, so a file without one says 1:1.
                long displayWidth = track.DisplayWidth > 0 ? track.DisplayWidth : track.Width;
                long displayHeight = track.DisplayHeight > 0 ? track.DisplayHeight : track.Height;
                track.AspectRatio = track.DisplayUnit < 4 && displayWidth > 0 && displayHeight > 0 && track.Width > 0 && track.Height > 0
                    ? ColorCodes.AspectRatio(checked(displayWidth * track.Height), checked(displayHeight * track.Width))
                    : new(0, 1);
            }
            else
            {
                track.SampleRate = track.SamplingFrequency is > 0 and < int.MaxValue ? (int)track.SamplingFrequency : 0;
                if (track.Codec == CodecIds.Aac)
                {
                    if (track.CodecPrivate.Length == 0)
                    {
                        track.CodecPrivate = AacConfigFromName(track.CodecId, track.SampleRate, track.Channels, track.OutputSamplingFrequency);
                    }

                    if (AudioSpecificConfigs.TryRead(track.CodecPrivate, out int rate, out int channels))
                    {
                        track.SampleRate = rate;
                        track.Channels = channels > 0 ? channels : track.Channels; // 0: in a program_config_element
                    }
                }
            }

            return track;
        }

        private static TrackEntry ReadTrackEntry(ReadOnlySpan<byte> entry)
        {
            var track = new TrackEntry();
            while (Ebml.NextChild(ref entry, out uint id, out var payload))
            {
                switch (id)
                {
                    case Ebml.TrackNumber:
                        track.Number = Ebml.UInt(payload);
                        break;
                    case Ebml.TrackType:
                        track.Type = Ebml.UInt(payload);
                        break;
                    case Ebml.CodecId:
                        track.CodecId = Ebml.String(payload);
                        break;
                    case Ebml.CodecPrivate:
                        track.CodecPrivate = payload.ToArray();
                        break;
                    case Ebml.DefaultDuration:
                        track.DefaultDuration = Ebml.Bounded(payload);
                        break;
                    case Ebml.CodecDelay:
                        track.CodecDelay = Ebml.Bounded(payload);
                        break;
                    case Ebml.TrackTimestampScale:
                        track.TimestampScale = Ebml.Float(payload);
                        break;
                    case Ebml.Video:
                        ReadVideo(payload, track);
                        break;
                    case Ebml.Audio:
                        ReadAudio(payload, track);
                        break;
                    case Ebml.ContentEncodings:
                        ReadContentEncodings(payload, track);
                        break;
                    default:
                        break;
                }
            }

            return track;
        }

        private static void ReadVideo(ReadOnlySpan<byte> video, TrackEntry track)
        {
            while (Ebml.NextChild(ref video, out uint id, out var payload))
            {
                switch (id)
                {
                    case Ebml.PixelWidth:
                        track.Width = checked((int)Ebml.UInt(payload));
                        break;
                    case Ebml.PixelHeight:
                        track.Height = checked((int)Ebml.UInt(payload));
                        break;
                    case Ebml.DisplayWidth:
                        track.DisplayWidth = checked((int)Ebml.UInt(payload));
                        break;
                    case Ebml.DisplayHeight:
                        track.DisplayHeight = checked((int)Ebml.UInt(payload));
                        break;
                    case Ebml.DisplayUnit:
                        track.DisplayUnit = Ebml.UInt(payload);
                        break;
                    case Ebml.Colour:
                        ReadColour(payload, track);
                        break;
                    default:
                        break;
                }
            }
        }

        // Colour (RFC 9559 5.1.4.1.28.x): the codes are ISO/IEC 23001-8's (H.273), as MP4's 'colr'; Range 1 is
        // broadcast (limited), 2 full, 3 defined by the matrix and transfer (unspecified here).
        private static void ReadColour(ReadOnlySpan<byte> colour, TrackEntry track)
        {
            while (Ebml.NextChild(ref colour, out uint id, out var payload))
            {
                int value = (int)Math.Min(Ebml.UInt(payload), int.MaxValue);
                switch (id)
                {
                    case Ebml.MatrixCoefficients:
                        track.Matrix = ColorCodes.MatrixOf(value);
                        break;
                    case Ebml.Range:
                        track.Range = value switch
                        {
                            1 => ColorRange.Limited,
                            2 => ColorRange.Full,
                            _ => ColorRange.Unspecified,
                        };
                        break;
                    case Ebml.TransferCharacteristics:
                        track.Transfer = ColorCodes.TransferOf(value);
                        break;
                    case Ebml.Primaries:
                        track.Primaries = ColorCodes.PrimariesOf(value);
                        break;
                    default:
                        break;
                }
            }
        }

        private static void ReadAudio(ReadOnlySpan<byte> audio, TrackEntry track)
        {
            while (Ebml.NextChild(ref audio, out uint id, out var payload))
            {
                switch (id)
                {
                    case Ebml.SamplingFrequency:
                        track.SamplingFrequency = Ebml.Float(payload);
                        break;
                    case Ebml.OutputSamplingFrequency:
                        track.OutputSamplingFrequency = Ebml.Float(payload);
                        break;
                    case Ebml.Channels:
                        track.Channels = (int)Math.Min(Ebml.UInt(payload), int.MaxValue);
                        break;
                    default:
                        break;
                }
            }
        }

        // ContentEncodings (RFC 9559 5.1.4.1.31): header stripping of the frames is undone (its bytes put back in front
        // of each); anything else makes the track unreadable.
        private static void ReadContentEncodings(ReadOnlySpan<byte> encodings, TrackEntry track)
        {
            int count = 0;
            while (Ebml.NextChild(ref encodings, out uint id, out var encoding))
            {
                if (id != Ebml.ContentEncoding)
                {
                    continue;
                }

                count++;
                ulong scope = 1;
                ulong type = 0;
                ulong algorithm = 0;
                byte[] settings = [];
                bool compression = false;
                while (Ebml.NextChild(ref encoding, out uint field, out var payload))
                {
                    if (field == Ebml.ContentEncodingScope)
                    {
                        scope = Ebml.UInt(payload);
                    }
                    else if (field == Ebml.ContentEncodingType)
                    {
                        type = Ebml.UInt(payload);
                    }
                    else if (field == Ebml.ContentCompression)
                    {
                        compression = true;
                        while (Ebml.NextChild(ref payload, out uint part, out var value))
                        {
                            if (part == Ebml.ContentCompAlgo)
                            {
                                algorithm = Ebml.UInt(value);
                            }
                            else if (part == Ebml.ContentCompSettings)
                            {
                                settings = value.ToArray();
                            }
                        }
                    }
                }

                if (type == 0 && compression && algorithm == 3 && scope == 1)
                {
                    track.Prefix = settings;
                }
                else
                {
                    track.Unsupported = type != 0
                        ? "Encrypted Matroska tracks are not supported."
                        : $"Matroska content compression {algorithm} (scope {scope}) is not supported; only header stripping of the frames.";
                }
            }

            if (count > 1)
            {
                track.Unsupported = "More than one content encoding of a Matroska track is not supported.";
            }
        }

        // The Codec IDs (Matroska's codec specifications) of the codecs CodecIds names.
        private static string NormalizeCodec(string codecId) => codecId switch
        {
            "V_MPEG4/ISO/AVC" => CodecIds.H264,
            "V_MJPEG" => CodecIds.Mjpeg,
            "V_MPEG4/ISO/SP" or "V_MPEG4/ISO/ASP" or "V_MPEG4/ISO/AP" => CodecIds.Mpeg4Visual,
            _ when codecId == "A_AAC" || codecId.StartsWith("A_AAC/", StringComparison.Ordinal) => CodecIds.Aac,
            _ => codecId,
        };

        /// <summary>
        /// The AudioSpecificConfig (ISO/IEC 14496-3 1.6.2.1) an old AAC Codec ID describes, which carries no CodecPrivate:
        /// A_AAC/MPEG2/ or A_AAC/MPEG4/, then MAIN, LC, SSR or LTP (object types 1 to 4; LC/SBR is LC with an SBR sync
        /// extension at the output rate). Empty where the name or a rate does not fit.
        /// </summary>
        internal static byte[] AacConfigFromName(string codecId, int sampleRate, int channels, double outputRate)
        {
            string profile = codecId.StartsWith("A_AAC/MPEG2/", StringComparison.Ordinal) || codecId.StartsWith("A_AAC/MPEG4/", StringComparison.Ordinal)
                ? codecId["A_AAC/MPEG2/".Length..]
                : "";
            bool sbr = profile == "LC/SBR";
            int objectType = profile switch
            {
                "MAIN" => 1,
                "LC" or "LC/SBR" => 2,
                "SSR" => 3,
                "LTP" => 4,
                _ => 0,
            };
            int index = RateIndex(sampleRate);
            int outputIndex = RateIndex(outputRate > 0 && outputRate < int.MaxValue ? (int)outputRate : sampleRate * 2);
            if (objectType == 0 || index < 0 || channels is < 1 or > 7 || (sbr && outputIndex < 0))
            {
                return [];
            }

            // audioObjectType (5), samplingFrequencyIndex (4), channelConfiguration (4), GASpecificConfig's three flags (0).
            var config = new List<byte> { (byte)((objectType << 3) | (index >> 1)), (byte)(((index & 1) << 7) | (channels << 3)) };
            if (sbr)
            {
                // The sync extension: syncExtensionType 0x2B7 (11 bits), extensionAudioObjectType 5 (SBR), sbrPresentFlag 1,
                // extensionSamplingFrequencyIndex.
                config.AddRange([0x56, 0xE5, (byte)(0x80 | (outputIndex << 3))]);
            }

            return [.. config];
        }

        private static int RateIndex(int rate)
        {
            ReadOnlySpan<int> rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
            return rates.IndexOf(rate);
        }

        // A Cluster's blocks, from start to at most end; an unknown-size one ends at the first element that is not a
        // Cluster's child. Returns where the Cluster ends (or where the file breaks off).
        private long WalkCluster(long start, long end, bool unknownSize)
        {
            long clusterTime = long.MinValue;
            long pos = start;
            while (pos < end && TryHeader(pos, out uint id, out long size, out int headerLength))
            {
                if (unknownSize && Ebml.IsTopLevel(id))
                {
                    return pos;
                }

                if (size < 0)
                {
                    throw new InvalidDataException($"The element {id:X} at offset {pos} has an unknown size.");
                }

                long payloadStart = pos + headerLength;
                long payloadEnd = payloadStart + size;
                if (payloadEnd > _length)
                {
                    return _length; // broken off: the blocks before it are the file's
                }

                if (!unknownSize && payloadEnd > end)
                {
                    throw new InvalidDataException($"The element at offset {pos} runs past the end of its Cluster.");
                }

                if (id == Ebml.Timestamp)
                {
                    clusterTime = checked((long)Ebml.UInt(_window.At(payloadStart, Ebml.Bytes(size))));
                }
                else if (id == Ebml.SimpleBlock)
                {
                    AddBlock(payloadStart, size, clusterTime, keyframe: null, blockDuration: 0);
                }
                else if (id == Ebml.BlockGroup)
                {
                    ReadBlockGroup(payloadStart, payloadEnd, clusterTime);
                }

                pos = payloadEnd;
            }

            return pos;
        }

        private void ReadBlockGroup(long start, long end, long clusterTime)
        {
            long blockStart = -1;
            long blockSize = 0;
            long duration = 0;
            bool referenced = false;
            for (long pos = start; pos < end && TryHeader(pos, out uint id, out long size, out int headerLength);)
            {
                long payloadStart = pos + headerLength;
                if (size < 0 || payloadStart + size > end)
                {
                    throw new InvalidDataException($"The element at offset {pos} runs past the end of its BlockGroup.");
                }

                if (id == Ebml.Block)
                {
                    (blockStart, blockSize) = (payloadStart, size);
                }
                else if (id == Ebml.BlockDuration)
                {
                    duration = Ebml.Bounded(_window.At(payloadStart, Ebml.Bytes(size)));
                }
                else if (id == Ebml.ReferenceBlock)
                {
                    referenced = true;
                }

                pos = payloadStart + size;
            }

            if (blockStart >= 0)
            {
                AddBlock(blockStart, blockSize, clusterTime, keyframe: !referenced, duration);
            }
        }

        // A block's frames (RFC 9559 10, Block Structure; 10.3 lacing): track number, timestamp (relative to the
        // Cluster's), flags, then the lace's frame count and sizes. keyframe: null for a SimpleBlock (its flag says).
        private void AddBlock(long start, long size, long clusterTime, bool? keyframe, long blockDuration)
        {
            var head = _window.At(start, (int)Math.Min(size, Window.Size));
            if (!Ebml.TryReadSize(head, out long number, out int numberLength) || head.Length < numberLength + 3)
            {
                throw new InvalidDataException($"Invalid block header at offset {start}.");
            }

            int slot = VideoTrack is { } v && (ulong)number == v.Number ? 0 : AudioTrack is { } a && (ulong)number == a.Number ? 1 : -1;
            if (slot < 0)
            {
                return;
            }

            var track = slot == 0 ? VideoTrack! : AudioTrack!;
            if (clusterTime == long.MinValue)
            {
                throw new InvalidDataException($"The block at offset {start} comes before its Cluster's Timestamp.");
            }

            short relative = BinaryPrimitives.ReadInt16BigEndian(head[numberLength..]);
            byte flags = head[numberLength + 2];
            bool isKeyframe = keyframe ?? (flags & 0x80) != 0;
            int pos = numberLength + 3;
            Span<int> sizes = stackalloc int[256];
            int frames = 1;
            int lacing = (flags >> 1) & 3;
            long data = size - pos;
            if (lacing != 0)
            {
                Require(head, pos + 1, start);
                frames = head[pos++] + 1;
                long listed = 0;
                if (lacing == 1)
                {
                    // Xiph: each size but the last as bytes of 255 and the byte that ends it.
                    for (int f = 0; f < frames - 1; f++)
                    {
                        int value = 0;
                        byte b;
                        do
                        {
                            Require(head, pos + 1, start);
                            b = head[pos++];
                            value = checked(value + b);
                        }
                        while (b == 255);
                        sizes[f] = value;
                        listed += value;
                    }
                }
                else if (lacing == 3)
                {
                    // EBML: the first size, then each next but the last as a signed difference from the one before.
                    long previous = 0;
                    for (int f = 0; f < frames - 1; f++)
                    {
                        if (!Ebml.TryReadSize(head[pos..], out long value, out int length) || value < 0)
                        {
                            throw new InvalidDataException($"Invalid EBML lace size in the block at offset {start}.");
                        }

                        if (f > 0)
                        {
                            value = previous + value - ((1L << ((7 * length) - 1)) - 1);
                        }

                        pos += length;
                        sizes[f] = value is >= 0 and <= int.MaxValue ? (int)value : throw new InvalidDataException($"Invalid EBML lace size in the block at offset {start}.");
                        listed += value;
                        previous = value;
                    }
                }

                data = size - pos;
                if (lacing == 2)
                {
                    // Fixed-size: the frames share the data equally.
                    if (data % frames != 0)
                    {
                        throw new InvalidDataException($"The fixed-size lace at offset {start} does not divide into {frames} frames.");
                    }

                    for (int f = 0; f < frames - 1; f++)
                    {
                        sizes[f] = (int)(data / frames);
                        listed += sizes[f];
                    }
                }

                if (listed > data)
                {
                    throw new InvalidDataException($"The lace sizes of the block at offset {start} exceed its data.");
                }

                data -= listed;
            }

            sizes[frames - 1] = data <= int.MaxValue ? (int)data : throw new InvalidDataException($"The block at offset {start} is too large.");
            if (Entries.Count + frames > MaxPackets)
            {
                throw new InvalidDataException($"Matroska file has more than {MaxPackets} frames.");
            }

            long duration = blockDuration > 0 ? blockDuration
                : track.DefaultDuration > 0 ? checked(track.DefaultDuration * frames) / TimestampScale
                : 0;
            long pts = checked(clusterTime + relative - track.CodecDelayTicks);
            long offset = start + pos;
            for (int f = 0; f < frames; f++)
            {
                long share = (duration * (f + 1) / frames) - (duration * f / frames);
                Entries.Add(new Entry(offset, sizes[f], slot, _sequences[slot]++, pts, pts, share, isKeyframe));
                offset += sizes[f];
                pts += share;
            }
        }

        private static void Require(ReadOnlySpan<byte> head, int length, long start)
        {
            if (head.Length < length)
            {
                throw new InvalidDataException($"The lace header of the block at offset {start} runs past its data.");
            }
        }
    }

    /// <summary>
    /// Reads of the file through one buffer: the walk reads a few bytes at each element, mostly near the last ones, so
    /// one read of the stream serves many.
    /// </summary>
    private sealed class Window(Stream stream)
    {
        public const int Size = 1 << 16;

        private readonly byte[] _buffer = new byte[Size];
        private long _start = -1;
        private int _length;

        /// <summary>The count bytes at position, fewer at the end of the file.</summary>
        public ReadOnlySpan<byte> At(long position, int count)
        {
            if (_start < 0 || position < _start || position + count > _start + _length)
            {
                stream.Position = position;
                _length = stream.ReadAtLeast(_buffer, _buffer.Length, throwOnEndOfStream: false);
                _start = position;
            }

            long available = Math.Min(count, _start + _length - position);
            return _buffer.AsSpan((int)(position - _start), (int)Math.Max(0, available));
        }
    }
}

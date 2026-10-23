using System.Buffers.Binary;
using System.Text;

namespace ManagedCodecs.Containers.Mp4;

/// <summary>
/// MP4 / QuickTime (ISO/IEC 14496-12) demuxer: finds the first video and the first audio track
/// and yields their samples, from the sample tables in 'moov' and, in fragmented files, from the
/// track fragments of every 'moof' (8.8).
/// </summary>
/// <remarks>
/// <para>
/// The edit list is applied as FFmpeg applies it, packet for packet. Each edit's samples come in turn, their times moved
/// so that its media time falls at its start in the track (so H.264 with B-frames starts at pts 0 and a negative dts,
/// and AAC encoder priming has negative timestamps); empty edits before the first delay it. Samples an edit needs
/// decoded but not shown come as <see cref="Packet.Discard"/> packets: in video, those from the keyframe before the
/// edit's first frame, and those after its end up to the second keyframe past it (the first one with no composition
/// offsets); in audio, a second of samples before the edit, which primes the decoder. Audio edits take the whole
/// sample their end falls in; the first edit starts in the sample its media time falls in, whose earlier part
/// <see cref="Packet.SkipSamples"/> cuts (encoder priming), a later one at the next whole sample (as in FFmpeg, up to a
/// sample late). Audio durations run up to the next packet's dts, and the last packet's to the end of the edits. An
/// empty edit after a real one plays the media from its start, as FFmpeg reads its media time, -1.
/// </para>
/// <para>
/// A fragment's samples start at its 'tfdt' decode time, or right after the track's previous sample without one.
/// Negative composition offsets move every pts of their track forward by the largest of them, as FFmpeg does (which,
/// for 'ctts', looks at all entries but the last two). Not yet supported: compressed 'cmov' headers, and further
/// tracks.
/// </para>
/// </remarks>
public sealed class Mp4Demuxer : IDemuxer
{
    // Bounds the arrays built from a header that claims an absurd number of samples.
    private const int MaxSamples = 1 << 26;

    // Bounds the work of an edit list, each edit of which looks through the samples (real ones have a few edits).
    private const int MaxEdits = 1024;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    // Samples of every exposed track, merged in file order.
    private readonly Sample[] _samples;

    public Mp4Demuxer(Stream stream, bool leaveOpen = false)
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
            var moov = ReadMoov(stream, out bool hasFragments);
            uint movieTimescale = 0;
            Track? video = null;
            Track? audio = null;
            int trackIndex = 0;
            ReadOnlySpan<byte> mvex = default;

            ReadOnlySpan<byte> boxes = moov;
            while (NextBox(ref boxes, out uint type, out var payload))
            {
                if (type == Box.Mvhd)
                {
                    movieTimescale = ReadUInt32(payload, payload.Length > 0 && payload[0] == 1 ? 20 : 12);
                }
                else if (type == Box.Trak)
                {
                    uint handler = HandlerType(payload);
                    if (video is null && handler == Box.Vide)
                    {
                        video = new Track(payload, isAudio: false) { StreamIndex = trackIndex };
                    }
                    else if (audio is null && handler == Box.Soun)
                    {
                        audio = new Track(payload, isAudio: true) { StreamIndex = trackIndex };
                    }

                    trackIndex++;
                }
                else if (type == Box.Mvex)
                {
                    mvex = payload;
                }
            }

            if (video is null && audio is null)
            {
                throw new InvalidDataException("MP4 file has no video or audio track.");
            }

            if (hasFragments)
            {
                Track?[] tracks = [video, audio];
                ReadTrackExtends(mvex, tracks);
                ReadFragments(stream, tracks);
            }

            Sample[] videoSamples = [];
            Sample[] audioSamples = [];
            if (video is not null)
            {
                var samples = video.BuildSamples(movieTimescale);
                videoSamples = samples;

                long totalDuration = 0;
                foreach (var sample in samples)
                {
                    totalDuration += sample.Duration;
                }

                double fps = totalDuration > 0 ? samples.Length * (double)video.Timescale / totalDuration : 0;

                // ReadPackets skips empty samples, so the frame count does too.
                Video = new VideoStreamInfo(
                    video.Codec,
                    video.Width,
                    video.Height,
                    fps,
                    samples.Count(sample => sample.Size > 0),
                    new Rational(1, checked((int)video.Timescale)),
                    video.CodecPrivate,
                    video.StreamIndex);
            }

            if (audio is not null)
            {
                var samples = audio.BuildSamples(movieTimescale);
                audioSamples = samples;
                Audio = new AudioStreamInfo(
                    audio.Codec,
                    audio.SampleRate,
                    audio.Channels,
                    samples.Count(sample => sample.Size > 0),
                    new Rational(1, checked((int)audio.Timescale)),
                    audio.CodecPrivate,
                    audio.StreamIndex);
            }

            _samples = Interleave(videoSamples, audioSamples);
        }
        catch (Exception e) when (Malformed.IsMalformedInput(e))
        {
            throw Malformed.Wrap(e, "MP4");
        }
    }

    public VideoStreamInfo? Video { get; }

    public AudioStreamInfo? Audio { get; }

    /// <summary>
    /// The two tracks' samples in file order, which is how they are interleaved on disk, each track's in its own order:
    /// the next sample is whichever track's next one lies first in the file (an edit can go back to earlier samples).
    /// </summary>
    private static Sample[] Interleave(Sample[] first, Sample[] second)
    {
        var merged = new Sample[first.Length + second.Length];
        int a = 0;
        int b = 0;
        for (int i = 0; i < merged.Length; i++)
        {
            merged[i] = b == second.Length || (a < first.Length && first[a].Offset <= second[b].Offset) ? first[a++] : second[b++];
        }

        return merged;
    }

    public static Mp4Demuxer Open(string path) => new(File.OpenRead(path));

    public IEnumerable<Packet> ReadPackets() => Malformed.Guard(ReadPacketsCore(), "MP4");

    /// <inheritdoc/>
    public IAsyncEnumerable<Packet> ReadPacketsAsync(CancellationToken cancellationToken = default) => AsyncPackets.Of(ReadPackets(), cancellationToken);

    private IEnumerable<Packet> ReadPacketsCore()
    {
        var sequences = new Dictionary<int, long>();
        for (int i = 0; i < _samples.Length; i++)
        {
            var sample = _samples[i];
            if (sample.Size == 0)
            {
                continue;
            }

            if (sample.Offset < 0 || sample.Offset + sample.Size > _stream.Length)
            {
                throw new InvalidDataException($"Sample {i} lies outside the file.");
            }

            var data = new byte[sample.Size];
            _stream.Position = sample.Offset;
            _stream.ReadExactly(data);
            long sequence = sequences.GetValueOrDefault(sample.StreamIndex);
            sequences[sample.StreamIndex] = sequence + 1;
            yield return new Packet(sample.StreamIndex, sequence, data, sample.IsKeyframe, sample.Dts, sample.Pts, sample.Duration, sample.SkipSamples, sample.Discard);
        }
    }

    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    /// <summary>
    /// Scans the top-level boxes and reads the 'moov' box's payload; <paramref name="hasFragments"/>
    /// tells whether any 'moof' box follows (or precedes) it.
    /// </summary>
    private static byte[] ReadMoov(Stream stream, out bool hasFragments)
    {
        byte[]? moov = null;
        hasFragments = false;
        foreach (var (type, offset, headerSize, size) in TopLevelBoxes(stream))
        {
            if (type == Box.Moov && moov is null)
            {
                moov = new byte[checked((int)(size - headerSize))];
                stream.Position = offset + headerSize;
                stream.ReadExactly(moov);
            }
            else if (type == Box.Moof)
            {
                hasFragments = true;
                if (moov is not null)
                {
                    break; // whether there are fragments is all that is needed here
                }
            }
            else if (type == Box.Cmov)
            {
                throw new NotSupportedException("Compressed MP4 headers ('cmov') are not supported.");
            }
        }

        return moov ?? throw new InvalidDataException("Not an MP4 file: no 'moov' box.");
    }

    /// <summary>The top-level boxes: type, file offset, header size and total size.</summary>
    private static IEnumerable<(uint Type, long Offset, int HeaderSize, long Size)> TopLevelBoxes(Stream stream)
    {
        var header = new byte[16]; // an iterator cannot keep stack memory across yield
        long length = stream.Length;
        long pos = 0;

        while (pos + 8 <= length)
        {
            stream.Position = pos;
            stream.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            int headerSize = 8;

            if (size == 1)
            {
                stream.ReadExactly(header, 8, 8);
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = length - pos; // extends to the end of the file
            }

            if (size < headerSize)
            {
                throw new InvalidDataException($"Invalid size {size} for box '{Box.Name(type)}' at offset {pos}.");
            }

            if (type is Box.Moov or Box.Moof && pos + size > length)
            {
                throw new InvalidDataException($"The '{Box.Name(type)}' box is truncated.");
            }

            yield return (type, pos, headerSize, size);
            pos += size;
        }
    }

    /// <summary>Each track's defaults for its fragments, from 'trex' in 'mvex' (8.8.3).</summary>
    private static void ReadTrackExtends(ReadOnlySpan<byte> mvex, Track?[] tracks)
    {
        while (NextBox(ref mvex, out uint type, out var trex))
        {
            if (type != Box.Trex)
            {
                continue;
            }

            Require(trex, 24, "trex");
            uint trackId = ReadUInt32(trex, 4);
            foreach (var track in tracks)
            {
                if (track?.TrackId == trackId)
                {
                    track.SetFragmentDefaults(ReadUInt32(trex, 12), ReadUInt32(trex, 16), ReadUInt32(trex, 20));
                }
            }
        }
    }

    /// <summary>Adds the samples of every track fragment of every 'moof' to the tracks they belong to (8.8.4).</summary>
    private static void ReadFragments(Stream stream, Track?[] tracks)
    {
        foreach (var (type, offset, headerSize, size) in TopLevelBoxes(stream).ToList())
        {
            if (type != Box.Moof)
            {
                continue;
            }

            var moof = new byte[checked((int)(size - headerSize))];
            stream.Position = offset + headerSize;
            stream.ReadExactly(moof);

            // Without an explicit base, the first track fragment's data starts at the 'moof' and
            // each later one's where the previous one's ended (8.8.7.1). Where that one belongs to a
            // track not exposed, its data length is not worked out, so a following fragment
            // without a base of its own cannot be placed.
            long dataEnd = offset;
            bool skipped = false;
            ReadOnlySpan<byte> boxes = moof;
            while (NextBox(ref boxes, out uint child, out var traf))
            {
                if (child != Box.Traf)
                {
                    continue;
                }

                if (!FindChild(traf, Box.Tfhd, out var tfhd))
                {
                    throw new InvalidDataException("Track fragment without 'tfhd'.");
                }

                uint trackId = ReadUInt32(Require(tfhd, 8, "tfhd"), 4);
                var track = Array.Find(tracks, t => t?.TrackId == trackId);
                if (track is null)
                {
                    skipped = true; // a track not exposed
                    continue;
                }

                if (skipped && (ReadUInt32(tfhd, 0) & 0x020001) == 0)
                {
                    throw new NotSupportedException("A track fragment without a base offset follows one of a track that is not read.");
                }

                dataEnd = track.AddFragment(traf, offset, dataEnd);
            }
        }
    }

    /// <summary>The track's handler_type ('vide', 'soun', ...), or 0 when it has none.</summary>
    private static uint HandlerType(ReadOnlySpan<byte> trak) =>
        FindChild(trak, Box.Mdia, out var mdia) && FindChild(mdia, Box.Hdlr, out var hdlr)
            ? ReadUInt32(hdlr, 8) // FullBox header, pre_defined, then handler_type
            : 0;

    /// <summary>
    /// Takes the next box off the front of <paramref name="data"/>. Returns false at the end;
    /// throws when a box claims to be larger than what is left of its parent.
    /// </summary>
    private static bool NextBox(scoped ref ReadOnlySpan<byte> data, out uint type, out ReadOnlySpan<byte> payload)
    {
        type = 0;
        payload = default;
        if (data.Length < 8)
        {
            return false;
        }

        ulong size = BinaryPrimitives.ReadUInt32BigEndian(data);
        type = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        int headerSize = 8;

        if (size == 1)
        {
            if (data.Length < 16)
            {
                throw new InvalidDataException($"Truncated box '{Box.Name(type)}'.");
            }

            size = BinaryPrimitives.ReadUInt64BigEndian(data[8..]);
            headerSize = 16;
        }
        else if (size == 0)
        {
            size = (ulong)data.Length;
        }

        if (size < (ulong)headerSize || size > (ulong)data.Length)
        {
            throw new InvalidDataException($"Invalid size {size} for box '{Box.Name(type)}'.");
        }

        payload = data[headerSize..(int)size];
        data = data[(int)size..];
        return true;
    }

    private static bool FindChild(ReadOnlySpan<byte> parent, uint wanted, out ReadOnlySpan<byte> payload)
    {
        while (NextBox(ref parent, out uint type, out payload))
        {
            if (type == wanted)
            {
                return true;
            }
        }

        payload = default;
        return false;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
    {
        if (data.Length < offset + 4)
        {
            throw new InvalidDataException("Box too short.");
        }

        return BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
    }

    private static ReadOnlySpan<byte> Require(ReadOnlySpan<byte> data, int length, string box)
    {
        if (data.Length < length)
        {
            throw new InvalidDataException($"The '{box}' box is too short.");
        }

        return data;
    }

    private readonly record struct Sample(int StreamIndex, long Offset, int Size, long Dts, long Pts, int Duration, bool IsKeyframe, int SkipSamples = 0, bool Discard = false);

    /// <summary>The parts of one 'trak' needed to build its sample list.</summary>
    private sealed class Track
    {
        private readonly byte[] _stsz = [];
        private readonly byte[] _stz2 = [];
        private readonly byte[] _stco = [];
        private readonly byte[] _co64 = [];
        private readonly byte[] _stsc = [];
        private readonly byte[] _stts = [];
        private readonly byte[] _ctts = [];
        private readonly byte[]? _stss;
        private readonly byte[]? _elst;

        private readonly bool _isAudio;

        // Samples from track fragments: offset, size, decode time (-1 to continue from the
        // previous sample), duration, composition offset and sync flag.
        private readonly List<(long Offset, int Size, long Dts, int Duration, int CompositionOffset, bool IsKeyframe)> _fragmentSamples = [];

        // 'trex' defaults for the fragments: sample duration, size and flags.
        private (uint Duration, uint Size, uint Flags) _fragmentDefaults;

        public Track(ReadOnlySpan<byte> trak, bool isAudio)
        {
            _isAudio = isAudio;
            if (FindChild(trak, Box.Tkhd, out var tkhd))
            {
                TrackId = ReadUInt32(tkhd, tkhd.Length > 0 && tkhd[0] == 1 ? 20 : 12);
            }

            if (FindChild(trak, Box.Edts, out var edts) && FindChild(edts, Box.Elst, out var elst))
            {
                _elst = elst.ToArray();
            }

            if (!FindChild(trak, Box.Mdia, out var mdia) || !FindChild(mdia, Box.Mdhd, out var mdhd))
            {
                throw new InvalidDataException("Track has no 'mdhd' box.");
            }

            Timescale = ReadUInt32(mdhd, mdhd.Length > 0 && mdhd[0] == 1 ? 20 : 12);
            if (Timescale == 0 || Timescale > int.MaxValue)
            {
                throw new InvalidDataException($"Invalid track timescale {Timescale}.");
            }

            if (!FindChild(mdia, Box.Minf, out var minf) || !FindChild(minf, Box.Stbl, out var stbl))
            {
                throw new InvalidDataException("Track has no sample table.");
            }

            var boxes = stbl;
            while (NextBox(ref boxes, out uint type, out var payload))
            {
                switch (type)
                {
                    case Box.Stsd: ParseSampleDescription(payload); break;
                    case Box.Stsz: _stsz = payload.ToArray(); break;
                    case Box.Stz2: _stz2 = payload.ToArray(); break;
                    case Box.Stco: _stco = payload.ToArray(); break;
                    case Box.Co64: _co64 = payload.ToArray(); break;
                    case Box.Stsc: _stsc = payload.ToArray(); break;
                    case Box.Stts: _stts = payload.ToArray(); break;
                    case Box.Ctts: _ctts = payload.ToArray(); break;
                    case Box.Stss: _stss = payload.ToArray(); break;
                }
            }

            if (_stsz.Length >= 12)
            {
                SampleCount = ReadUInt32(_stsz, 8);
            }
            else if (_stz2.Length >= 12)
            {
                SampleCount = ReadUInt32(_stz2, 8);
            }
        }

        public int StreamIndex { get; init; }

        /// <summary>track_ID from 'tkhd', which 'trex' and 'tfhd' refer to.</summary>
        public uint TrackId { get; }

        public uint Timescale { get; }

        public uint SampleCount { get; }

        public string Codec { get; private set; } = "????";

        public int Width { get; private set; }

        public int Height { get; private set; }

        public int SampleRate { get; private set; }

        public int Channels { get; private set; }

        public ReadOnlyMemory<byte> CodecPrivate { get; private set; } = ReadOnlyMemory<byte>.Empty;

        public void SetFragmentDefaults(uint duration, uint size, uint flags) => _fragmentDefaults = (duration, size, flags);

        /// <summary>
        /// Adds the samples of one 'traf' (8.8.6 to 8.8.12) of the 'moof' at <paramref name="moofOffset"/>;
        /// <paramref name="dataEnd"/> is where the previous track fragment's data ended. Returns
        /// where this one's ends.
        /// </summary>
        public long AddFragment(ReadOnlySpan<byte> traf, long moofOffset, long dataEnd)
        {
            FindChild(traf, Box.Tfhd, out var tfhd);
            uint flags = ReadUInt32(tfhd, 0) & 0xFFFFFF;
            int pos = 8;
            long baseOffset = (flags & 0x020000) != 0 ? moofOffset : dataEnd; // default-base-is-moof
            if ((flags & 0x01) != 0)
            {
                baseOffset = checked((long)BinaryPrimitives.ReadUInt64BigEndian(Require(tfhd, pos + 8, "tfhd")[pos..]));
                pos += 8;
            }

            if ((flags & 0x02) != 0)
            {
                pos += 4; // sample_description_index: one description is all that is used
            }

            uint defaultDuration = (flags & 0x08) != 0 ? ReadNext(tfhd, ref pos, "tfhd") : _fragmentDefaults.Duration;
            uint defaultSize = (flags & 0x10) != 0 ? ReadNext(tfhd, ref pos, "tfhd") : _fragmentDefaults.Size;
            uint defaultFlags = (flags & 0x20) != 0 ? ReadNext(tfhd, ref pos, "tfhd") : _fragmentDefaults.Flags;

            long decodeTime = -1;
            if (FindChild(traf, Box.Tfdt, out var tfdt))
            {
                decodeTime = Require(tfdt, 8, "tfdt")[0] == 1
                    ? checked((long)BinaryPrimitives.ReadUInt64BigEndian(Require(tfdt, 12, "tfdt")[4..]))
                    : ReadUInt32(tfdt, 4);
            }

            // Each 'trun' (8.8.8) starts at its data offset from the base, or right after the previous one.
            long dataOffset = baseOffset;
            var boxes = traf;
            while (NextBox(ref boxes, out uint type, out var trun))
            {
                if (type != Box.Trun)
                {
                    continue;
                }

                uint trunFlags = ReadUInt32(Require(trun, 8, "trun"), 0) & 0xFFFFFF;
                uint count = ReadUInt32(trun, 4);
                if (_fragmentSamples.Count + (long)count > MaxSamples)
                {
                    throw new NotSupportedException("Track has more samples than supported.");
                }

                int p = 8;
                if ((trunFlags & 0x01) != 0)
                {
                    dataOffset = baseOffset + BinaryPrimitives.ReadInt32BigEndian(Require(trun, p + 4, "trun")[p..]);
                    p += 4;
                }

                uint? firstFlags = null;
                if ((trunFlags & 0x04) != 0)
                {
                    firstFlags = ReadUInt32(Require(trun, p + 4, "trun"), p);
                    p += 4;
                }

                for (uint i = 0; i < count; i++)
                {
                    uint duration = (trunFlags & 0x100) != 0 ? ReadNext(trun, ref p, "trun") : defaultDuration;
                    uint size = (trunFlags & 0x200) != 0 ? ReadNext(trun, ref p, "trun") : defaultSize;
                    uint sampleFlags = (trunFlags & 0x400) != 0 ? ReadNext(trun, ref p, "trun") : i == 0 && firstFlags is { } first ? first : defaultFlags;

                    // Signed in version 1; read as signed in version 0 too, as for 'ctts'.
                    int compositionOffset = (trunFlags & 0x800) != 0 ? (int)ReadNext(trun, ref p, "trun") : 0;

                    bool isKeyframe = _isAudio || (sampleFlags & 0x10000) == 0; // sample_is_non_sync_sample
                    _fragmentSamples.Add((dataOffset, SampleSize(size), i == 0 ? decodeTime : -1, SampleDuration(duration), compositionOffset, isKeyframe));
                    dataOffset += size;
                }

                decodeTime = -1;
            }

            return dataOffset;
        }

        /// <summary>The 32-bit field at <paramref name="pos"/>, which moves past it.</summary>
        private static uint ReadNext(ReadOnlySpan<byte> box, ref int pos, string name)
        {
            uint value = ReadUInt32(Require(box, pos + 4, name), pos);
            pos += 4;
            return value;
        }

        public Sample[] BuildSamples(uint movieTimescale)
        {
            if (SampleCount > MaxSamples)
            {
                throw new NotSupportedException($"Track has {SampleCount} samples, more than supported.");
            }

            int count = (int)SampleCount;
            var sizes = ReadSizes(count);
            var offsets = new long[count];
            count = AssignOffsets(sizes, offsets);

            var dts = new long[count];
            var durations = new int[count];
            ReadTimes(dts, durations);

            var keyframes = ReadKeyframes(count);
            var compositionOffsets = ReadCompositionOffsets(count);

            // Negative composition offsets ('ctts' or 'trun' version 1): like FFmpeg, every pts moves
            // forward by the largest of them, so that no pts comes before its dts.
            int ptsShift = 0;
            foreach (int offset in compositionOffsets)
            {
                ptsShift = Math.Max(ptsShift, -offset);
            }

            foreach (var fragmentSample in _fragmentSamples)
            {
                ptsShift = Math.Max(ptsShift, -fragmentSample.CompositionOffset);
            }

            // The samples in media time, in decoding order.
            var media = new Sample[count + _fragmentSamples.Count];
            for (int i = 0; i < count; i++)
            {
                media[i] = new Sample(StreamIndex, offsets[i], sizes[i], dts[i], dts[i] + compositionOffsets[i] + ptsShift, durations[i], keyframes[i]);
            }

            // Fragment samples continue from the last sample unless 'tfdt' says where they start.
            long next = count > 0 ? dts[count - 1] + durations[count - 1] : 0;
            for (int i = 0; i < _fragmentSamples.Count; i++)
            {
                var (offset, size, decodeTime, duration, compositionOffset, isKeyframe) = _fragmentSamples[i];
                long time = decodeTime >= 0 ? decodeTime : next;
                media[count + i] = new Sample(StreamIndex, offset, size, time, time + compositionOffset + ptsShift, duration, isKeyframe);
                next = time + duration;
            }

            var edits = Edits(movieTimescale);
            if (edits.Count == 0)
            {
                return media;
            }

            // FFmpeg keeps an index entry for every sample of its edited track, composition offsets included, in
            // fragmented files (see Mp4ConformanceTests): that decides where a video edit stops.
            bool hasCompositionOffsets = _ctts.Length > 0 || _fragmentSamples.Count > 0;
            var samples = new List<Sample>(media.Length);
            for (int e = 0; e < edits.Count; e++)
            {
                if (samples.Count > MaxSamples)
                {
                    throw new NotSupportedException($"The edit list makes more than {MaxSamples} packets.");
                }

                if (_isAudio)
                {
                    AddAudioEdit(media, edits[e], first: e == 0, samples);
                }
                else
                {
                    AddVideoEdit(media, edits[e], hasCompositionOffsets, samples);
                }
            }

            // Audio durations as FFmpeg gives them: up to the next packet's dts where that comes later, else the sample's
            // own; the last packet's up to the end of the edits (shorter or longer than the sample's). Video keeps its own.
            var last = edits[^1];
            long end = last.Duration > 0 ? last.Start + last.Duration : long.MinValue;
            for (int i = 0; _isAudio && i < samples.Count; i++)
            {
                long until = i + 1 < samples.Count ? samples[i + 1].Dts - samples[i].Dts : end - samples[i].Pts;
                if (until > 0 && until <= int.MaxValue)
                {
                    samples[i] = samples[i] with { Duration = (int)until };
                }
            }

            return [.. samples];
        }

        /// <summary>
        /// An edit of a video track as FFmpeg reads it: from the keyframe at or before the first sample shown in the
        /// edit, through the edit's end and on to a keyframe after it (with composition offsets, the second one: B frames
        /// after the first can still belong to the edit). Samples shown outside the edit are kept to be decoded, marked
        /// <see cref="Sample.Discard"/>. Times move so that the edit's media time comes at its start in the track.
        /// </summary>
        private static void AddVideoEdit(Sample[] media, Edit edit, bool hasCompositionOffsets, List<Sample> samples)
        {
            long end = edit.Duration > 0 ? edit.MediaTime + edit.Duration : long.MaxValue;
            int shown = Array.FindIndex(media, sample => sample.Pts >= edit.MediaTime && sample.Pts < end);
            if (shown < 0)
            {
                return;
            }

            int start = Array.FindLastIndex(media, shown, sample => sample.IsKeyframe);
            long shift = edit.Start - edit.MediaTime;
            bool keyframeAfterEnd = false;
            for (int i = Math.Max(start, 0); i < media.Length; i++)
            {
                var sample = media[i];
                bool inEdit = sample.Pts >= edit.MediaTime && sample.Pts < end;
                samples.Add(sample with { Dts = sample.Dts + shift, Pts = sample.Pts + shift, Discard = !inEdit });
                if (edit.Duration > 0 && sample.IsKeyframe && sample.Pts + sample.Duration >= end)
                {
                    if (hasCompositionOffsets && !keyframeAfterEnd)
                    {
                        keyframeAfterEnd = true;
                        continue;
                    }

                    break;
                }
            }
        }

        /// <summary>
        /// An edit of an audio track as FFmpeg reads it: a second of samples before the edit's media time to be decoded
        /// and not shown (<see cref="Sample.Discard"/>), then the samples from the edit's media time through the one its
        /// end falls in, which plays whole. The first edit starts in the sample that holds its media time, whose samples
        /// before it are skipped (encoder priming, <see cref="Packet.SkipSamples"/>); a later one at the first sample
        /// from its media time on (so up to a sample late), which comes at the edit's start in the track.
        /// </summary>
        private void AddAudioEdit(Sample[] media, Edit edit, bool first, List<Sample> samples)
        {
            int Containing(long time) => Array.FindIndex(media, sample => time < sample.Dts + sample.Duration);

            int shown = first ? Containing(edit.MediaTime) : Array.FindIndex(media, sample => sample.Dts >= edit.MediaTime);
            if (shown < 0)
            {
                return;
            }

            int start = Math.Max(Containing(edit.MediaTime - Timescale), 0);
            long end = edit.Duration > 0 ? edit.MediaTime + edit.Duration : long.MaxValue;
            long origin = first ? edit.MediaTime : media[shown].Dts;
            for (int i = start; i < media.Length; i++)
            {
                var sample = media[i];
                long time = edit.Start + sample.Dts - origin;
                long skip = first && i == shown ? Math.Clamp(edit.MediaTime - sample.Dts, 0, sample.Duration) : 0;
                samples.Add(sample with
                {
                    Dts = time,
                    Pts = time,
                    Discard = i < shown,
                    SkipSamples = checked((int)(SampleRate > 0 && SampleRate != Timescale ? skip * SampleRate / Timescale : skip)),
                });
                if (i >= shown && sample.Dts + sample.Duration >= end)
                {
                    break;
                }
            }
        }

        /// <summary>A sample's size in bytes; past <see cref="int.MaxValue"/> the file is broken (no sample is 2 GB).</summary>
        private static int SampleSize(uint size) =>
            size <= int.MaxValue ? (int)size : throw new InvalidDataException($"Sample size {size} is too large.");

        private static int SampleDuration(uint duration) =>
            duration <= int.MaxValue ? (int)duration : throw new InvalidDataException($"Sample duration {duration} is too large.");

        private int[] ReadSizes(int count)
        {
            var sizes = new int[count];
            if (_stsz.Length >= 12)
            {
                uint constant = ReadUInt32(_stsz, 4);
                if (constant != 0)
                {
                    Array.Fill(sizes, SampleSize(constant));
                    return sizes;
                }

                Require(_stsz, 12 + (4 * count), "stsz");
                for (int i = 0; i < count; i++)
                {
                    sizes[i] = SampleSize(ReadUInt32(_stsz, 12 + (4 * i)));
                }

                return sizes;
            }

            if (_stz2.Length >= 12)
            {
                int fieldSize = _stz2[7];
                if (fieldSize is not (4 or 8 or 16))
                {
                    throw new InvalidDataException($"Invalid 'stz2' field size {fieldSize}.");
                }

                Require(_stz2, 12 + (((count * fieldSize) + 7) / 8), "stz2");
                var entries = _stz2.AsSpan(12);
                for (int i = 0; i < count; i++)
                {
                    sizes[i] = fieldSize switch
                    {
                        4 => (i & 1) == 0 ? entries[i / 2] >> 4 : entries[i / 2] & 0x0F,
                        8 => entries[i],
                        _ => BinaryPrimitives.ReadUInt16BigEndian(entries[(2 * i)..]),
                    };
                }

                return sizes;
            }

            throw new InvalidDataException("Track has no sample size table.");
        }

        /// <summary>
        /// Walks the chunks (stsc + stco/co64) to give every sample its file offset.
        /// Returns how many samples the chunks actually hold, which can be fewer than declared.
        /// </summary>
        private int AssignOffsets(int[] sizes, long[] offsets)
        {
            bool wide = _co64.Length > 0;
            var chunkTable = Entries(wide ? _co64 : _stco, wide ? 8 : 4, wide ? "co64" : "stco");
            int chunkCount = chunkTable.Length / (wide ? 8 : 4);
            var stsc = Entries(_stsc, 12, "stsc");
            int entryCount = stsc.Length / 12;

            int sample = 0;
            for (int e = 0; e < entryCount && sample < sizes.Length; e++)
            {
                long firstChunk = ReadUInt32(stsc, e * 12);
                long perChunk = ReadUInt32(stsc, (e * 12) + 4);
                long nextFirst = e + 1 < entryCount ? ReadUInt32(stsc, (e + 1) * 12) : chunkCount + 1L;

                for (long chunk = Math.Max(firstChunk, 1); chunk < nextFirst && chunk <= chunkCount && sample < sizes.Length; chunk++)
                {
                    int index = (int)(chunk - 1);
                    long offset = wide
                        ? checked((long)BinaryPrimitives.ReadUInt64BigEndian(chunkTable[(index * 8)..]))
                        : BinaryPrimitives.ReadUInt32BigEndian(chunkTable[(index * 4)..]);

                    for (long s = 0; s < perChunk && sample < sizes.Length; s++)
                    {
                        offsets[sample] = offset;
                        offset += sizes[sample];
                        sample++;
                    }
                }
            }

            return sample;
        }

        private void ReadTimes(long[] dts, int[] durations)
        {
            var stts = Entries(_stts, 8, "stts");
            long time = 0;
            int sample = 0;
            for (int e = 0; e < stts.Length / 8 && sample < dts.Length; e++)
            {
                uint count = ReadUInt32(stts, e * 8);
                int delta = SampleDuration(ReadUInt32(stts, (e * 8) + 4));
                for (uint n = 0; n < count && sample < dts.Length; n++)
                {
                    dts[sample] = time;
                    durations[sample] = delta;
                    time += delta;
                    sample++;
                }
            }

            // Samples the time table does not cover keep counting from the last time.
            for (; sample < dts.Length; sample++)
            {
                dts[sample] = time;
            }
        }

        /// <summary>pts - dts per sample from 'ctts'; zero where the table is absent or ends early.</summary>
        private int[] ReadCompositionOffsets(int count)
        {
            var offsets = new int[count];
            var ctts = Entries(_ctts, 8, "ctts");
            int sample = 0;
            for (int e = 0; e < ctts.Length / 8 && sample < count; e++)
            {
                uint runLength = ReadUInt32(ctts, e * 8);

                // Signed in version 1; FFmpeg reads version 0 offsets as signed too.
                int offset = (int)ReadUInt32(ctts, (e * 8) + 4);
                for (uint n = 0; n < runLength && sample < count; n++)
                {
                    offsets[sample++] = offset;
                }
            }

            return offsets;
        }

        private bool[] ReadKeyframes(int count)
        {
            var keyframes = new bool[count];
            if (_stss is null)
            {
                Array.Fill(keyframes, true); // no sync sample table: every sample is a sync sample
                return keyframes;
            }

            var stss = Entries(_stss, 4, "stss");
            for (int e = 0; e < stss.Length / 4; e++)
            {
                uint number = ReadUInt32(stss, e * 4);
                if (number >= 1 && number <= count)
                {
                    keyframes[number - 1] = true;
                }
            }

            return keyframes;
        }

        /// <summary>An edit: where it starts in the track and its length (in the track's timescale), and its media time.</summary>
        private readonly record struct Edit(long Start, long MediaTime, long Duration);

        /// <summary>
        /// The edit list's edits as FFmpeg plays them, durations converted from the movie timescale rounded to nearest (0:
        /// to the end of the media). Empty edits before the first real one delay it; an empty edit after it plays the
        /// media from its start for its duration (FFmpeg reads its media time, -1, as the start). None without an edit
        /// list or a real edit in it.
        /// </summary>
        private List<Edit> Edits(uint movieTimescale)
        {
            var edits = new List<Edit>();
            if (_elst is null || _elst.Length < 8 || movieTimescale == 0)
            {
                return edits;
            }

            bool v1 = _elst[0] == 1;
            int entrySize = v1 ? 20 : 12;
            var entries = Entries(_elst, entrySize, "elst");
            if (entries.Length / entrySize > MaxEdits)
            {
                throw new NotSupportedException($"Edit lists of more than {MaxEdits} edits are not supported.");
            }

            long empty = 0;
            long start = 0;
            for (int e = 0; e < entries.Length / entrySize; e++)
            {
                var entry = entries[(e * entrySize)..];
                long duration = v1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(entry) : BinaryPrimitives.ReadUInt32BigEndian(entry);
                long mediaTime = v1 ? BinaryPrimitives.ReadInt64BigEndian(entry[8..]) : BinaryPrimitives.ReadInt32BigEndian(entry[4..]);
                if (mediaTime == -1 && edits.Count == 0)
                {
                    empty += duration;
                    continue;
                }

                if (edits.Count == 0)
                {
                    start = (long)((Int128)empty * Timescale / movieTimescale);
                }

                long length = (long)((((Int128)duration * Timescale) + (movieTimescale / 2)) / movieTimescale);
                edits.Add(new Edit(start, mediaTime, length));
                start += length;
            }

            return edits;
        }

        private void ParseSampleDescription(ReadOnlySpan<byte> stsd)
        {
            // FullBox header and entry_count, then the sample entries; the first one describes the track.
            var entries = Require(stsd, 8, "stsd")[8..];
            if (!NextBox(ref entries, out uint format, out var entry))
            {
                throw new InvalidDataException("The 'stsd' box has no sample entry.");
            }

            Codec = Box.Name(format);
            if (_isAudio)
            {
                ParseAudioSampleEntry(format, entry);
                return;
            }

            // VisualSampleEntry: SampleEntry (8), pre_defined/reserved (16), width, height at 24,
            // then fields up to 78 bytes, then child boxes.
            Require(entry, 78, Box.Name(format));
            Width = BinaryPrimitives.ReadUInt16BigEndian(entry[24..]);
            Height = BinaryPrimitives.ReadUInt16BigEndian(entry[26..]);
            Codec = Box.Name(format);

            var children = entry[78..];
            switch (format)
            {
                case Box.Avc1 or Box.Avc3:
                    Codec = CodecIds.H264;
                    if (FindChild(children, Box.AvcC, out var avcC))
                    {
                        CodecPrivate = avcC.ToArray();
                    }

                    break;

                case Box.Mp4v:
                    if (FindChild(children, Box.Esds, out var esds))
                    {
                        ParseEsds(esds);
                    }

                    break;

                case Box.Jpeg or Box.Mjpa:
                    Codec = CodecIds.Mjpeg;
                    break;
            }
        }

        /// <summary>
        /// AudioSampleEntry (14496-12 12.2.3): SampleEntry (8), version and reserved fields,
        /// channelcount at 16, samplesize, two reserved shorts, 16.16 samplerate at 24, then child
        /// boxes after 28 bytes; QuickTime sound descriptions version 1 and 2 add 16 or 36 bytes.
        /// The AudioSpecificConfig, when there is one, overrides rate and channel count.
        /// </summary>
        private void ParseAudioSampleEntry(uint format, ReadOnlySpan<byte> entry)
        {
            Require(entry, 28, Box.Name(format));
            int version = BinaryPrimitives.ReadUInt16BigEndian(entry[8..]);
            Channels = BinaryPrimitives.ReadUInt16BigEndian(entry[16..]);
            SampleRate = (int)(BinaryPrimitives.ReadUInt32BigEndian(entry[24..]) >> 16);
            int childrenStart = version switch
            {
                1 => 28 + 16,
                2 => 28 + 36,
                _ => 28,
            };

            if (format == Box.Mp4a && entry.Length > childrenStart && FindChild(entry[childrenStart..], Box.Esds, out var esds))
            {
                ParseEsds(esds);
                if (Codec == CodecIds.Aac && TryReadAudioSpecificConfig(CodecPrivate.Span, out int rate, out int channels))
                {
                    SampleRate = rate;
                    Channels = channels > 0 ? channels : Channels; // 0: the channels are in a program_config_element
                }
            }
        }

        /// <summary>
        /// The start of an AudioSpecificConfig (14496-3 1.6.2.1): audioObjectType, then the sampling
        /// frequency index (or an explicit 24-bit rate) and channelConfiguration.
        /// </summary>
        private static bool TryReadAudioSpecificConfig(ReadOnlySpan<byte> config, out int sampleRate, out int channels)
        {
            sampleRate = 0;
            channels = 0;
            if (config.Length < 2)
            {
                return false;
            }

            int bit = 0;
            int Read(int count, ReadOnlySpan<byte> data)
            {
                int value = 0;
                for (int i = 0; i < count; i++, bit++)
                {
                    int b = bit >> 3 < data.Length ? (data[bit >> 3] >> (7 - (bit & 7))) & 1 : 0;
                    value = (value << 1) | b;
                }

                return value;
            }

            if (Read(5, config) == 31)
            {
                Read(6, config); // audioObjectTypeExt
            }

            int index = Read(4, config);
            ReadOnlySpan<int> rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
            if (index == 15)
            {
                sampleRate = Read(24, config);
            }
            else if (index < rates.Length)
            {
                sampleRate = rates[index];
            }
            else
            {
                return false;
            }

            int configuration = Read(4, config);
            channels = configuration switch
            {
                >= 1 and <= 6 => configuration,
                7 => 8,
                _ => 0,
            };

            return sampleRate > 0;
        }

        /// <summary>
        /// Reads the MPEG-4 descriptors in 'esds' (ISO/IEC 14496-1 7.2.6): the object type tells
        /// MJPEG apart from MPEG-4 Visual, and the decoder specific info is the codec private data.
        /// </summary>
        private void ParseEsds(ReadOnlySpan<byte> esds)
        {
            var data = Require(esds, 4, "esds")[4..]; // FullBox header
            if (!ReadDescriptor(ref data, out int tag, out var es) || tag != 0x03 || es.Length < 3)
            {
                return;
            }

            int flags = es[2];
            int pos = 3;
            if ((flags & 0x80) != 0)
            {
                pos += 2; // dependsOn_ES_ID
            }

            if ((flags & 0x40) != 0 && pos < es.Length)
            {
                pos += 1 + es[pos]; // URL
            }

            if ((flags & 0x20) != 0)
            {
                pos += 2; // OCR_ES_ID
            }

            if (pos > es.Length)
            {
                return;
            }

            var inner = es[pos..];
            while (ReadDescriptor(ref inner, out tag, out var config))
            {
                if (tag != 0x04 || config.Length < 13)
                {
                    continue;
                }

                Codec = config[0] switch
                {
                    0x6C => CodecIds.Mjpeg,       // ISO/IEC 10918-1 (JPEG)
                    0x20 => CodecIds.Mpeg4Visual, // ISO/IEC 14496-2
                    0x21 => CodecIds.H264,
                    0x40 => CodecIds.Aac,             // ISO/IEC 14496-3 (MPEG-4 audio)
                    0x66 or 0x67 or 0x68 => CodecIds.Aac, // ISO/IEC 13818-7 (MPEG-2 AAC) Main, LC, SSR
                    _ => Codec,
                };

                var specific = config[13..];
                while (ReadDescriptor(ref specific, out tag, out var info))
                {
                    if (tag == 0x05)
                    {
                        CodecPrivate = info.ToArray();
                        break;
                    }
                }

                return;
            }
        }

        /// <summary>Reads a descriptor: a tag byte, then a length in 7-bit groups (high bit = more).</summary>
        private static bool ReadDescriptor(ref ReadOnlySpan<byte> data, out int tag, out ReadOnlySpan<byte> content)
        {
            tag = 0;
            content = default;
            if (data.Length < 2)
            {
                return false;
            }

            tag = data[0];
            int length = 0;
            int pos = 1;
            for (int i = 0; i < 4 && pos < data.Length; i++)
            {
                byte b = data[pos++];
                length = (length << 7) | (b & 0x7F);
                if ((b & 0x80) == 0)
                {
                    break;
                }
            }

            if (pos + length > data.Length)
            {
                return false;
            }

            content = data.Slice(pos, length);
            data = data[(pos + length)..];
            return true;
        }

        /// <summary>The entries of a FullBox table: header and count checked, entries returned.</summary>
        private static ReadOnlySpan<byte> Entries(byte[] box, int entrySize, string name)
        {
            if (box.Length == 0)
            {
                return default;
            }

            Require(box, 8, name);
            long count = ReadUInt32(box, 4);
            if (8 + (count * entrySize) > box.Length)
            {
                throw new InvalidDataException($"The '{name}' box is shorter than its entry count.");
            }

            return box.AsSpan(8, (int)(count * entrySize));
        }
    }

    /// <summary>Box types (FourCCs) as big-endian integers.</summary>
    private static class Box
    {
        public const uint Moov = 0x6D6F6F76;
        public const uint Mvhd = 0x6D766864;
        public const uint Mvex = 0x6D766578;
        public const uint Trex = 0x74726578;
        public const uint Moof = 0x6D6F6F66;
        public const uint Traf = 0x74726166;
        public const uint Tfhd = 0x74666864;
        public const uint Tfdt = 0x74666474;
        public const uint Trun = 0x7472756E;
        public const uint Tkhd = 0x746B6864;
        public const uint Cmov = 0x636D6F76;
        public const uint Trak = 0x7472616B;
        public const uint Edts = 0x65647473;
        public const uint Elst = 0x656C7374;
        public const uint Mdia = 0x6D646961;
        public const uint Mdhd = 0x6D646864;
        public const uint Hdlr = 0x68646C72;
        public const uint Minf = 0x6D696E66;
        public const uint Stbl = 0x7374626C;
        public const uint Stsd = 0x73747364;
        public const uint Stsz = 0x7374737A;
        public const uint Stz2 = 0x73747A32;
        public const uint Stco = 0x7374636F;
        public const uint Co64 = 0x636F3634;
        public const uint Stsc = 0x73747363;
        public const uint Stts = 0x73747473;
        public const uint Ctts = 0x63747473;
        public const uint Stss = 0x73747373;
        public const uint Vide = 0x76696465;
        public const uint Soun = 0x736F756E;
        public const uint Mp4a = 0x6D703461;
        public const uint Avc1 = 0x61766331;
        public const uint Avc3 = 0x61766333;
        public const uint AvcC = 0x61766343;
        public const uint Mp4v = 0x6D703476;
        public const uint Esds = 0x65736473;
        public const uint Jpeg = 0x6A706567;
        public const uint Mjpa = 0x6D6A7061;

        public static string Name(uint type)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, type);
            return Encoding.Latin1.GetString(bytes);
        }
    }
}

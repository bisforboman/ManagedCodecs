using System.Globalization;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// Demuxes every samples/*.mxf and compares each packet of its video and its audio streams (the first alone, as read
/// unasked, then each alone, all, and all named backwards: <see cref="MxfDemuxer.SelectAudio"/>) with FFmpeg's: pts, size,
/// MD5, keyframe flag and duration with samples/*.packets.csv (<c>ffprobe -show_packets -show_data_hash MD5</c>), the
/// decode times with *.packets.nofillin.csv (FFmpeg's MXF demuxer's own, before libavformat moves MPEG-2's back a frame;
/// audio has none there, and its dts is its pts), and the streams' facts with the header metadata FFmpeg reads.
/// </summary>
public class MxfConformanceTests
{
    public static TheoryData<string> Samples() =>
        new(TestSamples.Directory is null
            ? Enumerable.Empty<string>()
            : Directory.EnumerateFiles(TestSamples.Directory, "*.mxf").Order()
                .Where(f => File.Exists(Path.ChangeExtension(f, ".packets.csv")))
                .Select(f => Path.GetFileName(f)));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegPacketByPacket(string sampleName)
    {
        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var mxf = Assert.IsType<MxfDemuxer>(demuxer);
        var tracks = mxf.AudioTracks.Select(t => t.StreamIndex).ToArray();
        Assert.Equal(tracks.FirstOrDefault(-1), mxf.Audio?.StreamIndex ?? -1);

        // The first track alone, before any choice; then each alone, all of them, and all named backwards (the packets
        // stay in file order, Audio is the first named).
        CompareWithFfmpeg(mxf, sampleName, tracks.Take(1).ToArray());
        if (tracks.Length > 1)
        {
            foreach (int[] selection in tracks.Select(t => new[] { t }).Append(tracks).Append([.. tracks.Reverse()]))
            {
                mxf.SelectAudio(selection);
                CompareWithFfmpeg(mxf, sampleName, selection);
            }
        }
    }

    private static void CompareWithFfmpeg(MxfDemuxer mxf, string sampleName, int[] selected)
    {
        Assert.Equal(selected.FirstOrDefault(-1), mxf.Audio?.StreamIndex ?? -1);
        var streams = selected.Append(mxf.Video?.StreamIndex ?? -1).ToHashSet();

        var expected = Parse(TestSamples.ReferenceOf(sampleName, ".packets.csv")).Where(p => streams.Contains(p.StreamIndex)).ToList();
        var own = Parse(TestSamples.ReferenceOf(sampleName, ".packets.nofillin.csv")).Where(p => streams.Contains(p.StreamIndex)).ToList();

        // Where libavformat fills in MPEG-2 times with B pictures it can't place (an I or P picture's pts unknown: a file
        // cut before its index, mxf-mpeg2-b-cut), its decode times are the edit units; without B pictures its parser
        // delays them a frame.
        bool unplaced = expected.Any(e => e.StreamIndex == mxf.Video?.StreamIndex && e.Pts is null && e.Dts is not null);
        var packets = mxf.ReadPackets().ToList();

        Assert.Equal(expected.Select(p => p.StreamIndex), packets.Select(p => p.StreamIndex)); // file order
        Assert.Equal(mxf.ReadPacketInfo(), packets.Select(Info));
        var sequences = new Dictionary<int, long>();
        for (int i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var p = packets[i];
            string at = $"packet {i} (stream {p.StreamIndex})";

            // Without an index table it reads (mxf-h264-pipe-norip: the footer's, which it doesn't reach), FFmpeg gives
            // video no times at all; ours come from the edit units, the index's found by walking to the footer.
            Assert.True((e.Pts ?? p.Pts) == p.Pts, $"{at}: pts {p.Pts}, FFmpeg's {e.Pts}");
            Assert.True(e.Size == p.Data.Length, $"{at}: size {p.Data.Length}, FFmpeg's {e.Size}");
            Assert.True(e.Md5 == FrameMd5.Hash(p.Data.Span), $"{at}: other data than FFmpeg's");

            // An open GOP's I picture has no random access flag in the index; it is an MPEG-2 keyframe all the same, as
            // libavformat's parser marks it (mxf-mpeg2-b: every I picture, by the entries' prediction flags).
            Assert.True(e.IsKeyframe == p.IsKeyframe, $"{at}: keyframe {p.IsKeyframe}, FFmpeg's {e.IsKeyframe}");
            Assert.True(e.Duration == p.Duration, $"{at}: duration {p.Duration}, FFmpeg's {e.Duration}");

            // FFmpeg's demuxer's own decode times; where it gives none (audio; DNxHR's and D-10's constant-size index,
            // which has no entries; a file cut before its index) the times libavformat fills in, which count edit units
            // too, MPEG-2's a frame early (its parser's reorder delay, as mxf-mpeg2's own times show: D-10's start at -1)
            // unless it found B pictures it couldn't place (above).
            long? filled = own[i].Dts is null && e.Dts is { } d && p.StreamIndex == mxf.Video?.StreamIndex && mxf.Video.Codec == "mpeg2video" && !unplaced ? d + 1 : e.Dts;
            Assert.True((own[i].Dts ?? filled ?? p.Dts) == p.Dts, $"{at}: dts {p.Dts}, FFmpeg's demuxer's {own[i].Dts}, filled in {e.Dts}");

            long sequence = sequences.GetValueOrDefault(p.StreamIndex);
            Assert.Equal(sequence, p.Sequence);
            sequences[p.StreamIndex] = sequence + 1;
        }

        // Each stream's duration is its header's track duration, as ffprobe's (name.headers.json's duration_ts: the
        // sound's rescaled to samples), longer than the packets in a file cut short; where the header gives none (a
        // file written to a pipe), the picture's is 0 and the sound's runs to its last packet's end.
        var durations = HeaderDurations(sampleName);
        if (mxf.Video is { } video)
        {
            Assert.Equal(sequences[video.StreamIndex], video.FrameCount);
            Assert.Equal(durations.GetValueOrDefault(video.StreamIndex) ?? 0, video.Duration);
        }

        foreach (var audio in mxf.AudioTracks.Where(t => selected.Contains(t.StreamIndex)))
        {
            Assert.Equal(sequences[audio.StreamIndex], audio.FrameCount);
            Assert.Equal(new Rational(1, audio.SampleRate), audio.TimeBase);
            var last = packets.Last(p => p.StreamIndex == audio.StreamIndex);
            Assert.Equal(durations.GetValueOrDefault(audio.StreamIndex) ?? (last.Pts + last.Duration), audio.Duration);
        }
    }

    // ffprobe's duration_ts for each stream, from the header metadata (name.headers.json, -nofind_stream_info); null
    // where it gives none.
    private static Dictionary<int, long?> HeaderDurations(string sampleName)
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(TestSamples.ReferenceOf(sampleName, ".headers.json")));
        return json.RootElement.GetProperty("streams").EnumerateArray().ToDictionary(
            s => s.GetProperty("index").GetInt32(),
            s => s.TryGetProperty("duration_ts", out var d) ? d.GetInt64() : (long?)null);
    }

    /// <summary>
    /// mxf-h264-mono4 is what it is for: four mono sound tracks, a stream each as ffprobe numbers them (name.headers.json),
    /// each with packets of its own; and mxf-prores-pcm16's two stereo tracks.
    /// </summary>
    [Fact]
    public void ListsEverySoundTrack()
    {
        using var mono = MxfDemuxer.Open(TestSamples.PathOf("mxf-h264-mono4.mxf"));
        Assert.Equal([1, 2, 3, 4], mono.AudioTracks.Select(t => t.StreamIndex));
        Assert.All(mono.AudioTracks, t => Assert.Equal((CodecIds.PcmS24Le, 48000, 1, 25L, 48000L), (t.Codec, t.SampleRate, t.Channels, t.FrameCount, t.Duration)));
        Assert.Same(mono.AudioTracks[0], mono.Audio);
        Assert.Equal(FfprobeAudioStreams("mxf-h264-mono4.mxf"), mono.AudioTracks.Select(t => t.StreamIndex));

        mono.SelectAudio(1, 2, 3, 4);
        var first = mono.ReadPackets().Where(p => p.StreamIndex != mono.Video!.StreamIndex).Take(4).ToList();
        Assert.Equal([1, 2, 3, 4], first.Select(p => p.StreamIndex));
        Assert.Equal(4, first.Select(p => FrameMd5.Hash(p.Data.Span)).Distinct().Count());

        using var stereo = MxfDemuxer.Open(TestSamples.PathOf("mxf-prores-pcm16.mxf"));
        Assert.Equal([(1, 2), (2, 2)], stereo.AudioTracks.Select(t => (t.StreamIndex, t.Channels)));
        Assert.Equal(FfprobeAudioStreams("mxf-prores-pcm16.mxf"), stereo.AudioTracks.Select(t => t.StreamIndex));

        // One track, or none: the list says so.
        using var one = MxfDemuxer.Open(TestSamples.PathOf("mxf-h264-pcm.mxf"));
        Assert.Same(one.Audio, Assert.Single(one.AudioTracks));
        using var none = MxfDemuxer.Open(TestSamples.PathOf("mxf-mpeg2.mxf"));
        Assert.Empty(none.AudioTracks);
        Assert.Throws<ArgumentOutOfRangeException>(() => none.SelectAudio(0));
    }

    private static IEnumerable<int> FfprobeAudioStreams(string sampleName)
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(TestSamples.ReferenceOf(sampleName, ".headers.json")));
        return [.. json.RootElement.GetProperty("streams").EnumerateArray()
            .Where(s => s.GetProperty("codec_type").GetString() == "audio")
            .Select(s => s.GetProperty("index").GetInt32())];
    }

    /// <summary>
    /// <see cref="MxfDemuxer.SelectAudio"/>: <see cref="MxfDemuxer.Audio"/> is the first track named, a read already
    /// started keeps its tracks, a choice that is refused changes nothing, and a seek moves every track, those chosen
    /// after it too.
    /// </summary>
    [Fact]
    public void SelectsSoundTracks()
    {
        using var mxf = MxfDemuxer.Open(TestSamples.PathOf("mxf-h264-mono4.mxf"));
        var video = mxf.Video!;
        using var started = mxf.ReadPackets().GetEnumerator();
        Assert.True(started.MoveNext());

        mxf.SelectAudio(3, 2);
        Assert.Same(mxf.AudioTracks[2], mxf.Audio);
        Assert.Equal([0, 2, 3], mxf.ReadPackets().Select(p => p.StreamIndex).Distinct().Order());
        Assert.Equal([0, 2, 3], mxf.ReadPacketInfo().Select(p => p.StreamIndex).Distinct().Order());
        var rest = new List<int>();
        while (started.MoveNext())
        {
            rest.Add(started.Current.StreamIndex);
        }

        Assert.Equal([0, 1], rest.Distinct().Order());

        Assert.Throws<ArgumentException>(() => mxf.SelectAudio());
        Assert.Throws<ArgumentException>(() => mxf.SelectAudio(1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => mxf.SelectAudio(1, video.StreamIndex));
        Assert.Throws<ArgumentOutOfRangeException>(() => mxf.SelectAudio(5));
        Assert.Throws<ArgumentNullException>(() => mxf.SelectAudio(null!));
        Assert.Equal(3, mxf.Audio!.StreamIndex);
        Assert.Equal([0, 2, 3], mxf.ReadPackets().Select(p => p.StreamIndex).Distinct().Order());

        // Half a second in, by a track that is not selected: every track starts at its packet there (a 25th of a second
        // each, so the 13th), the video at the keyframe before (-g 5: the 11th picture).
        mxf.Seek(24000, 4);
        mxf.SelectAudio(1, 4);
        var after = mxf.ReadPackets().ToList();
        Assert.Equal(10, after.First(p => p.StreamIndex == video.StreamIndex).Sequence);
        Assert.Equal([(1, 12L, 23040L), (4, 12L, 23040L)], after.Where(p => p.StreamIndex != video.StreamIndex).Take(2).Select(p => (p.StreamIndex, p.Sequence, p.Pts)));
        Assert.Throws<ArgumentOutOfRangeException>(() => mxf.Seek(0, 5));
    }

    /// <summary>
    /// <see cref="IDemuxer"/>'s defaults, for a demuxer that reads one audio stream: the list is that stream, and only it
    /// can be selected.
    /// </summary>
    [Fact]
    public void ADemuxerOfOneAudioStreamListsAndSelectsIt()
    {
        IDemuxer one = new OneStream(new AudioStreamInfo(CodecIds.PcmS16Le, 48000, 2, 0, new Rational(1, 48000), default, 1));
        Assert.Same(one.Audio, Assert.Single(one.AudioTracks));
        one.SelectAudio(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => one.SelectAudio(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => one.SelectAudio(1, 2));
        Assert.Throws<ArgumentException>(() => one.SelectAudio(1, 1));
        Assert.Throws<ArgumentException>(() => one.SelectAudio());
        Assert.Throws<ArgumentNullException>(() => one.SelectAudio(null!));

        IDemuxer none = new OneStream(null);
        Assert.Empty(none.AudioTracks);
        Assert.Throws<ArgumentOutOfRangeException>(() => none.SelectAudio(0));

        // The library's other demuxers still read their first audio stream alone.
        using var mp4 = Demuxer.Open(TestSamples.PathOf("h264-with-audio.mp4"));
        Assert.Same(mp4.Audio, Assert.Single(mp4.AudioTracks));
        mp4.SelectAudio(mp4.Audio!.StreamIndex);
    }

    private sealed class OneStream(AudioStreamInfo? audio) : IDemuxer
    {
        public VideoStreamInfo? Video => null;

        public AudioStreamInfo? Audio => audio;

        public IEnumerable<Packet> ReadPackets() => [];

        public void Dispose()
        {
        }
    }

    public static TheoryData<string, int> H264Samples()
    {
        var rows = new TheoryData<string, int>();
        if (TestSamples.Directory is null)
        {
            return rows;
        }

        foreach (string file in Directory.EnumerateFiles(TestSamples.Directory, "*.mxf").Order())
        {
            string name = Path.GetFileName(file);
            if (File.Exists(TestSamples.ReferenceOf(name, ".h264.framemd5")))
            {
                rows.Add(name, 1);
                rows.Add(name, 4);
            }
        }

        return rows;
    }

    /// <summary>
    /// The H.264 frames, against FFmpeg's (name.h264.framemd5, <c>ffmpeg -fps_mode passthrough -f framemd5</c>: every frame,
    /// none dropped): each frame's MD5, one and four threads. FFmpeg's MXF packets carry decode times as presentation
    /// times (its muxer writes no temporal offsets), so the file gives no frame its presentation time, and FFmpeg's frame
    /// times are its command line's guess, not a reference (0 2 3 5 6 7 7 7 on mxf-h264-small's two short GOPs). The
    /// decoder times frames as for any Annex B stream (an AVI's): the k-th shown at the decode time of the packet the
    /// SPS's reorder depth D later, so k + D in edit units.
    /// </summary>
    [Theory]
    [MemberData(nameof(H264Samples))]
    public void DecodesTheH264AsFfmpegDoes(string sampleName, int threads)
    {
        var lines = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".h264.framemd5"));
        var expected = FrameMd5.Parse(lines).Where(e => e.StreamIndex == 0).ToList();

        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var video = demuxer.Video!;
        Assert.Equal(CodecIds.H264, video.Codec);
        var decoder = new H264Decoder(video) { Threads = threads };
        var colors = ColorReferences.Read(sampleName);
        var frames = new List<(long Pts, string Md5)>();
        void Take(VideoFrame frame)
        {
            // The frame's colour as ffprobe lists it (name.color): the VUI's, else the picture descriptor's.
            colors?.Check(frames.Count, frame);
            frames.Add((frame.Pts, FrameMd5.Hash(frame)));
            frame.Dispose();
        }

        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex))
        {
            foreach (var frame in decoder.Decode(packet))
            {
                Take(frame);
            }
        }

        foreach (var frame in decoder.Flush())
        {
            Take(frame);
        }

        Assert.Equal(expected.Count, frames.Count);
        long depth = frames[0].Pts;
        for (int k = 0; k < frames.Count; k++)
        {
            Assert.True(expected[k].Md5 == frames[k].Md5, $"Frame {k} differs from FFmpeg's.");
            Assert.True(frames[k].Pts == k + depth, $"Frame {k}: pts {frames[k].Pts}, not {k + depth}.");
        }
    }

    /// <summary>
    /// A clip-wrapped file cut short loses its footer's index: FFmpeg's demuxer (<c>-fflags +noparse</c>) then gives the
    /// H.264 clip's bytes as one packet (its parser alone cuts that into pictures, which this library doesn't do), and
    /// a file cut inside its header metadata is still refused. The make-samples cut files cover the rest.
    /// </summary>
    [Fact]
    public void ReadsAClipCutShortAsOnePacket()
    {
        byte[] file = File.ReadAllBytes(TestSamples.PathOf("mxf-opatom-h264.mxf"));
        byte[] clip;
        using (var whole = new MxfDemuxer(new MemoryStream(file)))
        {
            clip = [.. whole.ReadPackets().SelectMany(p => p.Data.ToArray())];
        }

        using var cut = new MxfDemuxer(new MemoryStream(file[..(file.Length * 55 / 100)]));
        var packet = Assert.Single(cut.ReadPackets());
        Assert.InRange(packet.Data.Length, 1, clip.Length - 1);
        Assert.Equal(clip[..packet.Data.Length], packet.Data.ToArray());
        Assert.Equal(1, cut.Video!.FrameCount);

        Assert.Throws<InvalidDataException>(() => new MxfDemuxer(new MemoryStream(file[..1000])));
    }

    [Fact]
    public void NamesTheCodecsThisLibraryDecodes()
    {
        using var h264 = Demuxer.Open(TestSamples.PathOf("mxf-h264-pcm.mxf"));
        Assert.Equal(CodecIds.H264, h264.Video!.Codec);
        Assert.Equal(CodecIds.PcmS24Le, h264.Audio!.Codec);

        // The sampled height (120), not the stored one with the macroblock row's padding (128) FFmpeg's demuxer reports.
        Assert.Equal((160, 120), (h264.Video.Width, h264.Video.Height));
        Assert.Equal(new Rational(1, 25), h264.Video.TimeBase);
        Assert.Equal(25, h264.Video.FramesPerSecond);

        using var prores = Demuxer.Open(TestSamples.PathOf("mxf-prores-pcm16.mxf"));
        Assert.Equal("prores", prores.Video!.Codec);
        Assert.Equal(CodecIds.PcmS16Le, prores.Audio!.Codec);
        Assert.Equal(2, prores.Audio.Channels);

        using var interlaced = Demuxer.Open(TestSamples.PathOf("mxf-h264-interlaced.mxf"));
        Assert.Equal(new Rational(1001, 30000), interlaced.Video!.TimeBase);
        Assert.Equal(120, interlaced.Video.Height); // two sampled fields of 60
    }

    /// <summary>
    /// The colour samples do say what they are for, in the stream and in the frames: the descriptor's colour where the
    /// VUI says nothing, the VUI's where both speak (the stream keeps the descriptor's), full range by the levels, and
    /// limited range at 10 bits.
    /// </summary>
    [Theory]
    [InlineData("mxf-h264-colour-hlg.mxf", ColorRange.Limited, ColorPrimaries.Bt2020, ColorTransfer.AribStdB67, ColorMatrix.Bt2020Ncl, ColorRange.Limited, ColorPrimaries.Bt2020)]
    [InlineData("mxf-h264-colour-conflict.mxf", ColorRange.Limited, ColorPrimaries.Bt2020, ColorTransfer.Smpte2084, ColorMatrix.Bt2020Ncl, ColorRange.Full, ColorPrimaries.Bt709)]
    [InlineData("mxf-h264-colour-full.mxf", ColorRange.Full, ColorPrimaries.Smpte170M, ColorTransfer.Bt709, ColorMatrix.Bt470BG, ColorRange.Full, ColorPrimaries.Smpte170M)]
    [InlineData("mxf-h264-422-10.mxf", ColorRange.Limited, ColorPrimaries.Bt709, ColorTransfer.Bt709, ColorMatrix.Bt709, ColorRange.Limited, ColorPrimaries.Bt709)]
    [InlineData("mxf-h264-pcm.mxf", ColorRange.Unspecified, ColorPrimaries.Unspecified, ColorTransfer.Unspecified, ColorMatrix.Unspecified, ColorRange.Unspecified, ColorPrimaries.Unspecified)]
    public void GivesThePictureDescriptorsColour(string sampleName, ColorRange range, ColorPrimaries primaries, ColorTransfer transfer, ColorMatrix matrix, ColorRange frameRange, ColorPrimaries framePrimaries)
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf(sampleName)))
        {
            return;
        }

        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var video = demuxer.Video!;
        Assert.Equal((range, primaries, transfer, matrix), (video.ColorRange, video.ColorPrimaries, video.ColorTransfer, video.ColorMatrix));

        var decoder = new H264Decoder(video);
        using var frame = demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex).SelectMany(decoder.Decode).Concat(decoder.Flush()).First();
        Assert.Equal((frameRange, framePrimaries), (frame.ColorRange, frame.ColorPrimaries));
    }

    [Fact]
    public void AppliesTheIndexTablesTemporalOffsetsAndFlags()
    {
        // FFmpeg's muxer writes H.264 unreordered (PosTableIndex 0, temporal offsets 0): rewritten here, the picture's
        // delta entry (the second: a system item comes first) says reordered (-1), and the offsets are those of an I
        // picture, a P picture and the two B pictures before it, as MPEG-2 files have them (11.2.3, Table 28: the
        // picture shown n-th is stored at n plus its offset; mxf-mpeg2-b): shown 1 and 2 are stored at 2 and 3, shown
        // 3 at 1. Decode times start early by the largest offset. Edit unit 0 loses its random access flag while 2
        // gains it.
        byte[] file = File.ReadAllBytes(TestSamples.PathOf("mxf-h264-pcm.mxf"));
        var (deltas, entries, entryLength) = IndexArrays(file);
        int deltaLength = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(deltas + 4));
        file[deltas + 8 + deltaLength] = 0xFF;
        file[entries + 8 + entryLength] = 1;
        file[entries + 8 + (2 * entryLength)] = 1;
        file[entries + 8 + (3 * entryLength)] = unchecked((byte)-2);
        file[entries + 8 + 2] &= 0x7F;
        file[entries + 8 + (2 * entryLength) + 2] |= 0x80;

        using var demuxer = new MxfDemuxer(new MemoryStream(file));
        var video = demuxer.ReadPacketInfo().Where(p => p.StreamIndex == demuxer.Video!.StreamIndex).ToList();
        Assert.Equal([0L, 3, 1, 2, 4], video.Take(5).Select(p => p.Pts));
        Assert.Equal([-1L, 0, 1, 2, 3], video.Take(5).Select(p => p.Dts));
        Assert.Equal([false, false, true, false], video.Take(4).Select(p => p.IsKeyframe));

        // The first delta entry alone saying so is enough too (a file whose picture is the edit unit's first element).
        file[deltas + 8 + deltaLength] = 0;
        file[deltas + 8] = 0xFF;
        using var first = new MxfDemuxer(new MemoryStream(file));
        Assert.Equal([0L, 3, 1, 2], first.ReadPacketInfo().Where(p => p.StreamIndex == first.Video!.StreamIndex).Take(4).Select(p => p.Pts));

        // The same offsets with no element flagged as reordered are not applied.
        file[deltas + 8] = 0;
        using var unordered = new MxfDemuxer(new MemoryStream(file));
        var plain = unordered.ReadPacketInfo().Where(p => p.StreamIndex == unordered.Video!.StreamIndex).Take(4).ToList();
        Assert.Equal([0L, 1, 2, 3], plain.Select(p => p.Pts));
        Assert.Equal([0L, 1, 2, 3], plain.Select(p => p.Dts));
    }

    [Fact]
    public void RefusesAnotherStreamAndSeeksEachStreamToItsKeyframe()
    {
        using var demuxer = new MxfDemuxer(File.OpenRead(TestSamples.PathOf("mxf-h264-index.mxf")));
        Assert.Throws<ArgumentOutOfRangeException>(() => demuxer.Seek(0, 7));

        // 12 s in: the video from its keyframe at 12 s (a GOP of 25), the audio from 12 s exactly (every packet a keyframe).
        demuxer.Seek(300, demuxer.Video!.StreamIndex);
        var read = demuxer.ReadPacketInfo().ToList();
        Assert.Equal(300, read.First(p => p.StreamIndex == demuxer.Video.StreamIndex).Pts);
        Assert.Equal(300 * 1920, read.First(p => p.StreamIndex == demuxer.Audio!.StreamIndex).Pts);
    }

    [Fact]
    public void ThrowsOnlyInvalidDataOnAFileCutShort()
    {
        byte[] file = File.ReadAllBytes(TestSamples.PathOf("mxf-h264-pcm.mxf"));
        foreach (int length in new[] { 20, 100, 600, 3000, 8000, file.Length / 2, file.Length - 70, file.Length - 1 })
        {
            try
            {
                using var demuxer = new MxfDemuxer(new MemoryStream(file[..length]));
                _ = demuxer.ReadPackets().Count();
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
            }
        }
    }

    // Where the first index table segment's Delta Entry Array and Index Entry Array values start, and an entry's length.
    private static (int Deltas, int Entries, int EntryLength) IndexArrays(byte[] file)
    {
        byte[] key = Convert.FromHexString("060E2B34025301010D01020101100100");
        int at = file.AsSpan().IndexOf(key);
        Assert.True(at >= 0, "No index table segment.");
        int length = file[at + 16] == 0x83 ? (file[at + 17] << 16) | (file[at + 18] << 8) | file[at + 19] : throw new InvalidOperationException("Another BER length form.");
        int position = at + 20;
        int end = position + length;
        int deltas = -1;
        int entries = -1;
        while (position < end)
        {
            int tag = (file[position] << 8) | file[position + 1];
            int size = (file[position + 2] << 8) | file[position + 3];
            if (tag == 0x3F09)
            {
                deltas = position + 4;
            }
            else if (tag == 0x3F0A)
            {
                entries = position + 4;
            }

            position += 4 + size;
        }

        int entryLength = (file[entries + 4] << 24) | (file[entries + 5] << 16) | (file[entries + 6] << 8) | file[entries + 7];
        return (deltas, entries, entryLength);
    }

    [Fact]
    public void RejectsWhatIsNotMxf()
    {
        Assert.Throws<InvalidDataException>(() => new MxfDemuxer(new MemoryStream(new byte[200])));
        Assert.Throws<ArgumentException>(() => new MxfDemuxer(new NonSeekableStream()));
    }

    private static PacketInfo Info(Packet p) =>
        new(p.StreamIndex, p.Sequence, p.Data.Length, p.IsKeyframe, p.Dts, p.Pts, p.Duration, p.SkipSamples, p.Discard);

    private static List<Probed> Parse(string path) =>
    [
        .. File.ReadAllLines(path).Where(l => l.Length > 0).Select(line =>
        {
            string[] f = line.Split(',');
            long? Number(string s) => s == "N/A" ? null : long.Parse(s, CultureInfo.InvariantCulture);
            return new Probed(
                int.Parse(f[0], CultureInfo.InvariantCulture),
                Number(f[1]),
                Number(f[2]),
                Number(f[3]) ?? 0,
                int.Parse(f[4], CultureInfo.InvariantCulture),
                f[5].StartsWith('K'),
                f.Length > 6 ? f[6]["MD5:".Length..] : null);
        }),
    ];

    private sealed record Probed(int StreamIndex, long? Pts, long? Dts, long Duration, int Size, bool IsKeyframe, string? Md5);

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}

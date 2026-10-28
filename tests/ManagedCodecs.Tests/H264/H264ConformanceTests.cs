using ManagedCodecs.Containers;
using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// Decodes every Constrained Baseline, Main and High profile H.264 sample (samples/*.mp4 with a .framemd5)
/// and compares each frame's MD5 and timestamp, in output order, with FFmpeg's. See samples/README.md.
/// Timestamps are compared from the first frame's, as FFmpeg's framemd5 moves them to start at 0.
/// </summary>
public class H264ConformanceTests
{
    public static TheoryData<string> Samples() =>
        new(TestSamples.WithReference(".framemd5").Where(IsSupported));

    [Theory]
    [MemberData(nameof(Samples))]
    public Task MatchesFfmpegFrameByFrame(string sampleName) => CompareWithFfmpeg(sampleName, borrowFrames: false, dispose: false);

    /// <summary>
    /// The same with every frame disposed as soon as it is hashed, so the decoder writes later
    /// frames into the planes it gives back: a frame still in use reusing them would show as a
    /// wrong frame.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public Task MatchesFfmpegWithDisposedFrames(string sampleName) => CompareWithFfmpeg(sampleName, borrowFrames: false, dispose: true);

    /// <summary>
    /// The same with <see cref="H264Decoder.BorrowFrames"/>: each frame is hashed as it comes
    /// back, before the next call, which is as long as a borrowed frame is valid. A picture reused
    /// while its frame still waited for reordering would show as a wrong frame. The frames are
    /// disposed too, which must leave the decoder's pictures (later frames' references) alone.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public Task MatchesFfmpegWithBorrowedFrames(string sampleName) => CompareWithFfmpeg(sampleName, borrowFrames: true, dispose: true);

    /// <summary>
    /// The same with frame threading (<see cref="H264Decoder.Threads"/>), more threads than cores so that they contend,
    /// and the frames disposed: the waits for references must give the frames of one thread.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public Task MatchesFfmpegWithThreads(string sampleName) =>
        CompareWithFfmpeg(sampleName, borrowFrames: false, dispose: true, threads: Environment.ProcessorCount + 1);

    /// <summary>
    /// The same with four threads that sleep up to a millisecond, at random, after each macroblock row: pictures then
    /// overtake each other where they may, and a wait that is too short (a row read before it is final) shows as a
    /// wrong frame where, without the delays, the threads would most often have been in time by luck.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public Task MatchesFfmpegWithDelayedThreads(string sampleName) =>
        CompareWithFfmpeg(sampleName, borrowFrames: false, dispose: true, threads: 4, rowDelay: 1);

    /// <summary>
    /// The same with every picture decoder checking its neighbour context (PictureDecoder.Context.cs) against the
    /// lookups it replaces: each nC, neighbouring motion, predicted intra mode and intra availability is derived both
    /// ways, and a difference throws (docs/decisions/2026-10-28-h264-neighbour-context.md).
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public Task NeighbourContextMatchesLookups(string sampleName) =>
        CompareWithFfmpeg(sampleName, borrowFrames: false, dispose: true, verifyNeighbourContext: true);

    /// <summary>
    /// The same through the asynchronous API: packets from <see cref="IDemuxer.ReadPacketsAsync"/>, frames from
    /// <see cref="H264Decoder.DecodeAsync"/> and <see cref="H264Decoder.FlushAsync"/> with two threads, which await the
    /// pictures being decoded instead of blocking.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public Task MatchesFfmpegAsynchronously(string sampleName) =>
        CompareWithFfmpeg(sampleName, borrowFrames: false, dispose: true, threads: 2, asynchronous: true);

    private static async Task CompareWithFfmpeg(string sampleName, bool borrowFrames, bool dispose, int threads = 1, int rowDelay = 0, bool asynchronous = false, bool verifyNeighbourContext = false)
    {
        var lines = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".framemd5"));
        var expected = FrameMd5.Parse(lines).Where(e => e.StreamIndex == 0).ToList(); // FFmpeg lists video first
        var timeBase = FrameMd5.ParseTimeBase(lines);

        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        var video = demuxer.Video!;
        var decoder = new H264Decoder(video) { BorrowFrames = borrowFrames, Threads = threads, RowDelay = rowDelay, VerifyNeighbourContext = verifyNeighbourContext };

        int index = 0;
        long firstPts = 0;
        void Check(VideoFrame frame)
        {
            Assert.True(index < expected.Count, $"More frames than the reference ({expected.Count}).");
            Assert.True(expected[index].Md5 == FrameMd5.Hash(frame), $"Frame {index} differs from FFmpeg.");
            if (index == 0)
            {
                firstPts = frame.Pts;
            }

            Assert.True(
                FrameMd5.SameTime(expected[index].Pts - expected[0].Pts, timeBase, frame.Pts - firstPts, video.TimeBase),
                $"Frame {index}: pts {frame.Pts} ({video.TimeBase}) is not FFmpeg's {expected[index].Pts} ({timeBase}).");
            index++;
            if (dispose)
            {
                frame.Dispose();
            }
        }

        if (asynchronous)
        {
            await foreach (var packet in demuxer.ReadPacketsAsync())
            {
                if (packet.StreamIndex == video.StreamIndex)
                {
                    foreach (var frame in await decoder.DecodeAsync(packet))
                    {
                        Check(frame);
                    }
                }
            }

            foreach (var frame in await decoder.FlushAsync())
            {
                Check(frame);
            }
        }
        else
        {
            foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex))
            {
                foreach (var frame in decoder.Decode(packet))
                {
                    Check(frame);
                }
            }

            foreach (var frame in decoder.Flush())
            {
                Check(frame);
            }
        }

        Assert.Equal(expected.Count, index);

        // The statistics below are counted without locks, exact with one thread only.
        if (threads > 1)
        {
            return;
        }

        // Field picture samples (from JM) must pair fields, and predict second fields from their first.
        if (sampleName.Contains("paff", StringComparison.Ordinal))
        {
            var fields = decoder.FieldStatistics;
            Assert.True(fields.SecondFields > 0 && fields.FromOwnFirstField > 0, $"{fields.SecondFields} field pairs, {fields.FromOwnFirstField} partitions predicted from their own first field.");
            Assert.True(!sampleName.Contains("adaptive", StringComparison.Ordinal) || fields.FramePictures > 0, "No frame pictures between the field pairs.");

            // MBAFF frames between field pictures (JM codes every pair of them as a field pair).
            Assert.True(!sampleName.Contains("paff-mbaff", StringComparison.Ordinal) || (fields.FramePictures > 0 && decoder.MbaffStatistics.FieldPairs > 0), $"{fields.FramePictures} frame pictures, {decoder.MbaffStatistics.FieldPairs} field macroblock pairs.");
            if (sampleName.Contains("paff-b", StringComparison.Ordinal))
            {
                // B field pictures: direct prediction in fields, temporal where the sample says so (the CAVLC one is
                // temporal too), and in the adaptive ones co-located blocks across frame and field coding: B frames over
                // fields in both, B fields over frames in the spatial one (JM's temporal one codes no such pair).
                Assert.True(decoder.MbaffStatistics.DirectField > 0, "No direct prediction in field pictures.");
                Assert.True((!sampleName.Contains("temporal", StringComparison.Ordinal) && !sampleName.Contains("cavlc", StringComparison.Ordinal)) || fields.TemporalDirect > 0, "No temporal direct prediction in field pictures.");
                bool adaptive = sampleName.Contains("adaptive", StringComparison.Ordinal);
                bool temporal = sampleName.Contains("temporal", StringComparison.Ordinal);
                Assert.True(!adaptive || (fields.FrameOverFields > 0 && (temporal || fields.FieldOverFrame > 0)), $"{fields.FieldOverFrame} field blocks over frames, {fields.FrameOverFields} frame blocks over fields.");
            }
        }

        // MBAFF samples (libx264's) must mix field and frame pairs every way they can meet.
        if (sampleName.Contains("mbaff", StringComparison.Ordinal) && !sampleName.Contains("paff", StringComparison.Ordinal))
        {
            var stats = decoder.MbaffStatistics;
            Assert.True(stats.FieldPairs > 0 && stats.FramePairs > 0, $"{stats.FieldPairs} field and {stats.FramePairs} frame pairs.");
            Assert.True(stats.MixedLeft > 0 && stats.FrameBelowField > 0 && stats.FieldBelowFrame > 0, "Not every mixture of field and frame pairs occurs.");
            if (sampleName.Contains("mbaff-p", StringComparison.Ordinal))
            {
                Assert.True(stats.SkippedPairs > 0 && stats.TopOnlySkipped > 0, $"{stats.SkippedPairs} skipped pairs, {stats.TopOnlySkipped} with only the top skipped.");
                Assert.True(stats.OppositeParity > 0, "No field macroblock predicts from the other field.");
            }

            if (sampleName.Contains("mbaff-b", StringComparison.Ordinal))
            {
                Assert.True(stats.DirectField > 0 && stats.MixedCoLocated > 0, $"{stats.DirectField} direct blocks in field macroblocks, {stats.MixedCoLocated} co-located in a pair of the other kind.");
                Assert.True(stats.ImplicitField > 0, "No implicitly weighted field macroblock partitions.");
                Assert.True(!sampleName.Contains("temporal", StringComparison.Ordinal) || stats.TemporalField > 0, "No temporal direct field macroblocks.");
            }
        }
    }

    /// <summary>H.264 in MP4 whose avcC says profile_idc 66 (Baseline), 77 (Main) or 100 (High).</summary>
    private static bool IsSupported(string name)
    {
        if (!name.EndsWith(".mp4", StringComparison.Ordinal))
        {
            return false;
        }

        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(name));
        if (demuxer.Video is not { Codec: CodecIds.H264 } video)
        {
            return false;
        }

        var avcC = video.CodecPrivate.Span;
        return avcC.Length > 1 && avcC[1] is 66 or 77 or 100 or 110 or 122 or 244;
    }
}

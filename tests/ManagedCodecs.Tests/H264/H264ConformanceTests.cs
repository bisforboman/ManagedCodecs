using ManagedCodecs.Containers.Mp4;
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
    public void MatchesFfmpegFrameByFrame(string sampleName) => CompareWithFfmpeg(sampleName, borrowFrames: false);

    /// <summary>
    /// The same with <see cref="H264Decoder.BorrowFrames"/>: each frame is hashed as it comes
    /// back, before the next call, which is as long as a borrowed frame is valid. A picture reused
    /// while its frame still waited for reordering would show as a wrong frame.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegWithBorrowedFrames(string sampleName) => CompareWithFfmpeg(sampleName, borrowFrames: true);

    private static void CompareWithFfmpeg(string sampleName, bool borrowFrames)
    {
        var lines = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".framemd5"));
        var expected = FrameMd5.Parse(lines).Where(e => e.StreamIndex == 0).ToList(); // FFmpeg lists video first
        var timeBase = FrameMd5.ParseTimeBase(lines);

        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        var video = demuxer.Video!;
        var decoder = new H264Decoder(video) { BorrowFrames = borrowFrames };

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
        }

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

        Assert.Equal(expected.Count, index);

        // Field picture samples (from JM) must pair fields, and predict second fields from their first.
        if (sampleName.Contains("paff", StringComparison.Ordinal))
        {
            var fields = decoder.FieldStatistics;
            Assert.True(fields.SecondFields > 0 && fields.FromOwnFirstField > 0, $"{fields.SecondFields} field pairs, {fields.FromOwnFirstField} partitions predicted from their own first field.");
            Assert.True(!sampleName.Contains("adaptive", StringComparison.Ordinal) || fields.FramePictures > 0, "No frame pictures between the field pairs.");
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

        // MBAFF samples must mix field and frame pairs every way they can meet.
        if (sampleName.Contains("mbaff", StringComparison.Ordinal))
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
        return avcC.Length > 1 && avcC[1] is 66 or 77 or 100 or 110 or 122;
    }
}

using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// Scaling matrix signalling libx264 never writes (it sends lists in the PPS only, and one chroma
/// list per intra and inter), from samples whose parameter sets are rewritten: the same matrix
/// signalled another way must decode to the same frames as the original, which the conformance
/// tests check against FFmpeg. Covers SPS lists, fall-back rule B (Table 7-2),
/// useDefaultScalingMatrixFlag and a Cr list of its own.
/// </summary>
public class H264ScalingMatrixTests
{
    private const string Custom = "h264-cqm-custom.mp4";
    private const string Jvt = "h264-cqm-jvt.mp4";

    public static TheoryData<string> CustomSamples() => new(new[] { Custom, "h264-cqm-custom-cavlc.mp4" }.Where(ParameterSetRewriter.Exists));

    public static TheoryData<string> JvtSamples() => new(new[] { Jvt }.Where(ParameterSetRewriter.Exists));

    [Theory]
    [MemberData(nameof(CustomSamples))]
    public void SequenceLists_WithoutPictureLists_AreUsed(string sampleName)
    {
        var lists = SentLists(sampleName);
        var moved = ParameterSetRewriter.Decode(
            sampleName,
            sps: nal => ParameterSetRewriter.Sps(nal, lists),
            pps: nal => ParameterSetRewriter.Pps(nal, replaceScaling: true, scaling: null));
        AssertSameFrames(ParameterSetRewriter.Decode(sampleName), moved);
    }

    /// <summary>
    /// Rule B with every picture list absent: lists 0, 3, 6 and 7 come from the sequence, but 1, 2, 4
    /// and 5 still follow the picture's list before them, so Cb takes the luma list here, not the
    /// sequence's Cb list. The same matrix sent in full under rule A must decode alike.
    /// </summary>
    [Theory]
    [MemberData(nameof(CustomSamples))]
    public void RuleB_AbsentPictureListsTakeTheSequenceLists(string sampleName)
    {
        var sequence = Resolved(SentLists(sampleName));
        var ruleB = ParameterSetRewriter.Decode(
            sampleName,
            sps: nal => ParameterSetRewriter.Sps(nal, new ScalingLists(sequence)),
            pps: nal => ParameterSetRewriter.Pps(nal, replaceScaling: true, scaling: new ScalingLists(new byte[]?[8])));
        var full = ParameterSetRewriter.Decode(
            sampleName,
            pps: nal => ParameterSetRewriter.Pps(nal, replaceScaling: true, scaling: new ScalingLists([sequence[0], sequence[0], sequence[0], sequence[3], sequence[3], sequence[3], sequence[6], sequence[7]])));
        AssertSameFrames(full, ruleB);
        Assert.False(SameFrames(ParameterSetRewriter.Decode(sampleName), ruleB), "The chroma lists changed nothing.");
    }

    /// <summary>
    /// Rule B with a picture list 0 of its own: lists 1 and 2 follow it, lists 3, 6 and 7 come from
    /// the sequence and 4 and 5 follow 3. The same matrix sent in full under rule A must decode alike.
    /// </summary>
    [Theory]
    [MemberData(nameof(CustomSamples))]
    public void RuleB_ListsOneAndTwoFollowThePictureList(string sampleName)
    {
        var sequence = Resolved(SentLists(sampleName));
        byte[] own = [.. Enumerable.Range(0, 16).Select(i => (byte)(40 - (2 * i)))];
        var ruleB = ParameterSetRewriter.Decode(
            sampleName,
            sps: nal => ParameterSetRewriter.Sps(nal, new ScalingLists(sequence)),
            pps: nal => ParameterSetRewriter.Pps(nal, replaceScaling: true, scaling: new ScalingLists([own, null, null, null, null, null, null, null])));
        var full = ParameterSetRewriter.Decode(
            sampleName,
            pps: nal => ParameterSetRewriter.Pps(nal, replaceScaling: true, scaling: new ScalingLists([own, own, own, sequence[3], sequence[3], sequence[3], sequence[6], sequence[7]])));
        AssertSameFrames(full, ruleB);
        Assert.False(SameFrames(ParameterSetRewriter.Decode(sampleName), ruleB), "The changed intra lists changed nothing.");
    }

    [Theory]
    [MemberData(nameof(JvtSamples))]
    public void UseDefaultScalingMatrixFlag_GivesTheDefaultLists(string sampleName)
    {
        var flagged = ParameterSetRewriter.Decode(
            sampleName,
            pps: nal => ParameterSetRewriter.Pps(nal, replaceScaling: true, scaling: new ScalingLists([.. Enumerable.Range(0, 8).Select(_ => Array.Empty<byte>())])));
        AssertSameFrames(ParameterSetRewriter.Decode(sampleName), flagged);
    }

    [Theory]
    [MemberData(nameof(JvtSamples))]
    public void RuleA_InTheSequence_GivesTheDefaultLists(string sampleName)
    {
        var sequence = ParameterSetRewriter.Decode(
            sampleName,
            sps: nal => ParameterSetRewriter.Sps(nal, new ScalingLists(new byte[]?[8])),
            pps: nal => ParameterSetRewriter.Pps(nal, replaceScaling: true, scaling: null));
        AssertSameFrames(ParameterSetRewriter.Decode(sampleName), sequence);
    }

    /// <summary>
    /// A Cr list of its own (intra and inter): with Cb's lists a and Cr's b, Cb and luma must come out
    /// as with a for both, Cr as with b for both.
    /// </summary>
    [Theory]
    [MemberData(nameof(CustomSamples))]
    public void CrList_OfItsOwn_ScalesCrOnly(string sampleName)
    {
        var lists = Resolved(SentLists(sampleName));
        byte[] intraCr = [.. Enumerable.Range(0, 16).Select(i => (byte)(60 - (3 * i)))];
        byte[] interCr = [.. Enumerable.Range(0, 16).Select(i => (byte)(8 + (5 * i)))];
        List<VideoFrame> Decode(byte[] intraCb, byte[] intraCrList, byte[] interCb, byte[] interCrList) =>
            ParameterSetRewriter.Decode(
                sampleName,
                pps: nal => ParameterSetRewriter.Pps(nal, replaceScaling: true, scaling: new ScalingLists([lists[0], intraCb, intraCrList, lists[3], interCb, interCrList, lists[6], lists[7]])));

        var mixed = Decode(lists[1], intraCr, lists[4], interCr);
        var cbOnly = Decode(lists[1], lists[1], lists[4], lists[4]);
        var crOnly = Decode(intraCr, intraCr, interCr, interCr);
        Assert.Equal(cbOnly.Count, mixed.Count);
        for (int i = 0; i < mixed.Count; i++)
        {
            Assert.Equal(cbOnly[i].Planes[0], mixed[i].Planes[0]);
            Assert.Equal(cbOnly[i].Planes[1], mixed[i].Planes[1]);
            Assert.Equal(crOnly[i].Planes[2], mixed[i].Planes[2]);
        }

        Assert.Contains(Enumerable.Range(0, mixed.Count), i => !mixed[i].Planes[2].AsSpan().SequenceEqual(cbOnly[i].Planes[2]));
    }

    /// <summary>The lists the sample's PPS sends (libx264 leaves Cr's out).</summary>
    private static ScalingLists SentLists(string sampleName)
    {
        byte[]?[]? lists = null;
        ParameterSetRewriter.Decode(sampleName, pps: nal =>
        {
            lists ??= ParameterSetRewriter.PpsScalingLists(nal);
            return nal;
        });
        Assert.NotNull(lists);
        Assert.Equal(8, lists.Length);
        return new ScalingLists(lists);
    }

    /// <summary>All eight lists of a set whose lists 0, 3, 6 and 7 are present (rule A's fall-back from the list before).</summary>
    private static byte[][] Resolved(ScalingLists sent)
    {
        var lists = new byte[8][];
        for (int i = 0; i < 8; i++)
        {
            lists[i] = sent.Lists[i] ?? lists[i - 1];
        }

        return lists;
    }

    private static bool SameFrames(List<VideoFrame> a, List<VideoFrame> b) =>
        a.Count == b.Count && a.Zip(b).All(p => Enumerable.Range(0, 3).All(c => p.First.Planes[c].AsSpan().SequenceEqual(p.Second.Planes[c])));

    private static void AssertSameFrames(List<VideoFrame> expected, List<VideoFrame> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.True(FrameMd5.Hash(expected[i]) == FrameMd5.Hash(actual[i]), $"Frame {i} differs.");
        }
    }
}

using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// Reference list construction (8.2.4) and marking (8.2.5) for cases libx264's Baseline output
/// does not produce, so the FFmpeg conformance samples do not cover them.
/// </summary>
public class ReferenceListTests
{
    private static readonly SequenceParameterSet Sps = new() { Log2MaxFrameNum = 4, MaxNumRefFrames = 3, WidthInMbs = 1, HeightInMapUnits = 1, FrameMbsOnly = true };

    [Fact]
    public void SlidingWindow_DropsTheOldestShortTermPicture()
    {
        var refs = new ReferenceList();
        Decode(refs, 0, idr: true);
        Decode(refs, 1);
        Decode(refs, 2);
        Decode(refs, 3); // the DPB holds 3 frames, so frame 0 goes

        Assert.Equal([3, 2, 1], FrameNums(refs.BuildList(Header(4, numRef: 3), Sps)));
    }

    [Fact]
    public void ShortTermOrder_FollowsFrameNumWrap()
    {
        var refs = new ReferenceList();
        Decode(refs, 0, idr: true);
        for (int frameNum = 1; frameNum < 16; frameNum++)
        {
            Decode(refs, frameNum);
        }

        Decode(refs, 0); // frame_num wraps at 16
        Decode(refs, 1);

        // Frame 15 is older than 0 and 1, so it comes last despite its larger frame_num.
        Assert.Equal([1, 0, 15], FrameNums(refs.BuildList(Header(2, numRef: 3), Sps)));
    }

    [Fact]
    public void Modification_MovesShortTermPicturesToTheFront()
    {
        var refs = DecodeFrames(4); // references 1, 2, 3 (frame 0 left the window)

        // picNumPred starts at 4: subtract 3 for picture 1, then add 1 for picture 2.
        var header = Header(4, numRef: 3, modifications: [new(0, 2), new(1, 0)]);

        Assert.Equal([1, 2, 3], FrameNums(refs.BuildList(header, Sps)));
    }

    [Fact]
    public void MemoryManagement_UnmarksAndMakesLongTermPictures()
    {
        var refs = DecodeFrames(3); // references 0, 1, 2

        // Frame 3: drop picture 2 (3 - 1), allow one long-term index, keep frame 3 as long-term 0.
        var marking = Header(3, operations:
        [
            new MemoryManagementOperation(1, 1, 0, 0, 0),
            new MemoryManagementOperation(4, 0, 0, 0, 1),
            new MemoryManagementOperation(6, 0, 0, 0, 0),
        ]);
        refs.Mark(marking, new Picture(1, 1) { FrameNum = 3 }, Sps);

        // Short-term pictures by descending PicNum first, then long-term ones.
        var list = refs.BuildList(Header(4, numRef: 3), Sps);
        Assert.Equal([1, 0, 3], FrameNums(list));
        Assert.True(list[2].IsLongTermReference);

        // A long-term modification (idc 2, LongTermPicNum 0) moves frame 3 to the front.
        var modified = refs.BuildList(Header(4, numRef: 3, modifications: [new(2, 0)]), Sps);
        Assert.Equal([3, 1, 0], FrameNums(modified));
    }

    private static ReferenceList DecodeFrames(int count)
    {
        var refs = new ReferenceList();
        for (int frameNum = 0; frameNum < count; frameNum++)
        {
            Decode(refs, frameNum, idr: frameNum == 0);
        }

        return refs;
    }

    [Fact]
    public void BLists_OrderByPictureOrderCountAroundTheCurrentPicture()
    {
        // References at order counts 0, 8, 4 and 16 (decoding order); the B picture is at 6.
        var refs = new ReferenceList();
        Decode(refs, 0, idr: true, poc: 0);
        Decode(refs, 1, poc: 8);
        Decode(refs, 2, poc: 4);
        var sps = new SequenceParameterSet { Log2MaxFrameNum = 4, MaxNumRefFrames = 4, WidthInMbs = 1, HeightInMapUnits = 1, FrameMbsOnly = true };
        refs.Mark(Header(3), new Picture(1, 1) { FrameNum = 3, Poc = 16 }, sps);

        var (l0, l1) = refs.BuildLists(BHeader(4, numRef0: 4, numRef1: 4), sps, currentPoc: 6);

        // List 0: before the current picture, latest first, then after it, earliest first.
        Assert.Equal([4, 0, 8, 16], Pocs(l0));

        // List 1: the other way round.
        Assert.Equal([8, 16, 4, 0], Pocs(l1));

        // Truncation to the active counts.
        (l0, l1) = refs.BuildLists(BHeader(4, numRef0: 2, numRef1: 1), sps, currentPoc: 6);
        Assert.Equal([4, 0], Pocs(l0));
        Assert.Equal([8], Pocs(l1));
    }

    [Fact]
    public void BLists_SwapTheFirstTwoOfList1WhenBothListsAreEqual()
    {
        // Every reference precedes the current picture, so both initial lists would be 4, 0.
        var refs = new ReferenceList();
        Decode(refs, 0, idr: true, poc: 0);
        Decode(refs, 1, poc: 4);

        var (l0, l1) = refs.BuildLists(BHeader(2, numRef0: 2, numRef1: 2), Sps, currentPoc: 6);
        Assert.Equal([4, 0], Pocs(l0));
        Assert.Equal([0, 4], Pocs(l1));

        // The rule looks at the whole initial list: with one active entry list 1 starts with the swapped one.
        (_, l1) = refs.BuildLists(BHeader(2, numRef0: 1, numRef1: 1), Sps, currentPoc: 6);
        Assert.Equal([0], Pocs(l1));
    }

    private static void Decode(ReferenceList refs, int frameNum, bool idr = false, int poc = 0)
    {
        if (idr)
        {
            refs.Clear();
        }

        refs.Mark(Header(frameNum, idr: idr), new Picture(1, 1) { FrameNum = frameNum, Poc = poc }, Sps);
    }

    private static SliceHeader BHeader(int frameNum, int numRef0, int numRef1) => new()
    {
        Type = SliceType.B,
        FrameNum = frameNum,
        NalRefIdc = 0,
        NumRefIdxL0Active = numRef0,
        NumRefIdxL1Active = numRef1,
    };

    private static int[] Pocs(IEnumerable<Picture> pictures) => [.. pictures.Select(p => p.Poc)];

    private static SliceHeader Header(
        int frameNum,
        bool idr = false,
        int numRef = 1,
        IReadOnlyList<RefPicListModification>? modifications = null,
        IReadOnlyList<MemoryManagementOperation>? operations = null) => new()
        {
            Type = SliceType.P,
            FrameNum = frameNum,
            IsIdr = idr,
            NalRefIdc = 1,
            NumRefIdxL0Active = numRef,
            RefPicListModificationL0 = modifications ?? [],
            AdaptiveRefPicMarking = operations is not null,
            MemoryManagement = operations ?? [],
        };

    private static int[] FrameNums(IEnumerable<Picture> pictures) => [.. pictures.Select(p => p.FrameNum)];
}

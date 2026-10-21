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

    private static readonly SequenceParameterSet FieldSps = new() { Log2MaxFrameNum = 4, MaxNumRefFrames = 2, WidthInMbs = 1, HeightInMapUnits = 1, FrameMbsOnly = false };

    private static readonly SequenceParameterSet FieldSps3 = new() { Log2MaxFrameNum = 4, MaxNumRefFrames = 3, WidthInMbs = 1, HeightInMapUnits = 1, FrameMbsOnly = false };

    [Fact]
    public void FieldLists_AlternateParityFromTheCurrentFieldsAndIncludeTheFirstField()
    {
        var sps = FieldSps3;
        var refs = new ReferenceList();
        DecodeFields(refs, 0, idr: true, sps);
        DecodeFields(refs, 1, sps: sps);
        var frame2 = new Picture(1, 1) { FrameNum = 2 };
        refs.MarkField(FieldHeader(2, bottom: false), frame2, sps, secondField: false);

        // The bottom field of frame 2: bottom fields first, latest frame first, alternating with top fields; the
        // top field of its own frame comes in as the latest top field (8.2.4.2.5).
        Assert.Equal(["1b", "2t", "0b", "1t", "0t"], Fields(refs.BuildList(FieldHeader(2, bottom: true, numRef: 5), sps)));
    }

    [Fact]
    public void FieldMarking_SecondFieldOfAReferenceFrameDoesNotSlideTheWindow()
    {
        var refs = new ReferenceList();
        DecodeFields(refs, 0, idr: true);
        DecodeFields(refs, 1);

        // Two frames fill the window: frame 2's top field pushes frame 0 out, its bottom field pushes nothing out
        // (8.2.5.3: the first field of the frame is already a short-term reference).
        DecodeFields(refs, 2);

        Assert.Equal(["2t", "2b", "1t", "1b"], Fields(refs.BuildList(FieldHeader(3, bottom: false, numRef: 4), FieldSps)));
    }

    [Fact]
    public void FieldMarking_UnmarksByFieldPicNum()
    {
        var sps = FieldSps3;
        var refs = new ReferenceList();
        DecodeFields(refs, 0, idr: true, sps);
        DecodeFields(refs, 1, sps: sps);

        // The top field of frame 2 (CurrPicNum 2 * 2 + 1 = 5) unmarks PicNum 2: the bottom field of frame 1, as
        // fields of the other parity have PicNum 2 * FrameNumWrap and those of the same parity 2 * FrameNumWrap + 1.
        var frame2 = new Picture(1, 1) { FrameNum = 2 };
        refs.MarkField(FieldHeader(2, bottom: false, operations: [new MemoryManagementOperation(1, 3, 0, 0, 0)]), frame2, sps, secondField: false);

        Assert.Equal(["0b", "2t", "1t", "0t"], Fields(refs.BuildList(FieldHeader(2, bottom: true, numRef: 4), sps)));
    }

    [Fact]
    public void FieldBLists_OrderFramesByOrderCountThenAlternateParity()
    {
        // Frames with fields at order counts 0 and 1, and 8 and 9; the B field is the top one at 4.
        var refs = new ReferenceList();
        DecodeFields(refs, 0, idr: true, FieldSps3, topPoc: 0);
        DecodeFields(refs, 1, sps: FieldSps3, topPoc: 8);

        var (l0, l1) = refs.BuildLists(FieldBHeader(2, bottom: false, numRef: 4), FieldSps3, currentPoc: 4);

        // List 0: the frame before first, list 1: the one after; each frame's top field before its bottom one.
        Assert.Equal(["0t", "0b", "1t", "1b"], Fields(l0));
        Assert.Equal(["1t", "1b", "0t", "0b"], Fields(l1));

        // A bottom field starts each frame with the bottom field.
        (l0, _) = refs.BuildLists(FieldBHeader(2, bottom: true, numRef: 4), FieldSps3, currentPoc: 5);
        Assert.Equal(["0b", "0t", "1b", "1t"], Fields(l0));
    }

    [Fact]
    public void FieldBLists_PutAFrameAtTheCurrentOrderCountBeforeIt()
    {
        // 8.2.4.2.4 splits at PicOrderCnt(f) <= PicOrderCnt(CurrPic): a frame at the current field's order count
        // counts as before it, so it leads list 0 and follows the later frame in list 1.
        var refs = new ReferenceList();
        DecodeFields(refs, 0, idr: true, FieldSps3, topPoc: 4);
        DecodeFields(refs, 1, sps: FieldSps3, topPoc: 8);

        var (l0, l1) = refs.BuildLists(FieldBHeader(2, bottom: false, numRef: 4), FieldSps3, currentPoc: 4);
        Assert.Equal(["0t", "0b", "1t", "1b"], Fields(l0));
        Assert.Equal(["1t", "1b", "0t", "0b"], Fields(l1));
    }

    [Fact]
    public void FieldBLists_SwapTheFirstTwoOfList1WhenBothListsAreEqual()
    {
        // One reference frame, before the B field: both lists would be its two fields in the same order.
        var refs = new ReferenceList();
        DecodeFields(refs, 0, idr: true, topPoc: 0);

        var (l0, l1) = refs.BuildLists(FieldBHeader(1, bottom: false, numRef: 2), FieldSps, currentPoc: 4);
        Assert.Equal(["0t", "0b"], Fields(l0));
        Assert.Equal(["0b", "0t"], Fields(l1));
    }

    private static SliceHeader FieldBHeader(int frameNum, bool bottom, int numRef) => new()
    {
        Type = SliceType.B,
        FrameNum = frameNum,
        FieldPic = true,
        BottomField = bottom,
        NalRefIdc = 0,
        NumRefIdxL0Active = numRef,
        NumRefIdxL1Active = numRef,
        RefPicListModificationL0 = [],
        RefPicListModificationL1 = [],
    };

    private static void DecodeFields(ReferenceList refs, int frameNum, bool idr = false, SequenceParameterSet? sps = null, int topPoc = 0)
    {
        if (idr)
        {
            refs.Clear();
        }

        var frame = new Picture(1, 1) { FrameNum = frameNum, Poc = topPoc, TopPoc = topPoc, BottomPoc = topPoc + 1 };
        refs.MarkField(FieldHeader(frameNum, bottom: false, idr: idr), frame, sps ?? FieldSps, secondField: false);
        refs.MarkField(FieldHeader(frameNum, bottom: true), frame, sps ?? FieldSps, secondField: true);
    }

    private static SliceHeader FieldHeader(int frameNum, bool bottom, bool idr = false, int numRef = 1, IReadOnlyList<MemoryManagementOperation>? operations = null) => new()
    {
        Type = SliceType.P,
        FrameNum = frameNum,
        FieldPic = true,
        BottomField = bottom,
        IsIdr = idr,
        NalRefIdc = 1,
        NumRefIdxL0Active = numRef,
        RefPicListModificationL0 = [],
        AdaptiveRefPicMarking = operations is not null,
        MemoryManagement = operations ?? [],
    };

    private static string[] Fields(IEnumerable<Picture> fields) => [.. fields.Select(f => $"{f.Frame.FrameNum}{(f.Parity == 0 ? 't' : 'b')}")];

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

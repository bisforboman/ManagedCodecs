using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// Picture order counts (8.2.1) on hand-worked sequences. The conformance samples are all type 2
/// (libx264 without B frames), so types 0 and 1 are checked here.
/// </summary>
public class PictureOrderTests
{
    [Fact]
    public void Type0_FollowsTheLsbAcrossWraparound()
    {
        var sps = new SequenceParameterSet { PicOrderCntType = 0, Log2MaxPicOrderCntLsb = 4, Log2MaxFrameNum = 4 };
        var order = new PictureOrder();

        Assert.Equal(0, order.Next(Header(idr: true, lsb: 0), sps));
        Assert.Equal(8, order.Next(Header(lsb: 8), sps));
        Assert.Equal(4, order.Next(Header(lsb: 4, reference: false), sps)); // B-like, shown before the 8
        Assert.Equal(14, order.Next(Header(lsb: 14), sps));
        Assert.Equal(18, order.Next(Header(lsb: 2), sps)); // 2 after 14 is past the wrap: msb 16
        Assert.Equal(16, order.Next(Header(lsb: 0, reference: false), sps));

        // A non-reference picture does not move the reference point: 6 after 2 is still msb 16.
        Assert.Equal(22, order.Next(Header(lsb: 6), sps));
        Assert.Equal(0, order.Next(Header(idr: true, lsb: 0), sps));
    }

    [Fact]
    public void Type0_TakesTheEarlierFieldAsTheFrameOrder()
    {
        var sps = new SequenceParameterSet { PicOrderCntType = 0, Log2MaxPicOrderCntLsb = 8, Log2MaxFrameNum = 4 };
        var order = new PictureOrder();
        order.Next(Header(idr: true, lsb: 0), sps);
        Assert.Equal(7, order.Next(Header(lsb: 10, deltaBottom: -3), sps));
    }

    [Fact]
    public void Type1_AddsTheCycleOffsets()
    {
        // A cycle of two reference frames, 4 and then 2 apart; non-reference pictures one step back.
        var sps = new SequenceParameterSet
        {
            PicOrderCntType = 1,
            Log2MaxFrameNum = 4,
            OffsetForRefFrame = [4, 2],
            OffsetForNonRefPic = -1,
        };
        var order = new PictureOrder();

        Assert.Equal(0, order.Next(Header(idr: true, frameNum: 0), sps));
        Assert.Equal(4, order.Next(Header(frameNum: 1), sps));                    // absFrameNum 1: first offset
        Assert.Equal(3, order.Next(Header(frameNum: 2, reference: false), sps)); // absFrameNum 2 - 1: 4, then -1
        Assert.Equal(6, order.Next(Header(frameNum: 2), sps));                    // absFrameNum 2: 4 + 2
        Assert.Equal(10, order.Next(Header(frameNum: 3), sps));                   // second cycle: 6 + 4
        Assert.Equal(11, order.Next(Header(frameNum: 4, delta0: -1), sps));       // 6 + 6, plus delta_pic_order_cnt[0]
    }

    [Fact]
    public void Type2_IsTwiceTheFrameNumberAcrossWraparound()
    {
        var sps = new SequenceParameterSet { PicOrderCntType = 2, Log2MaxFrameNum = 4 };
        var order = new PictureOrder();

        Assert.Equal(0, order.Next(Header(idr: true, frameNum: 0), sps));
        Assert.Equal(2, order.Next(Header(frameNum: 1), sps));
        Assert.Equal(3, order.Next(Header(frameNum: 2, reference: false), sps));
        Assert.Equal(4, order.Next(Header(frameNum: 2), sps));
        for (int frameNum = 3; frameNum < 16; frameNum++)
        {
            order.Next(Header(frameNum: frameNum), sps);
        }

        Assert.Equal(32, order.Next(Header(frameNum: 0), sps)); // FrameNumOffset 16 after the wrap
    }

    [Fact]
    public void MemoryManagementOperation5_StartsTheCountOver()
    {
        var sps = new SequenceParameterSet { PicOrderCntType = 2, Log2MaxFrameNum = 4 };
        var order = new PictureOrder();
        order.Next(Header(idr: true, frameNum: 0), sps);
        order.Next(Header(frameNum: 1), sps);

        Assert.Equal(0, order.Next(Header(frameNum: 2, mmco5: true), sps));
        Assert.Equal(2, order.Next(Header(frameNum: 1), sps)); // frame_num counts from 0 again

        var type0 = new SequenceParameterSet { PicOrderCntType = 0, Log2MaxPicOrderCntLsb = 4, Log2MaxFrameNum = 4 };
        order = new PictureOrder();
        order.Next(Header(idr: true, lsb: 0), type0);
        Assert.Equal(0, order.Next(Header(lsb: 6, mmco5: true), type0));
        Assert.Equal(2, order.Next(Header(lsb: 2), type0));
    }

    private static SliceHeader Header(bool idr = false, int frameNum = 0, int lsb = 0, bool reference = true, int deltaBottom = 0, int delta0 = 0, bool mmco5 = false) => new()
    {
        IsIdr = idr,
        FrameNum = frameNum,
        PicOrderCntLsb = lsb,
        NalRefIdc = reference ? 1 : 0,
        DeltaPicOrderCntBottom = deltaBottom,
        DeltaPicOrderCnt0 = delta0,
        MemoryManagement = mmco5 ? [new MemoryManagementOperation(5, 0, 0, 0, 0)] : [],
    };
}

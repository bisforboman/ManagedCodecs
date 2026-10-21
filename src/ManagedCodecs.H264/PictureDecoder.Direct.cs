namespace ManagedCodecs.H264;

/// <summary>Direct prediction of B_Skip, B_Direct_16x16 and B_Direct_8x8 (H.264 clause 8.4.1.2).</summary>
internal sealed partial class PictureDecoder
{
    /// <summary>
    /// Derives and stores the motion of 8x8 blocks <paramref name="first"/> .. first + count - 1
    /// of a macroblock in direct mode, spatial or temporal as the slice says.
    /// </summary>
    private void DirectMotion(SliceState slice, int mbAddr, int first, int count)
    {
        if (slice.RefList1[0].IsNonExisting)
        {
            throw new InvalidDataException("The co-located picture is a frame inferred for a gap in frame_num (8.2.5.2).");
        }

        for (int i = first; i < first + count; i++)
        {
            _direct[(mbAddr * 4) + i] = true;
        }

        if (slice.Header.DirectSpatialMvPred)
        {
            SpatialDirect(slice, mbAddr, first, count);
        }
        else
        {
            TemporalDirect(slice, mbAddr, first, count);
        }
    }

    /// <summary>
    /// 8.4.1.2.2: reference indices and motion vectors predicted from the neighbouring macroblocks
    /// (as for a 16x16 partition), set to zero motion in blocks whose co-located block is still.
    /// </summary>
    private void SpatialDirect(SliceState slice, int mbAddr, int first, int count)
    {
        int ref0 = SpatialDirectRefIdx(0, mbAddr);
        int ref1 = SpatialDirectRefIdx(1, mbAddr);
        (int X, int Y) mv0 = (0, 0);
        (int X, int Y) mv1 = (0, 0);
        bool zero = ref0 < 0 && ref1 < 0;
        if (zero)
        {
            ref0 = 0;
            ref1 = 0;
        }
        else
        {
            if (ref0 >= 0)
            {
                mv0 = PredictMv(0, mbAddr, 0, 0, 16, 16, ref0, PartitionShape.Other);
            }

            if (ref1 >= 0)
            {
                mv1 = PredictMv(1, mbAddr, 0, 0, 16, 16, ref1, PartitionShape.Other);
            }
        }

        var colPic = slice.RefList1[0];
        int size = Sps.Direct8x8Inference ? 8 : 4;
        foreach (var (x, y) in DirectBlocks(first, count, size))
        {
            // colZeroFlag: a short-term co-located picture whose block barely moves, from its own reference 0.
            bool colZero = false;
            if (!zero && colPic.IsShortTermReference && (ref0 == 0 || ref1 == 0))
            {
                var col = CoLocated(colPic, mbAddr, x / 4, y / 4);
                colZero = col.RefIdx == 0 && col.MvX is >= -1 and <= 1 && col.MvY is >= -1 and <= 1;
            }

            if (ref0 >= 0)
            {
                bool still = ref0 == 0 && colZero;
                StoreMotion(slice, 0, mbAddr, x, y, size, size, ref0, still ? 0 : mv0.X, still ? 0 : mv0.Y);
            }

            if (ref1 >= 0)
            {
                bool still = ref1 == 0 && colZero;
                StoreMotion(slice, 1, mbAddr, x, y, size, size, ref1, still ? 0 : mv1.X, still ? 0 : mv1.Y);
            }
        }
    }

    /// <summary>refIdxLX of spatial direct prediction: MinPositive over the neighbours A, B and C (or D).</summary>
    private int SpatialDirectRefIdx(int list, int mbAddr)
    {
        var a = MotionNeighbor(list, mbAddr, -1, 0, 0);
        var b = MotionNeighbor(list, mbAddr, 0, -1, 0);
        var c = MotionNeighbor(list, mbAddr, 16, -1, 0);
        if (!c.Available)
        {
            c = MotionNeighbor(list, mbAddr, -1, -1, 0);
        }

        return MinPositive(a.RefIdx, MinPositive(b.RefIdx, c.RefIdx));
    }

    private static int MinPositive(int x, int y) => x >= 0 && y >= 0 ? Math.Min(x, y) : Math.Max(x, y);

    /// <summary>
    /// 8.4.1.2.3: the co-located block's motion vector, scaled by the order count distances, gives
    /// both lists; list 0 refers to the picture the co-located block referred to.
    /// </summary>
    private void TemporalDirect(SliceState slice, int mbAddr, int first, int count)
    {
        var colPic = slice.RefList1[0];
        var refList0 = slice.RefList0;
        int size = Sps.Direct8x8Inference ? 8 : 4;
        foreach (var (x, y) in DirectBlocks(first, count, size))
        {
            var col = CoLocated(colPic, mbAddr, x / 4, y / 4);
            int ref0 = 0;
            if (col.RefIdx >= 0)
            {
                // The lowest index in list 0 of the picture the co-located block referred to.
                ref0 = -1;
                for (int i = 0; i < refList0.Count && ref0 < 0; i++)
                {
                    if (refList0[i].Id == col.RefId)
                    {
                        ref0 = i;
                    }
                }

                if (ref0 < 0)
                {
                    throw new InvalidDataException("Temporal direct: the co-located block's reference is not in list 0.");
                }
            }

            var pic0 = refList0[ref0];
            var pic1 = colPic;
            int mv0X;
            int mv0Y;
            int mv1X;
            int mv1Y;
            if (pic0.IsLongTermReference || pic1.Poc - pic0.Poc == 0)
            {
                (mv0X, mv0Y, mv1X, mv1Y) = (col.MvX, col.MvY, 0, 0);
            }
            else
            {
                int tb = Math.Clamp(Picture.Poc - pic0.Poc, -128, 127);
                int td = Math.Clamp(pic1.Poc - pic0.Poc, -128, 127);
                int tx = (16384 + Math.Abs(td / 2)) / td;
                int distScaleFactor = Math.Clamp(((tb * tx) + 32) >> 6, -1024, 1023);
                mv0X = ((distScaleFactor * col.MvX) + 128) >> 8;
                mv0Y = ((distScaleFactor * col.MvY) + 128) >> 8;
                mv1X = mv0X - col.MvX;
                mv1Y = mv0Y - col.MvY;
            }

            StoreMotion(slice, 0, mbAddr, x, y, size, size, ref0, mv0X, mv0Y);
            StoreMotion(slice, 1, mbAddr, x, y, size, size, 0, mv1X, mv1Y);
        }
    }

    private readonly record struct CoLocatedMotion(int RefIdx, int MvX, int MvY, long RefId);

    /// <summary>
    /// The co-located block of 4x4 block (bx, by) in the first list 1 picture (8.4.1.2.1): its
    /// list 0 motion, or list 1 where it does not use list 0; refIdx -1 in intra macroblocks. With
    /// direct_8x8_inference_flag each 8x8 block takes the motion of its corner 4x4 block.
    /// </summary>
    private CoLocatedMotion CoLocated(Picture colPic, int mbAddr, int bx, int by)
    {
        if (Sps.Direct8x8Inference)
        {
            bx = (bx >> 1) * 3;
            by = (by >> 1) * 3;
        }

        int part = (mbAddr * 4) + ((by >> 1) * 2) + (bx >> 1);
        int block = (mbAddr * 16) + (by * 4) + bx;
        int list = colPic.RefIdx[0][part] >= 0 ? 0 : 1;
        int refIdx = colPic.RefIdx[list][part];
        return refIdx < 0
            ? new CoLocatedMotion(-1, 0, 0, -1)
            : new CoLocatedMotion(refIdx, colPic.MvX[list][block], colPic.MvY[list][block], colPic.RefId[list][part]);
    }

    /// <summary>Top-left luma positions in the macroblock of the size x size blocks of 8x8 blocks first .. first + count - 1.</summary>
    private static DirectBlockList DirectBlocks(int first, int count, int size) => new(first, count, size);

    /// <summary>The positions of <see cref="DirectBlocks"/>, enumerated without allocating.</summary>
    private struct DirectBlockList(int first, int count, int size)
    {
        private readonly int _perBlock = size == 8 ? 1 : 4;
        private int _index = -1;

        public (int X, int Y) Current { get; private set; }

        public readonly DirectBlockList GetEnumerator() => this;

        public bool MoveNext()
        {
            if (++_index >= count * _perBlock)
            {
                return false;
            }

            int block = first + (_index / _perBlock);
            int k = _index % _perBlock;
            Current = (((block & 1) * 8) + ((k & 1) * size), ((block >> 1) * 8) + ((k >> 1) * size));
            return true;
        }
    }

    /// <summary>Motion compensation of direct predicted 8x8 blocks, in the units their motion was derived in.</summary>
    private void PredictDirect(SliceState slice, int mbAddr, int first, int count)
    {
        int size = Sps.Direct8x8Inference ? 8 : 4;
        foreach (var (x, y) in DirectBlocks(first, count, size))
        {
            PredictPartition(slice, mbAddr, x, y, size, size);
        }
    }
}

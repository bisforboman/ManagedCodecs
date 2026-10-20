namespace ManagedCodecs.H264;

/// <summary>Inter prediction for P slices (H.264 clause 8.4).</summary>
internal sealed partial class PictureDecoder
{
    // Partitions of P_L0_16x16, P_L0_L0_16x8 and P_L0_L0_8x16 (Table 7-13) as (x, y, width, height).
    private static readonly (int X, int Y, int W, int H)[][] MbPartitions =
    [
        [(0, 0, 16, 16)],
        [(0, 0, 16, 8), (0, 8, 16, 8)],
        [(0, 0, 8, 16), (8, 0, 8, 16)],
    ];

    // Sub-macroblock partitions of P_L0_8x8, P_L0_8x4, P_L0_4x8 and P_L0_4x4 (Table 7-17).
    private static readonly (int X, int Y, int W, int H)[][] SubPartitions =
    [
        [(0, 0, 8, 8)],
        [(0, 0, 8, 4), (0, 4, 8, 4)],
        [(0, 0, 4, 8), (4, 0, 4, 8)],
        [(0, 0, 4, 4), (4, 0, 4, 4), (0, 4, 4, 4), (4, 4, 4, 4)],
    ];

    // Per luma 4x4 block: motion vector in quarter samples.
    private short[] _mvX = [];
    private short[] _mvY = [];

    // Per 8x8 block: reference index into the slice's list (-1 = none) and the picture itself,
    // which is what the deblocking filter compares.
    private sbyte[] _refIdx = [];
    private Picture?[] _refPic = [];

    private enum PartitionShape
    {
        Other,
        Wide16x8,
        Tall8x16,
    }

    private void InitInter()
    {
        _mvX = new short[_mbCount * 16];
        _mvY = new short[_mbCount * 16];
        _refIdx = new sbyte[_mbCount * 4];
        _refPic = new Picture?[_mbCount * 4];
        Array.Fill(_refIdx, (sbyte)-1);
    }

    /// <summary>Resets the motion data of an intra macroblock.</summary>
    private void ClearInter(int mbAddr)
    {
        _mvX.AsSpan(mbAddr * 16, 16).Clear();
        _mvY.AsSpan(mbAddr * 16, 16).Clear();
        _refIdx.AsSpan(mbAddr * 4, 4).Fill(-1);
        _refPic.AsSpan(mbAddr * 4, 4).Clear();
    }

    /// <summary>P_Skip (8.4.1.1): reference 0, predicted motion, no residual.</summary>
    private void DecodeSkip(SliceState slice, int mbAddr)
    {
        StartInterMacroblock(slice, mbAddr, MbKind.PSkip);
        ClearCounts(mbAddr);
        _qp[mbAddr] = slice.Qp;

        var a = MotionNeighbor(mbAddr, -1, 0, 0);
        var b = MotionNeighbor(mbAddr, 0, -1, 0);
        (int X, int Y) mv = (0, 0);
        if (a.Available && b.Available && !(a.RefIdx == 0 && a.MvX == 0 && a.MvY == 0) && !(b.RefIdx == 0 && b.MvX == 0 && b.MvY == 0))
        {
            mv = PredictMv(mbAddr, 0, 0, 16, 16, 0, PartitionShape.Other);
        }

        SetMotion(slice, mbAddr, 0, 0, 16, 16, 0, mv.X, mv.Y);
    }

    /// <summary>P macroblock types 0 to 4 (Table 7-13): mb_pred or sub_mb_pred, then residual.</summary>
    private void DecodeInterMacroblock(SliceState slice, BitReader r, int mbAddr, int mbType)
    {
        StartInterMacroblock(slice, mbAddr, MbKind.Inter);
        int maxRef = slice.Header.NumRefIdxL0Active - 1;

        if (mbType < 3)
        {
            var partitions = MbPartitions[mbType];
            var shape = (PartitionShape)mbType;
            Span<int> refs = stackalloc int[partitions.Length];
            for (int p = 0; p < partitions.Length; p++)
            {
                refs[p] = ReadRefIdx(r, maxRef);
            }

            for (int p = 0; p < partitions.Length; p++)
            {
                var (x, y, w, h) = partitions[p];
                DecodeMotion(slice, r, mbAddr, x, y, w, h, refs[p], shape);
            }
        }
        else
        {
            // P_8x8 and P_8x8ref0 (where every ref_idx is inferred to be 0).
            Span<int> subTypes = stackalloc int[4];
            for (int i = 0; i < 4; i++)
            {
                subTypes[i] = r.ReadUe(3, "sub_mb_type");
            }

            Span<int> refs = stackalloc int[4];
            for (int i = 0; i < 4; i++)
            {
                refs[i] = mbType == 3 ? ReadRefIdx(r, maxRef) : 0;
            }

            for (int i = 0; i < 4; i++)
            {
                int x8 = (i & 1) * 8;
                int y8 = (i >> 1) * 8;
                foreach (var (x, y, w, h) in SubPartitions[subTypes[i]])
                {
                    DecodeMotion(slice, r, mbAddr, x8 + x, y8 + y, w, h, refs[i], PartitionShape.Other);
                }
            }
        }

        int cbp = CavlcTables.CodedBlockPatternInter[r.ReadUe(47, "coded_block_pattern")];
        int cbpLuma = cbp & 15;
        int cbpChroma = cbp >> 4;

        var residual = new Residual();
        if (cbp != 0)
        {
            UpdateQp(slice, r);
            ReadResidual(r, mbAddr, residual, is16x16: false, cbpLuma, cbpChroma);
        }
        else
        {
            ClearCounts(mbAddr);
        }

        _qp[mbAddr] = slice.Qp;

        // The prediction is already in the picture; add the residual.
        var (origin, _) = Origins(mbAddr);
        int stride = Picture.LumaStride;
        for (int i = 0; i < 16; i++)
        {
            int x = i & 3;
            int y = i >> 2;
            if ((cbpLuma & (1 << (((y >> 1) * 2) + (x >> 1)))) == 0)
            {
                continue;
            }

            var block = residual.Luma[i];
            Transform.Dequantize(block, slice.Qp, skipDc: false);
            Transform.InverseTransformAdd(block, Picture.Y.AsSpan(origin + (y * 4 * stride) + (x * 4)), stride);
        }

        ReconstructChroma(mbAddr, residual, cbpChroma, slice.Pps.ChromaQpIndexOffset, intraMode: -1, default);
    }

    private void StartInterMacroblock(SliceState slice, int mbAddr, MbKind kind)
    {
        _sliceOfMb[mbAddr] = slice.Number;
        DecodedMbCount++;
        _kind[mbAddr] = kind;
        _intra4x4Mode.AsSpan(mbAddr * 16, 16).Fill(-1);
        ClearInter(mbAddr);
    }

    private static int ReadRefIdx(BitReader r, int maxRef)
    {
        if (maxRef == 0)
        {
            return 0;
        }

        uint refIdx = r.ReadTe(maxRef);
        if (refIdx > (uint)maxRef)
        {
            throw new InvalidDataException($"ref_idx_l0 {refIdx} is out of range.");
        }

        return (int)refIdx;
    }

    /// <summary>Reads one mvd pair, adds the prediction, stores the motion and predicts the samples.</summary>
    private void DecodeMotion(SliceState slice, BitReader r, int mbAddr, int x, int y, int w, int h, int refIdx, PartitionShape shape)
    {
        int mvdX = r.ReadSe();
        int mvdY = r.ReadSe();
        var (predX, predY) = PredictMv(mbAddr, x, y, w, h, refIdx, shape);
        SetMotion(slice, mbAddr, x, y, w, h, refIdx, predX + mvdX, predY + mvdY);
    }

    /// <summary>Stores a partition's motion for later neighbours and the deblocking filter, then predicts it.</summary>
    private void SetMotion(SliceState slice, int mbAddr, int x, int y, int w, int h, int refIdx, int mvX, int mvY)
    {
        if (refIdx >= slice.RefList.Count)
        {
            throw new InvalidDataException($"ref_idx_l0 {refIdx} names no reference picture.");
        }

        if (mvX is < short.MinValue or > short.MaxValue || mvY is < short.MinValue or > short.MaxValue)
        {
            throw new InvalidDataException("Motion vector out of range.");
        }

        var reference = slice.RefList[refIdx];
        for (int by = y / 4; by < (y + h) / 4; by++)
        {
            for (int bx = x / 4; bx < (x + w) / 4; bx++)
            {
                _mvX[(mbAddr * 16) + (by * 4) + bx] = (short)mvX;
                _mvY[(mbAddr * 16) + (by * 4) + bx] = (short)mvY;
                int part = (mbAddr * 4) + ((by >> 1) * 2) + (bx >> 1);
                _refIdx[part] = (sbyte)refIdx;
                _refPic[part] = reference;
            }
        }

        int mbX = mbAddr % _widthInMbs;
        int mbY = mbAddr / _widthInMbs;
        int lumaX = (mbX * 16) + x;
        int lumaY = (mbY * 16) + y;
        InterPrediction.PredictLuma(reference, Picture.Y, Picture.LumaStride, lumaX, lumaY, w, h, mvX, mvY);
        InterPrediction.PredictChroma(reference, 1, Picture.Cb, Picture.ChromaStride, lumaX / 2, lumaY / 2, w / 2, h / 2, mvX, mvY);
        InterPrediction.PredictChroma(reference, 2, Picture.Cr, Picture.ChromaStride, lumaX / 2, lumaY / 2, w / 2, h / 2, mvX, mvY);
    }

    private readonly record struct Motion(bool Available, int RefIdx, int MvX, int MvY);

    /// <summary>
    /// Motion of the partition covering luma location (x, y) relative to the current macroblock
    /// (6.4.11.7). Inside the current macroblock a block is only available when it comes before
    /// <paramref name="currentBlock"/> (the decoding index of the current partition's first 4x4
    /// block). Intra macroblocks are available but have no reference (refIdx -1, zero motion).
    /// </summary>
    private Motion MotionNeighbor(int mbAddr, int x, int y, int currentBlock)
    {
        int n = NeighborMb(mbAddr, x, y);
        if (n < 0)
        {
            return new Motion(false, -1, 0, 0);
        }

        int bx = ((x + 16) % 16) / 4;
        int by = ((y + 16) % 16) / 4;
        if (n == mbAddr && BlockIndex(bx, by) >= currentBlock)
        {
            return new Motion(false, -1, 0, 0);
        }

        int block = (n * 16) + (by * 4) + bx;
        return new Motion(true, _refIdx[(n * 4) + ((by >> 1) * 2) + (bx >> 1)], _mvX[block], _mvY[block]);
    }

    /// <summary>Luma motion vector prediction (8.4.1.3).</summary>
    private (int X, int Y) PredictMv(int mbAddr, int x, int y, int w, int h, int refIdx, PartitionShape shape)
    {
        int current = BlockIndex(x / 4, y / 4);
        var a = MotionNeighbor(mbAddr, x - 1, y, current);
        var b = MotionNeighbor(mbAddr, x, y - 1, current);
        var c = MotionNeighbor(mbAddr, x + w, y - 1, current);
        if (!c.Available)
        {
            c = MotionNeighbor(mbAddr, x - 1, y - 1, current); // D replaces C
        }

        // Directional prediction for 16x8 and 8x16 partitions (8.4.1.3).
        if (shape == PartitionShape.Wide16x8)
        {
            if (y == 0 && b.RefIdx == refIdx)
            {
                return (b.MvX, b.MvY);
            }

            if (y == 8 && a.RefIdx == refIdx)
            {
                return (a.MvX, a.MvY);
            }
        }
        else if (shape == PartitionShape.Tall8x16)
        {
            if (x == 0 && a.RefIdx == refIdx)
            {
                return (a.MvX, a.MvY);
            }

            if (x == 8 && c.RefIdx == refIdx)
            {
                return (c.MvX, c.MvY);
            }
        }

        // Median prediction (8.4.1.3.1).
        if (!b.Available && !c.Available && a.Available)
        {
            b = a;
            c = a;
        }

        int matches = (a.RefIdx == refIdx ? 1 : 0) + (b.RefIdx == refIdx ? 1 : 0) + (c.RefIdx == refIdx ? 1 : 0);
        if (matches == 1)
        {
            var only = a.RefIdx == refIdx ? a : b.RefIdx == refIdx ? b : c;
            return (only.MvX, only.MvY);
        }

        return (Median(a.MvX, b.MvX, c.MvX), Median(a.MvY, b.MvY, c.MvY));
    }

    private static int Median(int a, int b, int c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
}

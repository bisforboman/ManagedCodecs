using System.Numerics;
using System.Runtime.InteropServices;

namespace ManagedCodecs.H264;

/// <summary>Inter prediction for P and B slices (H.264 clause 8.4).</summary>
internal sealed partial class PictureDecoder<TPixel>
{
    // Partitions of the 16x16, 16x8 and 8x16 macroblock types (Tables 7-13, 7-14) as (x, y, width, height).
    private static readonly (int X, int Y, int W, int H)[][] MbPartitions =
    [
        [(0, 0, 16, 16)],
        [(0, 0, 16, 8), (0, 8, 16, 8)],
        [(0, 0, 8, 16), (8, 0, 8, 16)],
    ];

    // Sub-macroblock partitions of the 8x8, 8x4, 4x8 and 4x4 sub-macroblock types (Tables 7-17, 7-18).
    private static readonly (int X, int Y, int W, int H)[][] SubPartitions =
    [
        [(0, 0, 8, 8)],
        [(0, 0, 8, 4), (0, 4, 8, 4)],
        [(0, 0, 4, 8), (4, 0, 4, 8)],
        [(0, 0, 4, 4), (4, 0, 4, 4), (0, 4, 4, 4), (4, 4, 4, 4)],
    ];

    // B macroblock types 0 to 21 (Table 7-14): shape (index into MbPartitions) and the lists each
    // partition predicts from (bit 0 list 0, bit 1 list 1). Type 0 is B_Direct_16x16.
    private static readonly (int Shape, int Pred0, int Pred1)[] BMbTypes =
    [
        (0, 0, 0),
        (0, 1, 0), (0, 2, 0), (0, 3, 0),
        (1, 1, 1), (2, 1, 1), (1, 2, 2), (2, 2, 2),
        (1, 1, 2), (2, 1, 2), (1, 2, 1), (2, 2, 1),
        (1, 1, 3), (2, 1, 3), (1, 2, 3), (2, 2, 3),
        (1, 3, 1), (2, 3, 1), (1, 3, 2), (2, 3, 2),
        (1, 3, 3), (2, 3, 3),
    ];

    // B sub-macroblock types 0 to 12 (Table 7-18): shape (index into SubPartitions) and lists.
    // Type 0 is B_Direct_8x8.
    private static readonly (int Shape, int Pred)[] BSubMbTypes =
    [
        (0, 0),
        (0, 1), (0, 2), (0, 3),
        (1, 1), (2, 1), (1, 2), (2, 2), (1, 3), (2, 3),
        (3, 1), (3, 2), (3, 3),
    ];

    // Per list (0, 1) and luma 4x4 block: motion vector in quarter samples; per list and 8x8 block:
    // reference index (-1 = list not used) and the referenced picture's Id. These are the current
    // picture's own arrays (Picture.MvX ...), kept for direct prediction of later pictures.
    private short[][] _mvX = [[], []];
    private short[][] _mvY = [[], []];
    private sbyte[][] _refIdx = [[], []];
    private long[][] _refId = [[], []];
    private sbyte[][] _refParity = [[], []];

    // Per list and 8x8 block: the referenced picture, for motion compensation and deblocking.
    private Picture?[][] _refPic = [[], []];

    // Per 8x8 block: predicted in direct mode (B_Skip, B_Direct_16x16, B_Direct_8x8).
    private bool[] _direct = [];

    // Bi-prediction: each list's prediction of one partition before they are averaged.
    private readonly TPixel[] _predictionL0 = new TPixel[16 * 16];
    private readonly TPixel[] _predictionL1 = new TPixel[16 * 16];

    private enum PartitionShape
    {
        Other,
        Wide16x8,
        Tall8x16,
    }

    private void InitInter()
    {
        _refPic = [new Picture?[_mbCount * 4], new Picture?[_mbCount * 4]];
        _direct = new bool[_mbCount * 4];
    }

    /// <summary>
    /// Takes the picture's motion arrays as the ones to fill, and clears all motion state. Each
    /// macroblock is decoded once, so a macroblock without motion (intra, or a list it does not
    /// use) keeps these values: refIdx -1, zero motion, no picture.
    /// </summary>
    private void ResetInter(Picture picture)
    {
        _mvX = picture.MvX;
        _mvY = picture.MvY;
        _refIdx = picture.RefIdx;
        _refId = picture.RefId;
        _refParity = picture.RefParity;
        for (int list = 0; list < 2; list++)
        {
            Array.Clear(_mvX[list]);
            Array.Clear(_mvY[list]);
            Array.Fill(_refIdx[list], (sbyte)-1);
            Array.Clear(_refId[list]);
            Array.Clear(_refPic[list]);
        }

        Array.Clear(_direct);
    }

    /// <summary>P_Skip (8.4.1.1: reference 0, predicted motion) or B_Skip (direct prediction); no residual.</summary>
    private void DecodeSkip(SliceState slice, int mbAddr)
    {
        StartInterMacroblock(slice, mbAddr, MbKind.PSkip);
        ClearCounts(mbAddr);
        _qp[mbAddr] = slice.Qp;
        slice.LastQpDeltaNonZero = false;

        if (slice.Header.Type == SliceType.B)
        {
            DirectMotion(slice, mbAddr, 0, 4);
            PredictDirect(slice, mbAddr, 0, 4);
            return;
        }

        var a = MotionNeighbor(0, mbAddr, -1, 0, 0);
        var b = MotionNeighbor(0, mbAddr, 0, -1, 0);
        (int X, int Y) mv = (0, 0);
        if (a.Available && b.Available && !(a.RefIdx == 0 && a.MvX == 0 && a.MvY == 0) && !(b.RefIdx == 0 && b.MvX == 0 && b.MvY == 0))
        {
            mv = PredictMv(0, mbAddr, 0, 0, 16, 16, 0, PartitionShape.Other);
        }

        StoreMotion(slice, 0, mbAddr, 0, 0, 16, 16, 0, mv.X, mv.Y);
        PredictPartition(slice, mbAddr, 0, 0, 16, 16);
    }

    /// <summary>
    /// An inter macroblock of a P slice (types 0 to 4, Table 7-13) or B slice (0 to 22, Table 7-14):
    /// mb_pred or sub_mb_pred, the prediction, then the residual.
    /// </summary>
    private void DecodeInterMacroblock(SliceState slice, BitReader r, int mbAddr, int mbType)
    {
        StartInterMacroblock(slice, mbAddr, MbKind.Inter);
        bool isB = slice.Header.Type == SliceType.B;

        // A field macroblock refers to fields: twice as many as the frames (8.4.2.1).
        int fieldScale = IsMbaffFieldMb(mbAddr) ? 2 : 1;
        int maxRef0 = (slice.Header.NumRefIdxL0Active * fieldScale) - 1;
        int maxRef1 = (slice.Header.NumRefIdxL1Active * fieldScale) - 1;

        // Whether transform_size_8x8_flag may follow coded_block_pattern: no partitions below 8x8 (7.3.5).
        bool noPartitionBelow8x8 = true;
        if (isB && mbType == 0)
        {
            noPartitionBelow8x8 = Sps.Direct8x8Inference;

            // B_Direct_16x16.
            _directMb[mbAddr] = true;
            DirectMotion(slice, mbAddr, 0, 4);
            PredictDirect(slice, mbAddr, 0, 4);
        }
        else if (isB ? mbType < 22 : mbType < 3)
        {
            var (shapeIndex, pred0, pred1) = isB ? BMbTypes[mbType] : (mbType, 1, 1);
            var partitions = MbPartitions[shapeIndex];
            var shape = (PartitionShape)shapeIndex;
            Span<int> preds = [pred0, pred1];
            Span<int> refs = [-1, -1, -1, -1]; // list * 2 + partition

            // All ref_idx_l0, all ref_idx_l1, all mvd_l0, all mvd_l1 (7.3.5.1).
            for (int list = 0; list < 2; list++)
            {
                for (int p = 0; p < partitions.Length; p++)
                {
                    if ((preds[p] & (1 << list)) != 0)
                    {
                        var (x, y, w, h) = partitions[p];
                        refs[(list * 2) + p] = ReadRefIdx(slice, r, list, mbAddr, x, y, w, h, list == 0 ? maxRef0 : maxRef1);
                    }
                }
            }

            for (int list = 0; list < 2; list++)
            {
                for (int p = 0; p < partitions.Length; p++)
                {
                    if ((preds[p] & (1 << list)) != 0)
                    {
                        var (x, y, w, h) = partitions[p];
                        DecodeMotion(slice, r, list, mbAddr, x, y, w, h, refs[(list * 2) + p], shape);
                    }
                }
            }

            foreach (var (x, y, w, h) in partitions)
            {
                PredictPartition(slice, mbAddr, x, y, w, h);
            }
        }
        else
        {
            noPartitionBelow8x8 = DecodeSubMacroblocks(slice, r, mbAddr, isB, refIdxZero: !isB && mbType == 4, maxRef0, maxRef1);
        }

        DecodeInterResidual(slice, r, mbAddr, noPartitionBelow8x8);
    }

    /// <summary>
    /// P_8x8 / P_8x8ref0 / B_8x8: four sub_mb_types, then ref_idx_l0, ref_idx_l1, mvd_l0 and mvd_l1
    /// for the sub-macroblocks that use each list. B_Direct_8x8 sub-macroblocks are direct predicted.
    /// Returns noSubMbPartSizeLessThan8x8Flag (7.3.5).
    /// </summary>
    private bool DecodeSubMacroblocks(SliceState slice, BitReader r, int mbAddr, bool isB, bool refIdxZero, int maxRef0, int maxRef1)
    {
        Span<int> subTypes = stackalloc int[4];
        for (int i = 0; i < 4; i++)
        {
            subTypes[i] = slice.Cabac is not { } c ? r.ReadUe(isB ? 12 : 3, "sub_mb_type")
                : isB ? CabacBSubMbType(c)
                : CabacSubMbType(c);
        }

        // Shape and lists per sub-macroblock; shape -1 marks B_Direct_8x8.
        Span<int> shapes = stackalloc int[4];
        Span<int> preds = stackalloc int[4];
        for (int i = 0; i < 4; i++)
        {
            (shapes[i], preds[i]) = !isB ? (subTypes[i], 1) : subTypes[i] == 0 ? (-1, 0) : BSubMbTypes[subTypes[i]];
            if (shapes[i] < 0)
            {
                // Known before any other partition's motion vector prediction can look at it.
                DirectMotion(slice, mbAddr, i, 1);
            }
        }

        Span<int> refs = [-1, -1, -1, -1, -1, -1, -1, -1]; // list * 4 + sub-macroblock
        for (int list = 0; list < 2; list++)
        {
            for (int i = 0; i < 4; i++)
            {
                if ((preds[i] & (1 << list)) != 0)
                {
                    refs[(list * 4) + i] = refIdxZero ? 0 : ReadRefIdx(slice, r, list, mbAddr, (i & 1) * 8, (i >> 1) * 8, 8, 8, list == 0 ? maxRef0 : maxRef1);
                }
            }
        }

        for (int list = 0; list < 2; list++)
        {
            for (int i = 0; i < 4; i++)
            {
                if ((preds[i] & (1 << list)) != 0)
                {
                    foreach (var (x, y, w, h) in SubPartitions[shapes[i]])
                    {
                        DecodeMotion(slice, r, list, mbAddr, ((i & 1) * 8) + x, ((i >> 1) * 8) + y, w, h, refs[(list * 4) + i], PartitionShape.Other);
                    }
                }
            }
        }

        bool noPartitionBelow8x8 = true;
        for (int i = 0; i < 4; i++)
        {
            if (shapes[i] < 0)
            {
                noPartitionBelow8x8 &= Sps.Direct8x8Inference;
                PredictDirect(slice, mbAddr, i, 1);
                continue;
            }

            noPartitionBelow8x8 &= shapes[i] == 0;
            foreach (var (x, y, w, h) in SubPartitions[shapes[i]])
            {
                PredictPartition(slice, mbAddr, ((i & 1) * 8) + x, ((i >> 1) * 8) + y, w, h);
            }
        }

        return noPartitionBelow8x8;
    }

    /// <summary>
    /// coded_block_pattern, transform_size_8x8_flag, mb_qp_delta and the residual of an inter
    /// macroblock, added to its prediction.
    /// </summary>
    private void DecodeInterResidual(SliceState slice, BitReader r, int mbAddr, bool noPartitionBelow8x8)
    {
        int cbp = slice.Cabac is { } cabac
            ? CabacCbp(cabac, mbAddr)
            : (_noChromaSyntax ? CavlcTables.CodedBlockPatternInter444 : CavlcTables.CodedBlockPatternInter)[r.ReadUe(_noChromaSyntax ? 15 : 47, "coded_block_pattern")];
        int cbpLuma = cbp & 15;
        int cbpChroma = cbp >> 4;
        _cbp[mbAddr] = (byte)cbp;
        if (cbpLuma != 0 && slice.Pps.Transform8x8Mode && noPartitionBelow8x8)
        {
            _transform8x8[mbAddr] = ReadTransformSize8x8Flag(slice, r, mbAddr);
        }

        var residual = _residual;
        if (cbp != 0)
        {
            UpdateQp(slice, r);
            ReadResidual(slice, r, mbAddr, residual, is16x16: false, cbpLuma, cbpChroma);
        }
        else
        {
            ClearCounts(mbAddr);
            slice.LastQpDeltaNonZero = false;
        }

        _qp[mbAddr] = slice.Qp;

        var target = PictureTarget(mbAddr);
        AddInterResidual(slice, mbAddr, target, cbpLuma, 0);
        if (_chroma444)
        {
            AddInterResidual(slice, mbAddr, target, cbpLuma, 1);
            AddInterResidual(slice, mbAddr, target, cbpLuma, 2);
            return;
        }

        if (_monochrome)
        {
            return;
        }

        ReconstructChroma(slice, mbAddr, target, residual, cbpChroma, intraMode: -1, default);
    }

    /// <summary>The residual of an inter macroblock's luma, or in 4:4:4 of its Cb or Cr, added to the prediction.</summary>
    private void AddInterResidual(SliceState slice, int mbAddr, MbTarget target, int cbpLuma, int component)
    {
        int qp = QpPrime(slice, mbAddr, component);
        if (_transform8x8[mbAddr])
        {
            for (int b8 = 0; b8 < 4; b8++)
            {
                AddLuma8x8Residual(slice, mbAddr, target, cbpLuma, b8, qp, component);
            }

            return;
        }

        var (plane, origin, stride) = ComponentOf(target, component);
        var counts = _totalCoeff[component];
        var residual = _residuals[component];
        for (int i = 0; i < 16; i++)
        {
            int x = i & 3;
            int y = i >> 2;
            if ((cbpLuma & (1 << (((y >> 1) * 2) + (x >> 1)))) == 0 || counts[(mbAddr * 16) + i] == 0)
            {
                continue;
            }

            var block = residual.Luma[i];
            var dest = plane.AsSpan(origin + (y * 4 * stride) + (x * 4));
            if (IsLossless(mbAddr))
            {
                Transform.AddBypass(block, 4, 4, BypassDpcm.None, dest, stride, _bitDepth);
                continue;
            }

            Transform.Dequantize(block, qp, skipDc: false, slice.Scaling.LevelScale4x4(3 + component)); // Inter Y, Cb or Cr
            Transform.InverseTransformAdd(block, dest, stride, _bitDepth);
        }
    }

    private void StartInterMacroblock(SliceState slice, int mbAddr, MbKind kind)
    {
        _sliceOfMb[mbAddr] = slice.Number;
        DecodedMbCount++;
        _kind[mbAddr] = kind;
        _transform8x8[mbAddr] = false;
        _intra4x4Mode.AsSpan(mbAddr * 16, 16).Fill(-1);
        _cbp[mbAddr] = 0;
        _dcCoded[mbAddr] = 0;
        _chromaPredMode[mbAddr] = 0;
        _directMb[mbAddr] = kind == MbKind.PSkip && slice.Header.Type == SliceType.B;
    }

    /// <summary>ref_idx_lX of the partition at (x, y), w x h; with CABAC it is stored at once, as later partitions' contexts read it.</summary>
    private int ReadRefIdx(SliceState slice, BitReader r, int list, int mbAddr, int x, int y, int w, int h, int maxRef)
    {
        if (maxRef == 0)
        {
            return 0;
        }

        if (slice.Cabac is { } c)
        {
            int value = CabacRefIdx(c, list, mbAddr, x, y, maxRef);
            for (int by = y / 8; by < (y + h) / 8; by++)
            {
                for (int bx = x / 8; bx < (x + w) / 8; bx++)
                {
                    _refIdx[list][(mbAddr * 4) + (by * 2) + bx] = (sbyte)value;
                }
            }

            return value;
        }

        uint refIdx = r.ReadTe(maxRef);
        if (refIdx > (uint)maxRef)
        {
            throw new InvalidDataException($"ref_idx_l{list} {refIdx} is out of range.");
        }

        return (int)refIdx;
    }

    /// <summary>Reads one mvd pair of list <paramref name="list"/>, adds the prediction and stores the motion.</summary>
    private void DecodeMotion(SliceState slice, BitReader r, int list, int mbAddr, int x, int y, int w, int h, int refIdx, PartitionShape shape)
    {
        int mvdX;
        int mvdY;
        if (slice.Cabac is { } c)
        {
            mvdX = CabacMvd(c, list, mbAddr, x, y, 0);
            mvdY = CabacMvd(c, list, mbAddr, x, y, 1);
            StoreAbsMvd(list, mbAddr, x, y, w, h, mvdX, mvdY);
        }
        else
        {
            mvdX = r.ReadSe();
            mvdY = r.ReadSe();
        }

        var (predX, predY) = PredictMv(list, mbAddr, x, y, w, h, refIdx, shape);
        StoreMotion(slice, list, mbAddr, x, y, w, h, refIdx, predX + mvdX, predY + mvdY);
    }

    /// <summary>Stores one list's motion of a partition, for later neighbours, deblocking and later pictures.</summary>
    private void StoreMotion(SliceState slice, int list, int mbAddr, int x, int y, int w, int h, int refIdx, int mvX, int mvY)
    {
        // A field macroblock's index names a field: of frame refIdx / 2, the same parity as the
        // macroblock for even indices, the other one for odd (8.4.2.1).
        var refList = slice.RefList(list);
        bool field = IsMbaffFieldMb(mbAddr);
        int frameIdx = field ? refIdx >> 1 : refIdx;
        if (frameIdx >= refList.Count)
        {
            throw new InvalidDataException($"ref_idx_l{list} {refIdx} names no reference picture.");
        }

        if (mvX is < short.MinValue or > short.MaxValue || mvY is < short.MinValue or > short.MaxValue)
        {
            throw new InvalidDataException("Motion vector out of range.");
        }

        var reference = refList[frameIdx];
        if (reference.IsNonExisting)
        {
            throw new InvalidDataException($"ref_idx_l{list} {refIdx} names a frame inferred for a gap in frame_num (8.2.5.2).");
        }

        long referenceId = reference.Id;
        if (field)
        {
            reference = reference.Field((mbAddr & 1) ^ (refIdx & 1));
            Statistics.OppositeParity += refIdx & 1;
        }

        FieldStatistics.FromOwnFirstField += _fieldParity >= 0 && reference.Frame == Picture ? 1 : 0;

        // Motion vectors are stored a 4x4 block, references an 8x8 partition: each row and each
        // partition is written once.
        var mvXs = _mvX[list].AsSpan(mbAddr * 16, 16);
        var mvYs = _mvY[list].AsSpan(mbAddr * 16, 16);
        for (int by = y / 4; by < (y + h) / 4; by++)
        {
            mvXs.Slice((by * 4) + (x / 4), w / 4).Fill((short)mvX);
            mvYs.Slice((by * 4) + (x / 4), w / 4).Fill((short)mvY);
        }

        var refIdxs = _refIdx[list].AsSpan(mbAddr * 4, 4);
        var refIds = _refId[list].AsSpan(mbAddr * 4, 4);
        var refParities = _refParity[list].AsSpan(mbAddr * 4, 4);
        var refPics = _refPic[list].AsSpan(mbAddr * 4, 4);
        for (int py = y / 8; py <= (y + h - 1) / 8; py++)
        {
            for (int px = x / 8; px <= (x + w - 1) / 8; px++)
            {
                int part = (py * 2) + px;
                refIdxs[part] = (sbyte)refIdx;
                refIds[part] = referenceId;
                refParities[part] = (sbyte)reference.Parity;
                refPics[part] = reference;
            }
        }
    }

    /// <summary>
    /// Motion compensation of a partition from its stored motion (8.4.2): from one list, or from
    /// both, then weighted sample prediction (8.4.2.3): the default (one list as is, or the
    /// rounded average), explicit weights from the slice's table, or implicit weights from the
    /// order count distances (bi-predicted blocks of B slices with weighted_bipred_idc 2).
    /// </summary>
    private void PredictPartition(SliceState slice, int mbAddr, int x, int y, int w, int h)
    {
        int part = (mbAddr * 4) + ((y >> 3) * 2) + (x >> 3);
        int block = (mbAddr * 16) + ((y >> 2) * 4) + (x >> 2);
        var ref0 = _refPic[0][part];
        var ref1 = _refPic[1][part];

        // Where the partition is, in the coordinates of the references (a field macroblock's are
        // its field's), and where its samples go in the picture.
        var (lumaX, lumaY, lumaOut, lumaStride, chromaOut, chromaStride) = PartitionPlace(mbAddr, x, y);
        bool bi = ref0 is not null && ref1 is not null;
        bool field = IsMbaffFieldMb(mbAddr);
        int refIdx0 = _refIdx[0][part];
        int refIdx1 = _refIdx[1][part];
        var weighting = Weighting(slice, field ? refIdx0 >> 1 : refIdx0, field ? refIdx1 >> 1 : refIdx1, bi, mbAddr, ref0, ref1);

        for (int c = 0; c < (_monochrome ? 1 : 3); c++)
        {
            // A chroma block's size: 4:2:0 halves both sides, 4:2:2 the width, 4:4:4 neither.
            bool lumaLike = c == 0 || _chroma444;
            int cw = lumaLike ? w : w / 2;
            int ch = lumaLike || _mbHeightC == 16 ? h : h / 2;
            var output = Picture.Plane<TPixel>(c).AsSpan(c == 0 ? lumaOut : chromaOut);
            int outputStride = c == 0 ? lumaStride : chromaStride;
            if (!bi)
            {
                int list = ref0 is null ? 1 : 0;
                var reference = (ref0 ?? ref1)!;
                if (weighting.IsDefault)
                {
                    PredictComponent(reference, c, mbAddr, lumaX, lumaY, w, h, _mvX[list][block], _mvY[list][block], output, outputStride);
                    continue;
                }

                PredictComponent(reference, c, mbAddr, lumaX, lumaY, w, h, _mvX[list][block], _mvY[list][block], _predictionL0, 16);
                WeightOne(_predictionL0, 16, cw, ch, output, outputStride, weighting, list, c);
                continue;
            }

            PredictComponent(ref0!, c, mbAddr, lumaX, lumaY, w, h, _mvX[0][block], _mvY[0][block], _predictionL0, 16);
            PredictComponent(ref1!, c, mbAddr, lumaX, lumaY, w, h, _mvX[1][block], _mvY[1][block], _predictionL1, 16);
            if (weighting.IsDefault)
            {
                InterPrediction.AverageBlock(_predictionL0, _predictionL1, 16, cw, ch, output, outputStride);
            }
            else
            {
                WeightBoth(_predictionL0, _predictionL1, 16, cw, ch, output, outputStride, weighting, c);
            }
        }
    }

    /// <summary>
    /// Motion compensation of one component of a w x h luma partition at reference position
    /// (lumaX, lumaY): luma, and in 4:4:4 Cb and Cr, with the luma filter (8.4.2.2.1); 4:2:0 and
    /// 4:2:2 chroma with the chroma one, at its position and size (8.4.2.2.2).
    /// </summary>
    private void PredictComponent(Picture reference, int c, int mbAddr, int lumaX, int lumaY, int w, int h, int mvX, int mvY, Span<TPixel> output, int outputStride)
    {
        // With frame threading the reference may still be being decoded: wait for the lowest row this block reads,
        // three rows below it for the 6-tap filter and one more for the chroma filter's (luma is predicted first).
        if (c == 0)
        {
            reference.WaitForRow(lumaY + (mvY >> 2) + h + 3);
        }

        if (c == 0 || _chroma444)
        {
            InterPrediction.PredictLumaBlock(reference, lumaX, lumaY, w, h, mvX, mvY, output, outputStride, c);
            return;
        }

        // 4:2:2 chroma has the luma's rows: its vertical vector, in eighth samples, is the luma's doubled.
        bool tall = _mbHeightC == 16;
        InterPrediction.PredictChromaBlock(reference, c, lumaX / 2, tall ? lumaY : lumaY / 2, w / 2, tall ? h : h / 2, mvX, ChromaMvY(mvY, mbAddr, reference), output, outputStride);
    }

    /// <summary>
    /// The partition at (x, y) of a macroblock: its top-left luma sample in reference coordinates
    /// (field coordinates for a field macroblock of an MBAFF frame), and the offsets and strides of
    /// its luma and chroma samples in the picture.
    /// </summary>
    private (int LumaX, int LumaY, int LumaOut, int LumaStride, int ChromaOut, int ChromaStride) PartitionPlace(int mbAddr, int x, int y)
    {
        var (luma, lumaStride) = LumaPlace(mbAddr);
        var (chroma, chromaStride) = ChromaPlace(mbAddr);
        int lumaOut = luma + (y * lumaStride) + x;
        int chromaOut = chroma + ((_mbHeightC == 16 ? y : y / 2) * chromaStride) + (_mbWidthC == 16 ? x : x / 2);
        if (!_pairSlots)
        {
            return (((mbAddr % _widthInMbs) * 16) + x, ((mbAddr / _widthInMbs) * 16) + y, lumaOut, lumaStride, chromaOut, chromaStride);
        }

        int pair = mbAddr >> 1;
        int lumaX = ((pair % _widthInMbs) * 16) + x;
        int lumaY = _fieldMb[mbAddr] ? ((pair / _widthInMbs) * 16) + y : ((pair / _widthInMbs) * 32) + ((mbAddr & 1) * 16) + y;
        return (lumaX, lumaY, lumaOut, lumaStride, chromaOut, chromaStride);
    }

    /// <summary>
    /// The vertical chroma vector (8.4.1.4) in eighth chroma samples: the luma one in 4:2:0, plus
    /// the offset of a field predicting from the other parity; in 4:2:2 the luma one doubled
    /// (yIntC and yFracC of 8.4.2.2.2), with no field offset.
    /// </summary>
    private int ChromaMvY(int mvY, int mbAddr, Picture reference) =>
        _mbHeightC == 16 ? mvY * 2 : mvY + ChromaFieldOffset(mbAddr, reference);

    /// <summary>
    /// The vertical chroma offset of a field macroblock predicting from the field of the other
    /// parity (Table 8-9): a quarter chroma sample down from the bottom field, up from the top.
    /// </summary>
    private int ChromaFieldOffset(int mbAddr, Picture reference)
    {
        if (reference.Parity < 0 || reference.Parity == (mbAddr & 1))
        {
            return 0;
        }

        return reference.Parity == 1 ? -2 : 2;
    }

    /// <summary>
    /// The weights of one partition: per list and component (luma, Cb, Cr), with the log2
    /// denominators logWD for luma and chroma. <see cref="IsDefault"/> when they give exactly the
    /// default prediction, so the faster unweighted path can be taken.
    /// </summary>
    private readonly record struct PartitionWeights(bool IsDefault, int LumaLogWd, int ChromaLogWd, PredWeightTable? Table, int RefIdx0, int RefIdx1, int Implicit0, int Implicit1)
    {
        public int LogWd(int component) => component == 0 ? LumaLogWd : ChromaLogWd;

        public int Weight(int list, int component) =>
            Table is null ? (list == 0 ? Implicit0 : Implicit1) : Table.Weight(list, list == 0 ? RefIdx0 : RefIdx1, component);

        public int Offset(int list, int component) =>
            Table is null ? 0 : Table.Offset(list, list == 0 ? RefIdx0 : RefIdx1, component);
    }

    private static readonly PartitionWeights DefaultWeights = new(true, 0, 0, null, 0, 0, 0, 0);

    /// <remarks>
    /// Implicit weights come from the order counts of the partition's references: for a field
    /// macroblock of an MBAFF frame its fields', and the current picture's field of its parity.
    /// </remarks>
    private PartitionWeights Weighting(SliceState slice, int refIdx0, int refIdx1, bool bi, int mbAddr, Picture? ref0, Picture? ref1)
    {
        var header = slice.Header;
        if (header.Weights is { } table)
        {
            // Explicit (8.4.3): the table's entries for the partition's reference indices.
            bool isDefault = true;
            for (int list = 0; list < 2; list++)
            {
                int refIdx = list == 0 ? refIdx0 : refIdx1;
                if (refIdx < 0)
                {
                    continue;
                }

                for (int c = 0; c < 3; c++)
                {
                    int denom = c == 0 ? table.LumaLog2Denom : table.ChromaLog2Denom;
                    isDefault &= table.Weight(list, refIdx, c) == 1 << denom && table.Offset(list, refIdx, c) == 0;
                }
            }

            return new PartitionWeights(isDefault, table.LumaLog2Denom, table.ChromaLog2Denom, table, refIdx0, refIdx1, 0, 0);
        }

        if (!bi || header.Type != SliceType.B || slice.Pps.WeightedBipredIdc != 2)
        {
            return DefaultWeights;
        }

        // Implicit (8.4.3): weights 64 - DistScaleFactor / 4 and DistScaleFactor / 4 out of 64,
        // from the order count distances, unless the references are long-term, coincide or too far.
        var pic0 = ref0!;
        var pic1 = ref1!;
        int currentPoc = !IsFieldMb(mbAddr) ? Picture.Poc : (mbAddr & 1) == 0 ? Picture.TopPoc : Picture.BottomPoc;
        if (IsFieldMb(mbAddr))
        {
            Statistics.ImplicitField++;
        }

        int w0 = 32;
        int w1 = 32;
        if (pic1.Poc != pic0.Poc && !slice.Marks.IsLongTerm(pic0) && !slice.Marks.IsLongTerm(pic1))
        {
            int tb = Math.Clamp(currentPoc - pic0.Poc, -128, 127);
            int td = Math.Clamp(pic1.Poc - pic0.Poc, -128, 127);
            int tx = (16384 + Math.Abs(td / 2)) / td;
            int distScaleFactor = Math.Clamp(((tb * tx) + 32) >> 6, -1024, 1023);
            if (distScaleFactor >> 2 is >= -64 and <= 128)
            {
                w0 = 64 - (distScaleFactor >> 2);
                w1 = distScaleFactor >> 2;
            }
        }

        return w0 == 32 ? DefaultWeights : new PartitionWeights(false, 5, 5, null, refIdx0, refIdx1, w0, w1);
    }

    /// <summary>Weighted prediction from one list (8-449, 8-450).</summary>
    private void WeightOne(ReadOnlySpan<TPixel> prediction, int stride, int width, int height, Span<TPixel> output, int outputStride, PartitionWeights weights, int list, int component)
    {
        int logWd = weights.LogWd(component);
        int w = weights.Weight(list, component);
        int o = weights.Offset(list, component) << (_bitDepth - 8); // offsets are in 8-bit units (8-449)
        if (typeof(TPixel) == typeof(byte))
        {
            InterPrediction.WeightOne(MemoryMarshal.Cast<TPixel, byte>(prediction), stride, width, height, MemoryMarshal.Cast<TPixel, byte>(output), outputStride, w, o, logWd);
            return;
        }

        InterPrediction.WeightOneReference(prediction, stride, width, height, output, outputStride, w, o, logWd, Samples.Max<TPixel>(_bitDepth));
    }

    /// <summary>Weighted bi-prediction (8-451).</summary>
    private void WeightBoth(ReadOnlySpan<TPixel> prediction0, ReadOnlySpan<TPixel> prediction1, int stride, int width, int height, Span<TPixel> output, int outputStride, PartitionWeights weights, int component)
    {
        int logWd = weights.LogWd(component);
        int w0 = weights.Weight(0, component);
        int w1 = weights.Weight(1, component);
        int offset = ((weights.Offset(0, component) << (_bitDepth - 8)) + (weights.Offset(1, component) << (_bitDepth - 8)) + 1) >> 1;
        if (typeof(TPixel) == typeof(byte))
        {
            InterPrediction.WeightBoth(MemoryMarshal.Cast<TPixel, byte>(prediction0), MemoryMarshal.Cast<TPixel, byte>(prediction1), stride, width, height, MemoryMarshal.Cast<TPixel, byte>(output), outputStride, w0, w1, offset, logWd);
            return;
        }

        InterPrediction.WeightBothReference(prediction0, prediction1, stride, width, height, output, outputStride, w0, w1, offset, logWd, Samples.Max<TPixel>(_bitDepth));
    }

    private readonly record struct Motion(bool Available, int RefIdx, int MvX, int MvY);

    /// <summary>
    /// List <paramref name="list"/> motion of the partition covering luma location (x, y)
    /// relative to the current macroblock (6.4.11.7). Inside the current macroblock a block is
    /// only available when it comes before <paramref name="currentBlock"/> (the decoding index of
    /// the current partition's first 4x4 block). Intra macroblocks, and partitions that do not use
    /// the list, are available with refIdx -1 and zero motion.
    /// </summary>
    /// <remarks>
    /// In MBAFF frames a neighbour of the other kind is seen in the current macroblock's terms
    /// (8.4.1.3.1): a frame neighbour of a field macroblock with half the vertical motion
    /// (rounded towards zero) and twice the reference index, a field neighbour of a frame
    /// macroblock with twice the motion and half the index.
    /// </remarks>
    private Motion MotionNeighbor(int list, int mbAddr, int x, int y, int currentBlock)
    {
        var (n, xW, yW) = Neighbor(mbAddr, x, y);
        if (n < 0)
        {
            return new Motion(false, -1, 0, 0);
        }

        int bx = xW / 4;
        int by = yW / 4;
        if (n == mbAddr && BlockIndex(bx, by) >= currentBlock)
        {
            return new Motion(false, -1, 0, 0);
        }

        int block = (n * 16) + (by * 4) + bx;
        int refIdx = _refIdx[list][(n * 4) + ((by >> 1) * 2) + (bx >> 1)];
        int mvY = _mvY[list][block];
        if (_mbaff && _fieldMb[n] != _fieldMb[mbAddr])
        {
            (refIdx, mvY) = _fieldMb[mbAddr] ? (refIdx < 0 ? refIdx : refIdx * 2, mvY / 2) : (refIdx >> 1, mvY * 2);
        }

        return new Motion(true, refIdx, _mvX[list][block], mvY);
    }

    /// <summary>Luma motion vector prediction for list <paramref name="list"/> (8.4.1.3).</summary>
    private (int X, int Y) PredictMv(int list, int mbAddr, int x, int y, int w, int h, int refIdx, PartitionShape shape)
    {
        int current = BlockIndex(x / 4, y / 4);
        var a = MotionNeighbor(list, mbAddr, x - 1, y, current);
        var b = MotionNeighbor(list, mbAddr, x, y - 1, current);
        var c = MotionNeighbor(list, mbAddr, x + w, y - 1, current);
        if (!c.Available)
        {
            c = MotionNeighbor(list, mbAddr, x - 1, y - 1, current); // D replaces C
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

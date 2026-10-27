using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ManagedCodecs.H264;

/// <summary>The deblocking filter (H.264 clause 8.7) for frames.</summary>
internal sealed partial class PictureDecoder<TPixel>
{
    // Table 8-16: alpha' and beta' by indexA / indexB.
    private static ReadOnlySpan<byte> Alpha =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 4, 5, 6, 7, 8, 9, 10, 12, 13,
        15, 17, 20, 22, 25, 28, 32, 36, 40, 45, 50, 56, 63, 71, 80, 90, 101, 113, 127, 144, 162, 182, 203, 226, 255, 255,
    ];

    private static ReadOnlySpan<byte> Beta =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4,
        6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16, 17, 17, 18, 18,
    ];

    // Table 8-17: tC0 by indexA, for bS = 1, 2, 3.
    private static ReadOnlySpan<byte> Tc0 =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 1, 1, 2, 1, 1, 2, 1, 1, 2, 1, 2, 3,
        1, 2, 3, 2, 2, 3, 2, 2, 4, 2, 3, 4, 2, 3, 4, 3, 3, 5, 3, 4, 6, 3, 4, 6,
        4, 5, 7, 4, 5, 8, 4, 6, 9, 5, 7, 10, 6, 8, 11, 6, 8, 13, 7, 10, 14, 8, 11, 16,
        9, 12, 18, 10, 13, 20, 11, 15, 23, 13, 17, 25,
    ];

    // The picture's macroblocks decoded in address order from its first (every one of its slots before this address),
    // and its rows deblocked so far: macroblock rows of a frame, pair rows of a pair-slot picture (an MBAFF frame's
    // macroblock pairs, a field picture's macroblock rows). Rows are deblocked a row behind decoding.
    private int _decodedPrefix;
    private int _deblockedRows;

    // Per macroblock, once its deblocking has looked at it: inter, one block of motion in each list
    // and no luma coefficients (HasUniformMotionWithoutCoefficients), for its neighbours' edges.
    private bool[]? _uniformMb;

    // Decoded macroblock rows last reported to the picture's progress (frame threading).
    private int _reportedRows;

    /// <summary>
    /// Deblocks the rows that decoding no longer needs unfiltered: a row once the row below it is decoded, as intra
    /// prediction reads the row above as it was before filtering, and filtering a row changes all of it (its vertical
    /// edges) and the bottom of the row above (its top edges) but nothing below it. That holds for pair rows too, where
    /// frame and field pairs meet. Macroblocks are still filtered in address order (8.7), so the result is the same as
    /// filtering the whole picture at the end.
    /// </summary>
    private void MacroblockDone(int mbAddr)
    {
        _lastDoneMb = mbAddr;

        // Every macroblock marked as in a slice is decoded by now (they are marked as they start): some may be past this
        // one (slices out of order). A field picture's macroblocks are every other slot.
        while (_decodedPrefix < _mbCount && _sliceOfMb[_decodedPrefix] >= 0)
        {
            _decodedPrefix += MbStep;
        }

        int decodedRows = (_decodedPrefix - Math.Max(_fieldParity, 0)) / RowSlots;
        int rows = Math.Max(_deblockedRows, decodedRows - 1);
        DeblockRows(_deblockedRows, rows);
        _deblockedRows = rows;

        // Rows are final once the row below them is deblocked too: its top edge filters their last rows.
        if (decodedRows != _reportedRows)
        {
            _reportedRows = decodedRows;
            ReportProgress(decodedRows, Math.Max(0, _deblockedRows - 1));
            if (RowDelay > 0)
            {
                Thread.Sleep(Random.Shared.Next(RowDelay + 1));
            }
        }
    }

    // The slots of a row: a frame's macroblock row, or a pair row (two macroblock slots a pair; a field picture has one
    // of them).
    private int RowSlots => _pairSlots ? 2 * _widthInMbs : _widthInMbs;

    /// <summary>
    /// Reports decoded and final rows to the picture (frame threading), in the units of what it is: a frame's macroblock
    /// and luma rows (an MBAFF frame's pair row is two macroblock rows, 32 luma rows), a field picture's own.
    /// </summary>
    private void ReportProgress(int decodedRows, int finalRows)
    {
        if (Picture.SeparatePlanes)
        {
            return; // a colour plane's rows are not the frame's: the frame is reported done once all three planes are
        }

        if (_fieldParity >= 0)
        {
            Picture.Field(_fieldParity).ReportProgress(decodedRows, 16 * finalRows);
        }
        else if (_pairSlots)
        {
            Picture.ReportProgress(2 * decodedRows, 32 * finalRows);
        }
        else
        {
            Picture.ReportProgress(decodedRows, 16 * finalRows);
        }
    }

    /// <summary>The macroblock row of the frame that holds (the bottom of) macroblock <paramref name="mbAddr"/>.</summary>
    private int FrameMbRow(int mbAddr) => !_pairSlots ? mbAddr / _widthInMbs : (2 * ((mbAddr >> 1) / _widthInMbs)) + 1;

    /// <summary>Filters what is left of the picture, macroblock by macroblock in address order (8.7).</summary>
    public void Deblock()
    {
        int height = _mbCount / RowSlots;
        DeblockRows(_deblockedRows, height);
        _deblockedRows = height;
    }

    /// <summary>Filters the rows from <paramref name="firstRow"/> up to <paramref name="endRow"/>: macroblock rows of a frame, pair rows of a pair-slot picture.</summary>
    [SkipLocalsInit]
    private void DeblockRows(int firstRow, int endRow)
    {
        if (firstRow >= endRow)
        {
            return;
        }

        if (_pairSlots)
        {
            DeblockMbaff(firstRow, endRow);
            return;
        }

        // bS per edge (0..3) and per 4-sample segment along it, for vertical and horizontal edges.
        Span<byte> vertical = stackalloc byte[16];
        Span<byte> horizontal = stackalloc byte[16];

        _uniformMb ??= new bool[_mbCount];
        for (int mbAddr = firstRow * _widthInMbs; mbAddr < endRow * _widthInMbs; mbAddr++)
        {
            // Kept for every macroblock, filtered or not, before its right and lower neighbours read it.
            bool intra = IsIntra(mbAddr);
            bool uniform = !intra && HasUniformMotionWithoutCoefficients(mbAddr);
            _uniformMb[mbAddr] = uniform;

            var slice = _slices[_sliceOfMb[mbAddr]];
            if (slice.DisableDeblockingFilterIdc == 1)
            {
                continue;
            }

            int mbX = mbAddr % _widthInMbs;
            int mbY = mbAddr / _widthInMbs;
            bool sameSliceOnly = slice.DisableDeblockingFilterIdc == 2;
            bool left = mbX > 0 && (!sameSliceOnly || _sliceOfMb[mbAddr - 1] == _sliceOfMb[mbAddr]);
            bool top = mbY > 0 && (!sameSliceOnly || _sliceOfMb[mbAddr - _widthInMbs] == _sliceOfMb[mbAddr]);

            // Macroblock edges (edge 0) depend on the neighbour. Between two macroblocks that
            // move as one block each and have no luma coefficients, all four segments have the
            // bS of any one of them.
            MacroblockEdgeStrengths(vertical, left, mbAddr - 1, 3, 4, mbAddr, uniform);
            MacroblockEdgeStrengths(horizontal, top, mbAddr - _widthInMbs, 12, 1, mbAddr, uniform);

            // Internal edges: fixed for intra macroblocks and for macroblocks with uniform motion
            // and no luma coefficients; otherwise block by block.
            if (intra)
            {
                vertical[4..].Fill(3);
                horizontal[4..].Fill(3);
            }
            else if (uniform)
            {
                vertical[4..].Clear();
                horizontal[4..].Clear();
            }
            else
            {
                // Edges 1 and 3 of an 8x8-transform macroblock are cleared below; only 4:2:2 chroma
                // needs their bS first.
                InternalStrengths(mbAddr, vertical, horizontal, oddEdges: !_transform8x8[mbAddr] || (_mbHeightC == 16 && !_chroma444));
            }

            // 4:2:2 chroma first, while the bS of all four horizontal edges are there (the planes
            // are independent, so the order does not matter).
            if (_mbHeightC == 16 && !_chroma444)
            {
                DeblockChroma422(slice, mbAddr, vertical, horizontal);
            }

            // With 8x8 transforms the luma edges inside the 8x8 blocks are not filtered, nor 4:4:4
            // chroma's; 4:2:0 chroma filters only edges 0 and 2 anyway.
            if (_transform8x8[mbAddr])
            {
                vertical.Slice(4, 4).Clear();
                vertical.Slice(12, 4).Clear();
                horizontal.Slice(4, 4).Clear();
                horizontal.Slice(12, 4).Clear();
            }

            var (lumaOrigin, lumaStride) = LumaPlace(mbAddr);
            var (chromaOrigin, chromaStride) = ChromaPlace(mbAddr);
            int qp = _qp[mbAddr];
            int qpLeft = mbX > 0 ? _qp[mbAddr - 1] : qp;
            int qpAbove = mbY > 0 ? _qp[mbAddr - _widthInMbs] : qp;
            FilterLumaEdges(PlaneY, lumaOrigin, lumaStride, vertical, horizontal, qpLeft, qpAbove, qp, slice);

            // 4:4:4 chroma: the luma filter on the luma edges (chromaStyleFilteringFlag 0, 8.7.2), at the QP_C of each side.
            if (_chroma444)
            {
                FilterLumaEdges(PlaneCb, chromaOrigin, chromaStride, vertical, horizontal, ChromaQp(qpLeft, slice.CbQpOffset, _qpBdOffset), ChromaQp(qpAbove, slice.CbQpOffset, _qpBdOffset), ChromaQp(qp, slice.CbQpOffset, _qpBdOffset), slice);
                FilterLumaEdges(PlaneCr, chromaOrigin, chromaStride, vertical, horizontal, ChromaQp(qpLeft, slice.CrQpOffset, _qpBdOffset), ChromaQp(qpAbove, slice.CrQpOffset, _qpBdOffset), ChromaQp(qp, slice.CrQpOffset, _qpBdOffset), slice);
                continue;
            }

            if (_mbHeightC == 16 || _monochrome)
            {
                continue;
            }

            // Chroma (4:2:0): edges 0 and 4 use the bS of luma edges 0 and 8; QP_C per component.
            // Cb and Cr are filtered together, all vertical edges first.
            int qpCb = ChromaQp(qp, slice.CbQpOffset, _qpBdOffset);
            int qpCr = ChromaQp(qp, slice.CrQpOffset, _qpBdOffset);
            for (int edge = 0; edge < 2; edge++)
            {
                int qpP = edge == 0 ? qpLeft : qp;
                FilterChromaEdge(PlaneCb, PlaneCr, chromaOrigin + (edge * 4), 1, chromaStride, vertical.Slice(edge * 8, 4), ChromaQp(qpP, slice.CbQpOffset, _qpBdOffset), qpCb, ChromaQp(qpP, slice.CrQpOffset, _qpBdOffset), qpCr, slice.AlphaOffset, slice.BetaOffset, bitDepth: _bitDepth);
            }

            for (int edge = 0; edge < 2; edge++)
            {
                int qpP = edge == 0 ? qpAbove : qp;
                FilterChromaEdge(PlaneCb, PlaneCr, chromaOrigin + (edge * 4 * chromaStride), chromaStride, 1, horizontal.Slice(edge * 8, 4), ChromaQp(qpP, slice.CbQpOffset, _qpBdOffset), qpCb, ChromaQp(qpP, slice.CrQpOffset, _qpBdOffset), qpCr, slice.AlphaOffset, slice.BetaOffset, bitDepth: _bitDepth);
            }
        }
    }

    /// <summary>
    /// The four vertical edges of a 16x16 block, then its four horizontal ones, with the luma
    /// filter: luma, or a 4:4:4 chroma component. The QPs are of the left, upper and current
    /// macroblocks (the QP_C of the component for chroma).
    /// </summary>
    private void FilterLumaEdges(TPixel[] plane, int origin, int stride, ReadOnlySpan<byte> vertical, ReadOnlySpan<byte> horizontal, int qpLeft, int qpAbove, int qp, SliceInfo slice)
    {
        for (int edge = 0; edge < 4; edge++)
        {
            FilterEdge(plane, origin + (edge * 4), 1, stride, 16, vertical.Slice(edge * 4, 4), edge == 0 ? qpLeft : qp, qp, slice.AlphaOffset, slice.BetaOffset, chroma: false, bitDepth: _bitDepth);
        }

        for (int edge = 0; edge < 4; edge++)
        {
            FilterEdge(plane, origin + (edge * 4 * stride), stride, 1, 16, horizontal.Slice(edge * 4, 4), edge == 0 ? qpAbove : qp, qp, slice.AlphaOffset, slice.BetaOffset, chroma: false, bitDepth: _bitDepth);
        }
    }

    /// <summary>
    /// The chroma edges of a 4:2:2 macroblock (8x16 samples a component): each with the bS of the
    /// luma edge at the corresponding luma samples (8.7.2.1). Vertical edges 0 and 4 run 16 rows,
    /// four to a luma segment of luma edges 0 and 8; horizontal edges 0, 4, 8 and 12 are at the
    /// rows of luma edges 0 to 3, which keep their bS with 8x8 transforms too, as chroma's 4x4
    /// blocks are the height of luma's.
    /// </summary>
    private void DeblockChroma422(SliceInfo slice, int mbAddr, ReadOnlySpan<byte> vertical, ReadOnlySpan<byte> horizontal)
    {
        var (origin, stride) = ChromaPlace(mbAddr);
        int qp = _qp[mbAddr];
        int qpCb = ChromaQp(qp, slice.CbQpOffset, _qpBdOffset);
        int qpCr = ChromaQp(qp, slice.CrQpOffset, _qpBdOffset);
        int qpLeft = mbAddr % _widthInMbs > 0 ? _qp[mbAddr - 1] : qp;
        int qpAbove = mbAddr >= _widthInMbs ? _qp[mbAddr - _widthInMbs] : qp;
        for (int edge = 0; edge < 2; edge++)
        {
            int qpP = edge == 0 ? qpLeft : qp;
            var strengths = vertical.Slice(edge * 8, 4);
            FilterEdge(PlaneCb, origin + (edge * 4), 1, stride, 16, strengths, ChromaQp(qpP, slice.CbQpOffset, _qpBdOffset), qpCb, slice.AlphaOffset, slice.BetaOffset, chroma: true, segmentShift: 2, bitDepth: _bitDepth);
            FilterEdge(PlaneCr, origin + (edge * 4), 1, stride, 16, strengths, ChromaQp(qpP, slice.CrQpOffset, _qpBdOffset), qpCr, slice.AlphaOffset, slice.BetaOffset, chroma: true, segmentShift: 2, bitDepth: _bitDepth);
        }

        for (int edge = 0; edge < 4; edge++)
        {
            int qpP = edge == 0 ? qpAbove : qp;
            FilterChromaEdge(PlaneCb, PlaneCr, origin + (edge * 4 * stride), stride, 1, horizontal.Slice(edge * 4, 4), ChromaQp(qpP, slice.CbQpOffset, _qpBdOffset), qpCb, ChromaQp(qpP, slice.CrQpOffset, _qpBdOffset), qpCr, slice.AlphaOffset, slice.BetaOffset, bitDepth: _bitDepth);
        }
    }

    /// <summary>
    /// bS of the four segments of a macroblock edge, 0 where it is not <paramref name="filtered"/>:
    /// segment k between block <paramref name="firstP"/> + k * <paramref name="step"/> of
    /// <paramref name="mbP"/> and block k * step of <paramref name="mbQ"/> (raster indices; a
    /// vertical edge has firstP 3 and step 4, a horizontal one 12 and 1).
    /// </summary>
    private void MacroblockEdgeStrengths(Span<byte> strengths, bool filtered, int mbP, int firstP, int step, int mbQ, bool uniformQ)
    {
        if (!filtered)
        {
            strengths[..4].Clear();
            return;
        }

        // Both macroblocks one block of motion each, without coefficients: neither is intra, so
        // every segment's bS is the motion's.
        if (uniformQ && _uniformMb![mbP])
        {
            strengths[..4].Fill((byte)MotionStrength((mbP * 16) + firstP, mbQ * 16));
            return;
        }

        for (int k = 0; k < 4; k++)
        {
            strengths[k] = (byte)BoundaryStrength(mbP, firstP + (k * step), mbQ, k * step, mbEdge: true);
        }
    }

    /// <summary>bS for the edge between luma 4x4 blocks p and q (raster indices), 8.7.2.1.</summary>
    private int BoundaryStrength(int mbP, int blockP, int mbQ, int blockQ, bool mbEdge)
    {
        if (IsIntra(mbP) || IsIntra(mbQ))
        {
            return mbEdge ? 4 : 3;
        }

        if (HasCoefficients(mbP, blockP) || HasCoefficients(mbQ, blockQ))
        {
            return 2;
        }

        return MotionStrength((mbP * 16) + blockP, (mbQ * 16) + blockQ);
    }

    /// <summary>
    /// bS 1 or 0 of an edge between inter blocks p and q without coefficients (8.7.2.1), from their
    /// motion; the blocks are picture-wide 4x4 block indices (macroblock * 16 + raster index). The
    /// reference pictures are compared as pictures, whichever list they came from.
    /// </summary>
    private int MotionStrength(int p, int q, int limitY = 4)
    {
        int partP = ((p >> 4) * 4) + ((p & 15) >> 3 << 1) + ((p & 3) >> 1);
        int partQ = ((q >> 4) * 4) + ((q & 15) >> 3 << 1) + ((q & 3) >> 1);
        var p0 = _refPic[0][partP];
        var p1 = _refPic[1][partP];
        var q0 = _refPic[0][partQ];
        var q1 = _refPic[1][partQ];

        bool Far(int listA, int a, int listB, int b) =>
            Math.Abs(_mvX[listA][a] - _mvX[listB][b]) >= 4 || Math.Abs(_mvY[listA][a] - _mvY[listB][b]) >= limitY;

        if (p1 is null && q1 is null)
        {
            // One motion vector each from list 0 (all of P slices).
            return p0 != q0 || Far(0, p, 0, q) ? 1 : 0;
        }

        int countP = (p0 is null ? 0 : 1) + (p1 is null ? 0 : 1);
        int countQ = (q0 is null ? 0 : 1) + (q1 is null ? 0 : 1);
        if (countP != countQ)
        {
            return 1;
        }

        if (countP == 1)
        {
            int listP = p0 is null ? 1 : 0;
            int listQ = q0 is null ? 1 : 0;
            return (p0 ?? p1) != (q0 ?? q1) || Far(listP, p, listQ, q) ? 1 : 0;
        }

        if (!((p0 == q0 && p1 == q1) || (p0 == q1 && p1 == q0)))
        {
            return 1; // not the same two reference pictures
        }

        if (p0 != p1)
        {
            // Two different pictures: compare the motion vectors that refer to the same picture.
            return p0 == q0 ? (Far(0, p, 0, q) || Far(1, p, 1, q) ? 1 : 0) : (Far(0, p, 1, q) || Far(1, p, 0, q) ? 1 : 0);
        }

        // Both vectors of each block refer to the same picture: either pairing may match.
        return (Far(0, p, 0, q) || Far(1, p, 1, q)) && (Far(0, p, 1, q) || Far(1, p, 0, q)) ? 1 : 0;
    }

    /// <summary>
    /// bS of the internal edges (1..3) of an inter macroblock: <see cref="BoundaryStrength"/> with
    /// both blocks in the same macroblock, so neither is intra. Without <paramref name="oddEdges"/>,
    /// edges 1 and 3 are left unset.
    /// </summary>
    private void InternalStrengths(int mbAddr, Span<byte> vertical, Span<byte> horizontal, int limitY = 4, bool oddEdges = true)
    {
        // With 8x8 transforms each 4x4 block counts as coded when its 8x8 block is.
        Span<byte> per8x8 = stackalloc byte[16];
        ReadOnlySpan<byte> coefficients = per8x8;
        if (_transform8x8[mbAddr])
        {
            for (int i = 0; i < 16; i++)
            {
                per8x8[i] = (byte)(HasCoefficients(mbAddr, i) ? 1 : 0);
            }
        }
        else
        {
            coefficients = _totalCoeffLuma.AsSpan(mbAddr * 16, 16);
        }

        int base4x4 = mbAddr * 16;
        int Strength(int p, int q, ReadOnlySpan<byte> coefficients) =>
            (coefficients[p] | coefficients[q]) != 0 ? 2 : MotionStrength(base4x4 + p, base4x4 + q, limitY);

        for (int edge = oddEdges ? 1 : 2; edge < 4; edge += oddEdges ? 1 : 2)
        {
            for (int k = 0; k < 4; k++)
            {
                vertical[(edge * 4) + k] = (byte)Strength((k * 4) + edge - 1, (k * 4) + edge, coefficients);
                horizontal[(edge * 4) + k] = (byte)Strength(((edge - 1) * 4) + k, (edge * 4) + k, coefficients);
            }
        }
    }

    /// <summary>
    /// True when every internal edge of an inter macroblock has bS 0: no luma coefficients and
    /// uniform motion (P_Skip, uncoded 16x16).
    /// </summary>
    private bool HasUniformMotionWithoutCoefficients(int mbAddr) =>
        Vector128.Create(_totalCoeffLuma.AsSpan(mbAddr * 16, 16)) == Vector128<byte>.Zero && HasUniformMotion(mbAddr);

    /// <summary>
    /// Whether, in each list, all 16 blocks of an inter macroblock have the same reference
    /// picture and index and the same motion vector: then it predicts as one 16x16 partition.
    /// </summary>
    private bool HasUniformMotion(int mbAddr)
    {
        for (int list = 0; list < 2; list++)
        {
            var refs = _refPic[list].AsSpan(mbAddr * 4, 4);
            if (refs[1] != refs[0] || refs[2] != refs[0] || refs[3] != refs[0])
            {
                return false;
            }

            // The index too, as explicit weights go by index and two can name one picture.
            if (MemoryMarshal.Read<uint>(MemoryMarshal.AsBytes(_refIdx[list].AsSpan(mbAddr * 4, 4))) != (byte)_refIdx[list][mbAddr * 4] * 0x01010101u)
            {
                return false;
            }

            var mvX = _mvX[list].AsSpan(mbAddr * 16, 16);
            var mvY = _mvY[list].AsSpan(mbAddr * 16, 16);
            var x0 = Vector128.Create(mvX[0]);
            var y0 = Vector128.Create(mvY[0]);
            if (Vector128.Create(mvX) != x0 || Vector128.Create(mvX[8..]) != x0 || Vector128.Create(mvY) != y0 || Vector128.Create(mvY[8..]) != y0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the transform block holding luma 4x4 block <paramref name="block"/> (raster index)
    /// has nonzero coefficients: the 4x4 block itself, or with 8x8 transforms its 8x8 block.
    /// </summary>
    private bool HasCoefficients(int mbAddr, int block) =>
        _transform8x8[mbAddr] ? Has8x8Coefficients(mbAddr, ((block >> 3) * 2) + ((block & 3) >> 1)) : _totalCoeffLuma[(mbAddr * 16) + block] != 0;

    private bool IsIntra(int mbAddr) => _kind[mbAddr] is MbKind.Intra4x4 or MbKind.Intra16x16 or MbKind.IPcm;

    /// <summary>
    /// Filters one edge (8.7.2). <paramref name="q0"/> is the first q0 sample; p samples lie at
    /// -<paramref name="across"/>, -2 * across, ... and the edge runs along <paramref name="along"/>.
    /// <paramref name="strengths"/> holds bS per luma 4x4 segment; chroma samples use bS of segment i / 2,
    /// or of i / 4 with <paramref name="segmentShift"/> 2 (vertical edges of 4:2:2 chroma). Samples
    /// of <paramref name="bitDepth"/> bits; the vector version takes 8-bit ones.
    /// </summary>
    internal static void FilterEdge(TPixel[] plane, int q0, int across, int along, int length, ReadOnlySpan<byte> strengths, int qpP, int qpQ, int alphaOffset, int betaOffset, bool chroma, bool vectorized = true, int segmentShift = 0, int bitDepth = 8)
    {
        if (segmentShift == 0)
        {
            segmentShift = chroma ? 1 : 2; // 2 or 4 samples per segment
        }

        if (MemoryMarshal.Read<uint>(strengths) == 0)
        {
            return; // all four segments have bS 0
        }

        int qpAv = (qpP + qpQ + 1) >> 1;
        int indexA = Math.Clamp(qpAv + alphaOffset, 0, 51);
        int indexB = Math.Clamp(qpAv + betaOffset, 0, 51);

        // alpha', beta' and tC0' scale by 2^(BitDepth - 8) (8-460, 8-461, 8-462).
        int alpha = Alpha[indexA] << (bitDepth - 8);
        int beta = Beta[indexB] << (bitDepth - 8);
        if (alpha == 0 || beta == 0)
        {
            return; // no sample can pass |p0 - q0| < alpha
        }

        if (typeof(TPixel) == typeof(byte) && vectorized && Vector128.IsHardwareAccelerated)
        {
            FilterEdgeVector(Unsafe.As<byte[]>(plane), q0, across, along, length, strengths, segmentShift, indexA, alpha, beta, chroma);
        }
        else if (typeof(TPixel) == typeof(ushort) && vectorized && Vector128.IsHardwareAccelerated && bitDepth <= 12)
        {
            // 16-bit lanes: the widest sum (8 samples and a rounding term, 8-479) fits up to 12 bits.
            FilterEdgeVector16(Unsafe.As<ushort[]>(plane), q0, across, along, length, strengths, segmentShift, indexA, alpha, beta, chroma, bitDepth);
        }
        else
        {
            FilterEdgeScalar(plane, q0, across, along, length, strengths, segmentShift, indexA, alpha, beta, chroma, bitDepth);
        }
    }

    /// <summary>The filter of 8.7.2.3 / 8.7.2.4 sample by sample; the reference for the vector version.</summary>
    private static void FilterEdgeScalar(TPixel[] plane, int q0, int across, int along, int length, ReadOnlySpan<byte> strengths, int segmentShift, int indexA, int alpha, int beta, bool chroma, int bitDepth)
    {
        for (int i = 0; i < length; i++)
        {
            int bS = strengths[i >> segmentShift];
            if (bS != 0)
            {
                int pos = q0 + (i * along);
                FilterSamples(plane, pos - across, across, pos, across, bS, indexA, alpha, beta, chroma, bitDepth);
            }
        }
    }

    /// <summary>
    /// The filter across one line of samples: p0 at <paramref name="p0"/>, p1, p2, p3 each
    /// <paramref name="pStep"/> further away from the edge, and q0, q1, q2, q3 from <paramref name="q0"/>
    /// on in steps of <paramref name="qStep"/> (the two sides differ where an MBAFF frame macroblock
    /// meets a field macroblock).
    /// </summary>
    private static void FilterSamples(TPixel[] plane, int p0, int pStep, int q0, int qStep, int bS, int indexA, int alpha, int beta, bool chroma, int bitDepth)
    {
        int max = Samples.Max<TPixel>(bitDepth);
        int p0v = int.CreateTruncating(plane[p0]);
        int p1 = int.CreateTruncating(plane[p0 - pStep]);
        int q0v = int.CreateTruncating(plane[q0]);
        int q1 = int.CreateTruncating(plane[q0 + qStep]);

        if (Math.Abs(p0v - q0v) >= alpha || Math.Abs(p1 - p0v) >= beta || Math.Abs(q1 - q0v) >= beta)
        {
            return;
        }

        if (chroma)
        {
            if (bS < 4)
            {
                int tc = (Tc0[(indexA * 3) + bS - 1] << (bitDepth - 8)) + 1;
                int delta = Math.Clamp((((q0v - p0v) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
                plane[p0] = TPixel.CreateTruncating(Math.Clamp(p0v + delta, 0, max));
                plane[q0] = TPixel.CreateTruncating(Math.Clamp(q0v - delta, 0, max));
            }
            else
            {
                plane[p0] = TPixel.CreateTruncating(((2 * p1) + p0v + q1 + 2) >> 2);
                plane[q0] = TPixel.CreateTruncating(((2 * q1) + q0v + p1 + 2) >> 2);
            }

            return;
        }

        int p2 = int.CreateTruncating(plane[p0 - (2 * pStep)]);
        int q2 = int.CreateTruncating(plane[q0 + (2 * qStep)]);
        bool ap = Math.Abs(p2 - p0v) < beta;
        bool aq = Math.Abs(q2 - q0v) < beta;

        if (bS < 4)
        {
            int tc0 = Tc0[(indexA * 3) + bS - 1] << (bitDepth - 8);
            int tc = tc0 + (ap ? 1 : 0) + (aq ? 1 : 0);
            int delta = Math.Clamp((((q0v - p0v) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
            plane[p0] = TPixel.CreateTruncating(Math.Clamp(p0v + delta, 0, max));
            plane[q0] = TPixel.CreateTruncating(Math.Clamp(q0v - delta, 0, max));
            if (ap)
            {
                plane[p0 - pStep] = TPixel.CreateTruncating(p1 + Math.Clamp((p2 + ((p0v + q0v + 1) >> 1) - (p1 << 1)) >> 1, -tc0, tc0));
            }

            if (aq)
            {
                plane[q0 + qStep] = TPixel.CreateTruncating(q1 + Math.Clamp((q2 + ((p0v + q0v + 1) >> 1) - (q1 << 1)) >> 1, -tc0, tc0));
            }

            return;
        }

        // bS == 4: the strong filter where the edge is smooth enough.
        bool small = Math.Abs(p0v - q0v) < ((alpha >> 2) + 2);
        if (ap && small)
        {
            int p3 = int.CreateTruncating(plane[p0 - (3 * pStep)]);
            plane[p0] = TPixel.CreateTruncating((p2 + (2 * p1) + (2 * p0v) + (2 * q0v) + q1 + 4) >> 3);
            plane[p0 - pStep] = TPixel.CreateTruncating((p2 + p1 + p0v + q0v + 2) >> 2);
            plane[p0 - (2 * pStep)] = TPixel.CreateTruncating(((2 * p3) + (3 * p2) + p1 + p0v + q0v + 4) >> 3);
        }
        else
        {
            plane[p0] = TPixel.CreateTruncating(((2 * p1) + p0v + q1 + 2) >> 2);
        }

        if (aq && small)
        {
            int q3 = int.CreateTruncating(plane[q0 + (3 * qStep)]);
            plane[q0] = TPixel.CreateTruncating((p1 + (2 * p0v) + (2 * q0v) + (2 * q1) + q2 + 4) >> 3);
            plane[q0 + qStep] = TPixel.CreateTruncating((p0v + q0v + q1 + q2 + 2) >> 2);
            plane[q0 + (2 * qStep)] = TPixel.CreateTruncating(((2 * q3) + (3 * q2) + q1 + q0v + p0v + 4) >> 3);
        }
        else
        {
            plane[q0] = TPixel.CreateTruncating(((2 * q1) + q0v + p1 + 2) >> 2);
        }
    }

    /// <summary>
    /// The same filter on up to 16 positions at once, in registers: the samples across the edge
    /// (p3 .. q3) loaded as rows (horizontal edges) or transposed from them (vertical ones),
    /// filtered in 16-bit lanes with per-lane bS, and written back.
    /// </summary>
    private static void FilterEdgeVector(byte[] plane, int q0, int across, int along, int length, ReadOnlySpan<byte> strengths, int segmentShift, int indexA, int alpha, int beta, bool chroma)
    {
        var parameters = new EdgeParameters(indexA, alpha, beta);

        // Four lanes to a segment, or two (8-sample chroma edges, whose lanes 8-15 repeat 0-7).
        var bs = SegmentLanes(strengths, segmentShift == 2 ? LumaSegments : ChromaSegments);
        if (across == 1)
        {
            // An edge of 8 rows takes them twice: lanes 8-15 are filtered and written back alike.
            FilterColumns(plane, plane, q0 - 4, length == 16 ? q0 - 4 + (8 * along) : q0 - 4, along, bs, parameters, parameters, chroma);
        }
        else
        {
            FilterRows(plane, q0, across, length, bs, parameters, chroma);
        }
    }

    /// <summary>
    /// <see cref="FilterEdgeVector"/> for samples of more than 8 bits (ushort planes): the taps are
    /// 16-bit lanes already (10-bit sums stay within them), tC0 scaled to the bit depth, the
    /// results clipped to its largest value.
    /// </summary>
    [SkipLocalsInit]
    private static void FilterEdgeVector16(ushort[] plane, int q0, int across, int along, int length, ReadOnlySpan<byte> strengths, int segmentShift, int indexA, int alpha, int beta, bool chroma, int bitDepth)
    {
        Span<short> taps = stackalloc short[(8 * 16) + 16];
        int firstTap = chroma ? 2 : 0;
        int lastTap = chroma ? 5 : 7;
        for (int k = firstTap; k <= lastTap; k++)
        {
            int start = q0 + ((k - 4) * across);
            var row = taps.Slice(k * 16, length);
            if (along == 1)
            {
                MemoryMarshal.Cast<ushort, short>(plane.AsSpan(start, length)).CopyTo(row);
                continue;
            }

            for (int i = 0; i < length; i++)
            {
                row[i] = (short)plane[start + (i * along)];
            }
        }

        Span<short> lanes = stackalloc short[4 * 16];
        SetLanes(lanes, 0, length, strengths, segmentShift, indexA, alpha, beta, tcShift: bitDepth - 8);
        FilterTaps(taps, length, lanes, chroma, (short)((1 << bitDepth) - 1));

        // Only p2 .. q2 (luma) or p0, q0 (chroma) can change.
        for (int k = chroma ? 3 : 1; k <= (chroma ? 4 : 6); k++)
        {
            int start = q0 + ((k - 4) * across);
            var row = taps.Slice(k * 16, length);
            if (along == 1)
            {
                MemoryMarshal.Cast<short, ushort>(row).CopyTo(plane.AsSpan(start, length));
                continue;
            }

            for (int i = 0; i < length; i++)
            {
                plane[start + (i * along)] = (ushort)row[i];
            }
        }
    }

    /// <summary>
    /// Both chroma components' edge at once: Cb in lanes 0-7, Cr in lanes 8-15, each with its own
    /// QP (so its own alpha, beta and tC0) and the same bS. The same result as
    /// <see cref="FilterEdge"/> on each component.
    /// </summary>
    [SkipLocalsInit]
    internal static void FilterChromaEdge(TPixel[] cbPlane, TPixel[] crPlane, int q0, int across, int along, ReadOnlySpan<byte> strengths, int qpPCb, int qpQCb, int qpPCr, int qpQCr, int alphaOffset, int betaOffset, bool vectorized = true, int bitDepth = 8)
    {
        if (MemoryMarshal.Read<uint>(strengths) == 0)
        {
            return;
        }

        if (typeof(TPixel) != typeof(byte) || !vectorized || !Vector128.IsHardwareAccelerated)
        {
            // Deeper samples: each component through FilterEdge (its vector path, if any).
            FilterEdge(cbPlane, q0, across, along, 8, strengths, qpPCb, qpQCb, alphaOffset, betaOffset, chroma: true, vectorized, bitDepth: bitDepth);
            FilterEdge(crPlane, q0, across, along, 8, strengths, qpPCr, qpQCr, alphaOffset, betaOffset, chroma: true, vectorized, bitDepth: bitDepth);
            return;
        }

        byte[] cb = Unsafe.As<byte[]>(cbPlane);
        byte[] cr = Unsafe.As<byte[]>(crPlane);
        var cbParameters = ChromaParameters(qpPCb, qpQCb, alphaOffset, betaOffset, out bool cbFiltered);
        var crParameters = ChromaParameters(qpPCr, qpQCr, alphaOffset, betaOffset, out bool crFiltered);
        if (!cbFiltered && !crFiltered)
        {
            return;
        }

        var bs = SegmentLanes(strengths, ChromaSegments);
        if (across == 1)
        {
            FilterColumns(cb, cr, q0 - 4, q0 - 4, along, bs, cbParameters, crParameters, chroma: true);
        }
        else
        {
            FilterChromaRows(cb, cr, q0, across, bs, cbParameters, crParameters);
        }
    }

    /// <summary>
    /// A chroma component's parameters for <see cref="FilterChromaEdge"/>; <paramref name="filtered"/>
    /// false when alpha or beta is 0, which lets no sample pass (alpha is then 0 in the parameters).
    /// </summary>
    private static EdgeParameters ChromaParameters(int qpP, int qpQ, int alphaOffset, int betaOffset, out bool filtered)
    {
        int qpAv = (qpP + qpQ + 1) >> 1;
        int indexA = Math.Clamp(qpAv + alphaOffset, 0, 51);
        int alpha = Alpha[indexA];
        int beta = Beta[Math.Clamp(qpAv + betaOffset, 0, 51)];
        filtered = alpha != 0 && beta != 0;
        return new EdgeParameters(indexA, filtered ? alpha : 0, beta);
    }

    /// <summary>
    /// The per-lane parameters of <see cref="FilterTaps"/> for lanes first .. first + count - 1:
    /// bS, tC0 (scaled by 2^<paramref name="tcShift"/>, BitDepth - 8), alpha and beta, each a row of 16 in <paramref name="lanes"/>.
    /// </summary>
    private static void SetLanes(Span<short> lanes, int first, int count, ReadOnlySpan<byte> strengths, int segmentShift, int indexA, int alpha, int beta, int tcShift)
    {
        // bS and tC0 are the same along a segment (4 luma or 2 chroma samples), alpha and beta
        // along the edge: eight lanes at a time.
        var alphaV = Vector128.Create((short)alpha);
        var betaV = Vector128.Create((short)beta);
        for (int lane = 0; lane < count; lane += 8)
        {
            int s = lane >> segmentShift;
            short b0 = strengths[s];
            short b1 = strengths[s + 1];
            Vector128<short> bs;
            Vector128<short> tc;
            if (segmentShift == 2)
            {
                bs = Vector128.Create(b0, b0, b0, b0, b1, b1, b1, b1);
                short t0 = Tc0Of(b0, indexA);
                short t1 = Tc0Of(b1, indexA);
                tc = Vector128.Create(t0, t0, t0, t0, t1, t1, t1, t1);
            }
            else
            {
                short b2 = strengths[s + 2];
                short b3 = strengths[s + 3];
                bs = Vector128.Create(b0, b0, b1, b1, b2, b2, b3, b3);
                short t0 = Tc0Of(b0, indexA);
                short t1 = Tc0Of(b1, indexA);
                short t2 = Tc0Of(b2, indexA);
                short t3 = Tc0Of(b3, indexA);
                tc = Vector128.Create(t0, t0, t1, t1, t2, t2, t3, t3);
            }

            bs.CopyTo(lanes[(first + lane)..]);
            (tc << tcShift).CopyTo(lanes[(16 + first + lane)..]);
            alphaV.CopyTo(lanes[(32 + first + lane)..]);
            betaV.CopyTo(lanes[(48 + first + lane)..]);
        }
    }

    /// <summary>tC0 (Table 8-17) for bS 1 to 3; 0 for the other strengths, which do not use it.</summary>
    private static short Tc0Of(int bS, int indexA) => bS is > 0 and < 4 ? Tc0[(indexA * 3) + bS - 1] : (short)0;

    /// <summary>
    /// The filter (8.7.2.3, 8.7.2.4) on <paramref name="length"/> positions of gathered 16-bit
    /// taps (taps[k * 16 + i]: sample k, 0 = p3 .. 7 = q3, at position i), eight lanes at a time,
    /// with the lane parameters of <see cref="SetLanes"/>; results clipped to 0 .. <paramref name="maxSample"/>.
    /// </summary>
    private static void FilterTaps(Span<short> taps, int length, ReadOnlySpan<short> lanes, bool chroma, short maxSample)
    {
        var maxV = Vector128.Create(maxSample);
        for (int h = 0; h < length; h += 8)
        {
            var bs = Vector128.Create(lanes.Slice(h, 8));
            var tc0 = Vector128.Create(lanes.Slice(16 + h, 8));
            var alphaV = Vector128.Create(lanes.Slice(32 + h, 8));
            var betaV = Vector128.Create(lanes.Slice(48 + h, 8));
            EdgeLanes t;
            t.P1 = LoadTap(taps, 2, h);
            t.P0 = LoadTap(taps, 3, h);
            t.Q0 = LoadTap(taps, 4, h);
            t.Q1 = LoadTap(taps, 5, h);
            if (chroma)
            {
                t.P3 = t.P2 = t.Q2 = t.Q3 = default;
            }
            else
            {
                t.P3 = LoadTap(taps, 0, h);
                t.P2 = LoadTap(taps, 1, h);
                t.Q2 = LoadTap(taps, 6, h);
                t.Q3 = LoadTap(taps, 7, h);
            }

            if (!FilterLanes(ref t, bs, tc0, alphaV, betaV, chroma, maxV))
            {
                continue;
            }

            if (!chroma)
            {
                StoreTap(taps, 1, h, maxV, t.P2);
                StoreTap(taps, 2, h, maxV, t.P1);
                StoreTap(taps, 5, h, maxV, t.Q1);
                StoreTap(taps, 6, h, maxV, t.Q2);
            }

            StoreTap(taps, 3, h, maxV, t.P0);
            StoreTap(taps, 4, h, maxV, t.Q0);
        }
    }

    /// <summary>p3 .. q3 at eight positions along an edge, in 16-bit lanes.</summary>
    private struct EdgeLanes
    {
        public Vector128<short> P3;
        public Vector128<short> P2;
        public Vector128<short> P1;
        public Vector128<short> P0;
        public Vector128<short> Q0;
        public Vector128<short> Q1;
        public Vector128<short> Q2;
        public Vector128<short> Q3;
    }

    /// <summary>
    /// The filter (8.7.2.3, 8.7.2.4) on eight positions with per-lane bS, tC0, alpha and beta:
    /// replaces p2 .. q2 (luma) or p0, q0 (chroma, which reads only p1 .. q1) with their
    /// filtered values, which the caller clamps to 0 .. <paramref name="maxV"/> as it stores them.
    /// False, and nothing changed, when no lane passes the filter's tests.
    /// </summary>
    /// <remarks>
    /// In parts small enough for the JIT to inline (it does not inline a method with as many
    /// locals as the whole filter has), so the lanes stay in registers.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FilterLanes(ref EdgeLanes t, Vector128<short> bs, Vector128<short> tc0, Vector128<short> alphaV, Vector128<short> betaV, bool chroma, Vector128<short> maxV)
    {
        var filter = Vector128.GreaterThan(bs, Vector128<short>.Zero)
            & Vector128.LessThan(Vector128.Abs(t.P0 - t.Q0), alphaV)
            & Vector128.LessThan(Vector128.Abs(t.P1 - t.P0), betaV)
            & Vector128.LessThan(Vector128.Abs(t.Q1 - t.Q0), betaV);
        if (filter == Vector128<short>.Zero)
        {
            return false;
        }

        var strong = filter & Vector128.Equals(bs, Vector128.Create((short)4));
        if (chroma)
        {
            FilterChromaLanes(ref t, filter, strong, tc0, maxV);
        }
        else if (strong == Vector128<short>.Zero)
        {
            // Only bS < 4 here, which is every internal edge.
            FilterNormalLanes(ref t, filter, tc0, betaV, maxV);
        }
        else
        {
            var original = t;
            FilterNormalLanes(ref t, filter & ~strong, tc0, betaV, maxV);
            FilterStrongLanes(ref t, in original, strong, alphaV, betaV);
        }

        return true;
    }

    /// <summary>Chroma: p0 and q0 by bS &lt; 4 (8-470) or bS 4 (8-480), where <paramref name="filter"/> is set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterChromaLanes(ref EdgeLanes t, Vector128<short> filter, Vector128<short> strong, Vector128<short> tc0, Vector128<short> maxV)
    {
        var (p1, p0, q0, q1) = (t.P1, t.P0, t.Q0, t.Q1);
        var two = Vector128.Create((short)2);
        var tc = tc0 + Vector128<short>.One;
        var delta = Vector128.Clamp((((q0 - p0) << 2) + (p1 - q1) + Vector128.Create((short)4)) >> 3, -tc, tc);
        var p0n = Vector128.ConditionalSelect(strong, ((p1 << 1) + p0 + q1 + two) >> 2, Vector128.Clamp(p0 + delta, Vector128<short>.Zero, maxV));
        var q0n = Vector128.ConditionalSelect(strong, ((q1 << 1) + q0 + p1 + two) >> 2, Vector128.Clamp(q0 - delta, Vector128<short>.Zero, maxV));
        t.P0 = Vector128.ConditionalSelect(filter, p0n, p0);
        t.Q0 = Vector128.ConditionalSelect(filter, q0n, q0);
    }

    /// <summary>Luma with bS &lt; 4 (8.7.2.3): p1 .. q1, where <paramref name="filter"/> is set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterNormalLanes(ref EdgeLanes t, Vector128<short> filter, Vector128<short> tc0, Vector128<short> betaV, Vector128<short> maxV)
    {
        var (p2, p1, p0, q0, q1, q2) = (t.P2, t.P1, t.P0, t.Q0, t.Q1, t.Q2);
        var ap = Vector128.LessThan(Vector128.Abs(p2 - p0), betaV);
        var aq = Vector128.LessThan(Vector128.Abs(q2 - q0), betaV);

        // The masks are -1 where true, so subtracting them adds one.
        var tc = tc0 - ap - aq;
        var delta = Vector128.Clamp((((q0 - p0) << 2) + (p1 - q1) + Vector128.Create((short)4)) >> 3, -tc, tc);
        var average = (p0 + q0 + Vector128<short>.One) >> 1;
        t.P1 = Vector128.ConditionalSelect(filter & ap, p1 + Vector128.Clamp((p2 + average - (p1 << 1)) >> 1, -tc0, tc0), p1);
        t.Q1 = Vector128.ConditionalSelect(filter & aq, q1 + Vector128.Clamp((q2 + average - (q1 << 1)) >> 1, -tc0, tc0), q1);
        t.P0 = Vector128.ConditionalSelect(filter, Vector128.Clamp(p0 + delta, Vector128<short>.Zero, maxV), p0);
        t.Q0 = Vector128.ConditionalSelect(filter, Vector128.Clamp(q0 - delta, Vector128<short>.Zero, maxV), q0);
    }

    /// <summary>Luma with bS 4 (8.7.2.4): p2 .. q2 from the <paramref name="original"/> samples, where <paramref name="strong"/> is set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterStrongLanes(ref EdgeLanes t, in EdgeLanes original, Vector128<short> strong, Vector128<short> alphaV, Vector128<short> betaV)
    {
        var (p3, p2, p1, p0, q0, q1, q2, q3) = (original.P3, original.P2, original.P1, original.P0, original.Q0, original.Q1, original.Q2, original.Q3);
        var two = Vector128.Create((short)2);
        var four = Vector128.Create((short)4);
        var small = Vector128.LessThan(Vector128.Abs(p0 - q0), (alphaV >> 2) + two);
        var sp = strong & small & Vector128.LessThan(Vector128.Abs(p2 - p0), betaV);
        var sq = strong & small & Vector128.LessThan(Vector128.Abs(q2 - q0), betaV);
        t.P0 = Vector128.ConditionalSelect(strong, Vector128.ConditionalSelect(sp, (p2 + (p1 << 1) + (p0 << 1) + (q0 << 1) + q1 + four) >> 3, ((p1 << 1) + p0 + q1 + two) >> 2), t.P0);
        t.P1 = Vector128.ConditionalSelect(sp, (p2 + p1 + p0 + q0 + two) >> 2, t.P1);
        t.P2 = Vector128.ConditionalSelect(sp, ((p3 << 1) + (p2 * 3) + p1 + p0 + q0 + four) >> 3, p2);
        t.Q0 = Vector128.ConditionalSelect(strong, Vector128.ConditionalSelect(sq, (p1 + (p0 << 1) + (q0 << 1) + (q1 << 1) + q2 + four) >> 3, ((q1 << 1) + q0 + p1 + two) >> 2), t.Q0);
        t.Q1 = Vector128.ConditionalSelect(sq, (p0 + q0 + q1 + q2 + two) >> 2, t.Q1);
        t.Q2 = Vector128.ConditionalSelect(sq, ((q3 << 1) + (q2 * 3) + q1 + q0 + p0 + four) >> 3, q2);
    }

    /// <summary>
    /// A horizontal edge of 16 or 8 8-bit samples, filtered in place: the rows p3 .. q3 (luma) or
    /// p1 .. q1 (chroma) loaded whole from the plane.
    /// </summary>
    private static void FilterRows(byte[] plane, int q0, int stride, int length, Vector128<byte> bs, in EdgeParameters parameters, bool chroma)
    {
        // Rows q0 - reach * stride .. q0 + (reach - 1) * stride, length bytes each.
        int reach = chroma ? 2 : 4;
        if (q0 - (reach * stride) < 0 || q0 + ((reach - 1) * stride) + length > plane.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(q0));
        }

        // With 8 samples the upper lanes hold zeros: filtered (or not) for nothing, and never stored.
        ref byte origin = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(plane), q0);
        EdgeBytes taps = default;
        if (!chroma)
        {
            taps.P3 = LoadRow(ref origin, -4 * stride, length);
            taps.P2 = LoadRow(ref origin, -3 * stride, length);
            taps.Q2 = LoadRow(ref origin, 2 * stride, length);
            taps.Q3 = LoadRow(ref origin, 3 * stride, length);
        }

        taps.P1 = LoadRow(ref origin, -2 * stride, length);
        taps.P0 = LoadRow(ref origin, -stride, length);
        taps.Q0 = LoadRow(ref origin, 0, length);
        taps.Q1 = LoadRow(ref origin, stride, length);
        if (!FilterBytes(ref taps, bs, parameters, parameters, chroma))
        {
            return;
        }

        if (!chroma)
        {
            StoreRow(taps.P2, ref origin, -3 * stride, length);
            StoreRow(taps.P1, ref origin, -2 * stride, length);
            StoreRow(taps.Q1, ref origin, stride, length);
            StoreRow(taps.Q2, ref origin, 2 * stride, length);
        }

        StoreRow(taps.P0, ref origin, -stride, length);
        StoreRow(taps.Q0, ref origin, 0, length);

        static Vector128<byte> LoadRow(ref byte origin, int offset, int length) =>
            length == 16 ? Vector128.LoadUnsafe(ref Unsafe.Add(ref origin, offset)) : Row8(ref origin, offset);

        static void StoreRow(Vector128<byte> value, ref byte origin, int offset, int length)
        {
            if (length == 16)
            {
                value.StoreUnsafe(ref Unsafe.Add(ref origin, offset));
            }
            else
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref origin, offset), value.AsUInt64().ToScalar());
            }
        }
    }

    /// <summary>The segment (0 .. 3) of each of 16 lanes: four lanes to a luma segment.</summary>
    private static Vector128<byte> LumaSegments => Vector128.Create((byte)0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3);

    /// <summary>Two lanes to a chroma segment, in each half (4:2:0's Cb and Cr).</summary>
    private static Vector128<byte> ChromaSegments => Vector128.Create((byte)0, 0, 1, 1, 2, 2, 3, 3, 0, 0, 1, 1, 2, 2, 3, 3);

    /// <summary>bS of each lane: the four <paramref name="strengths"/> spread by <paramref name="segments"/>.</summary>
    private static Vector128<byte> SegmentLanes(ReadOnlySpan<byte> strengths, Vector128<byte> segments) =>
        Vector128.Shuffle(Vector128.CreateScalarUnsafe(MemoryMarshal.Read<uint>(strengths)).AsByte(), segments);

    /// <summary>
    /// A vertical edge of 16 8-bit rows, filtered in place: rows 0-7 from <paramref name="top"/> and
    /// 8-15 from <paramref name="bottom"/> at <paramref name="start"/> (p3 of the first row), each
    /// row's eight bytes p3 .. q3 transposed in registers into eight tap vectors of 16 lanes. Luma
    /// (one plane, the bottom half 8 rows down, bS four rows to a segment) or chroma: 4:2:2's tall
    /// edges the same way, 4:2:0's Cb in the top half and Cr in the bottom, two rows to a segment,
    /// each with its own parameters (<paramref name="low"/> for lanes 0-7, <paramref name="high"/> for 8-15).
    /// </summary>
    private static void FilterColumns(byte[] top, byte[] bottom, int start, int bottomStart, int stride, Vector128<byte> bs, in EdgeParameters low, in EdgeParameters high, bool chroma)
    {
        // Eight rows of eight bytes from each start.
        if (start < 0 || bottomStart < 0 || start + (7 * stride) + 8 > top.Length || bottomStart + (7 * stride) + 8 > bottom.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        ref byte upper = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(top), start);
        ref byte lower = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bottom), bottomStart);
        var taps = Sse2.IsSupported ? Columns<Sse2Zip>(ref upper, ref lower, stride) : Columns<PortableZip>(ref upper, ref lower, stride);
        if (!FilterBytes(ref taps, bs, low, high, chroma))
        {
            return;
        }

        if (Sse2.IsSupported)
        {
            WriteColumns<Sse2Zip>(in taps, ref upper, ref lower, stride);
        }
        else
        {
            WriteColumns<PortableZip>(in taps, ref upper, ref lower, stride);
        }
    }

    /// <summary>The 4:2:0 chroma edge of 8 rows or columns of Cb (lanes 0-7) and Cr (lanes 8-15), filtered in place.</summary>
    private static void FilterChromaRows(byte[] cb, byte[] cr, int q0, int stride, Vector128<byte> bs, in EdgeParameters low, in EdgeParameters high)
    {
        // Rows p1 .. q1, eight bytes each.
        if (q0 - (2 * stride) < 0 || q0 + stride + 8 > cb.Length || q0 + stride + 8 > cr.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(q0));
        }

        ref byte b = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(cb), q0);
        ref byte r = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(cr), q0);
        EdgeBytes taps = default;
        taps.P1 = Pair(ref Unsafe.Subtract(ref b, 2 * stride), ref Unsafe.Subtract(ref r, 2 * stride));
        taps.P0 = Pair(ref Unsafe.Subtract(ref b, stride), ref Unsafe.Subtract(ref r, stride));
        taps.Q0 = Pair(ref b, ref r);
        taps.Q1 = Pair(ref Unsafe.Add(ref b, stride), ref Unsafe.Add(ref r, stride));
        if (FilterBytes(ref taps, bs, low, high, chroma: true))
        {
            WriteHalves(taps.P0, ref Unsafe.Subtract(ref b, stride), ref Unsafe.Subtract(ref r, stride));
            WriteHalves(taps.Q0, ref b, ref r);
        }

        static Vector128<byte> Pair(ref byte first, ref byte second) =>
            Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref first), Unsafe.ReadUnaligned<ulong>(ref second)).AsByte();

        static void WriteHalves(Vector128<byte> value, ref byte first, ref byte second)
        {
            Unsafe.WriteUnaligned(ref first, value.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref second, value.AsUInt64().GetElement(1));
        }
    }

    /// <summary>bS, tC0, alpha and beta for eight lanes, from one indexA's row of the tables.</summary>
    // EdgeParameters.Tc0Row by indexA, made once: building the vector lane by lane for every edge cost about 1% of
    // decoding.
    private static readonly Vector128<byte>[] Tc0Rows =
        [.. Enumerable.Range(0, 52).Select(i => Vector128.Create(0, Tc0[i * 3], Tc0[(i * 3) + 1], Tc0[(i * 3) + 2], 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0))];

    private readonly struct EdgeParameters(int indexA, int alpha, int beta)
    {
        /// <summary>tC0 by bS (0 for bS 0 and 4), lanes 0 .. 4.</summary>
        public readonly Vector128<byte> Tc0Row = Tc0Rows[indexA];
        public readonly Vector128<short> Alpha = Vector128.Create((short)alpha);
        public readonly Vector128<short> Beta = Vector128.Create((short)beta);
    }

    /// <summary>p3 .. q3 at 16 positions along an edge, 8-bit samples.</summary>
    private struct EdgeBytes
    {
        public Vector128<byte> P3;
        public Vector128<byte> P2;
        public Vector128<byte> P1;
        public Vector128<byte> P0;
        public Vector128<byte> Q0;
        public Vector128<byte> Q1;
        public Vector128<byte> Q2;
        public Vector128<byte> Q3;
    }

    /// <summary>
    /// <see cref="FilterLanes"/> on 16 8-bit lanes, widened in two halves of eight with their own
    /// parameters; <paramref name="bs"/> per lane. False, and nothing changed, when no lane is filtered.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FilterBytes(ref EdgeBytes taps, Vector128<byte> bs, in EdgeParameters low, in EdgeParameters high, bool chroma)
    {
        var maxV = Vector128.Create((short)255);
        var tcLow = Vector128.Shuffle(low.Tc0Row, bs);
        var tcHigh = Vector128.Shuffle(high.Tc0Row, bs);

        EdgeLanes lo;
        lo.P3 = Lower(taps.P3);
        lo.P2 = Lower(taps.P2);
        lo.P1 = Lower(taps.P1);
        lo.P0 = Lower(taps.P0);
        lo.Q0 = Lower(taps.Q0);
        lo.Q1 = Lower(taps.Q1);
        lo.Q2 = Lower(taps.Q2);
        lo.Q3 = Lower(taps.Q3);
        bool lowChanged = FilterLanes(ref lo, Lower(bs), Lower(tcLow), low.Alpha, low.Beta, chroma, maxV);

        EdgeLanes hi;
        hi.P3 = Upper(taps.P3);
        hi.P2 = Upper(taps.P2);
        hi.P1 = Upper(taps.P1);
        hi.P0 = Upper(taps.P0);
        hi.Q0 = Upper(taps.Q0);
        hi.Q1 = Upper(taps.Q1);
        hi.Q2 = Upper(taps.Q2);
        hi.Q3 = Upper(taps.Q3);
        bool highChanged = FilterLanes(ref hi, Upper(bs), Upper(tcHigh), high.Alpha, high.Beta, chroma, maxV);
        if (!lowChanged && !highChanged)
        {
            return false;
        }

        // Lanes that did not change keep their values, which are in range.
        taps.P0 = Pack(lo.P0, hi.P0);
        taps.Q0 = Pack(lo.Q0, hi.Q0);
        if (!chroma)
        {
            taps.P2 = Pack(lo.P2, hi.P2);
            taps.P1 = Pack(lo.P1, hi.P1);
            taps.Q1 = Pack(lo.Q1, hi.Q1);
            taps.Q2 = Pack(lo.Q2, hi.Q2);
        }

        return true;
    }

    /// <summary>
    /// Rows 0-7 from <paramref name="top"/> and 8-15 from <paramref name="bottom"/>, eight bytes
    /// each, transposed: byte k of every row in tap k (8-bit, 16-bit, 32-bit then 64-bit interleaves).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EdgeBytes Columns<TZip>(ref byte top, ref byte bottom, int stride)
        where TZip : struct, IZip
    {
        // pairs: bytes of rows 2k and 2k + 1 interleaved.
        var a0 = TZip.Low(Row8(ref top, 0), Row8(ref top, stride));
        var a1 = TZip.Low(Row8(ref top, 2 * stride), Row8(ref top, 3 * stride));
        var a2 = TZip.Low(Row8(ref top, 4 * stride), Row8(ref top, 5 * stride));
        var a3 = TZip.Low(Row8(ref top, 6 * stride), Row8(ref top, 7 * stride));
        var a4 = TZip.Low(Row8(ref bottom, 0), Row8(ref bottom, stride));
        var a5 = TZip.Low(Row8(ref bottom, 2 * stride), Row8(ref bottom, 3 * stride));
        var a6 = TZip.Low(Row8(ref bottom, 4 * stride), Row8(ref bottom, 5 * stride));
        var a7 = TZip.Low(Row8(ref bottom, 6 * stride), Row8(ref bottom, 7 * stride));

        // quads: bytes 0-3 (Low) or 4-7 (High) of four rows, one 32-bit group per byte.
        var b0 = TZip.Low(a0.AsUInt16(), a1.AsUInt16()).AsUInt32();
        var b1 = TZip.Low(a2.AsUInt16(), a3.AsUInt16()).AsUInt32();
        var b2 = TZip.Low(a4.AsUInt16(), a5.AsUInt16()).AsUInt32();
        var b3 = TZip.Low(a6.AsUInt16(), a7.AsUInt16()).AsUInt32();
        var c0 = TZip.High(a0.AsUInt16(), a1.AsUInt16()).AsUInt32();
        var c1 = TZip.High(a2.AsUInt16(), a3.AsUInt16()).AsUInt32();
        var c2 = TZip.High(a4.AsUInt16(), a5.AsUInt16()).AsUInt32();
        var c3 = TZip.High(a6.AsUInt16(), a7.AsUInt16()).AsUInt32();

        // One byte position of eight rows in each 64-bit group.
        EdgeBytes taps;
        var upper = TZip.Low(b0, b1).AsUInt64();
        var upperHigh = TZip.High(b0, b1).AsUInt64();
        var lowerRows = TZip.Low(b2, b3).AsUInt64();
        var lowerHigh = TZip.High(b2, b3).AsUInt64();
        taps.P3 = TZip.Low(upper, lowerRows).AsByte();
        taps.P2 = TZip.High(upper, lowerRows).AsByte();
        taps.P1 = TZip.Low(upperHigh, lowerHigh).AsByte();
        taps.P0 = TZip.High(upperHigh, lowerHigh).AsByte();
        upper = TZip.Low(c0, c1).AsUInt64();
        upperHigh = TZip.High(c0, c1).AsUInt64();
        lowerRows = TZip.Low(c2, c3).AsUInt64();
        lowerHigh = TZip.High(c2, c3).AsUInt64();
        taps.Q0 = TZip.Low(upper, lowerRows).AsByte();
        taps.Q1 = TZip.High(upper, lowerRows).AsByte();
        taps.Q2 = TZip.Low(upperHigh, lowerHigh).AsByte();
        taps.Q3 = TZip.High(upperHigh, lowerHigh).AsByte();
        return taps;
    }

    /// <summary>The inverse of <see cref="Columns"/>: writes all eight bytes of each of the 16 rows.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteColumns<TZip>(in EdgeBytes taps, ref byte top, ref byte bottom, int stride)
        where TZip : struct, IZip
    {
        // Bytes 2k and 2k + 1 of rows 0-7 (Low) and 8-15 (High).
        var l0 = TZip.Low(taps.P3, taps.P2).AsUInt16();
        var l1 = TZip.Low(taps.P1, taps.P0).AsUInt16();
        var l2 = TZip.Low(taps.Q0, taps.Q1).AsUInt16();
        var l3 = TZip.Low(taps.Q2, taps.Q3).AsUInt16();
        var h0 = TZip.High(taps.P3, taps.P2).AsUInt16();
        var h1 = TZip.High(taps.P1, taps.P0).AsUInt16();
        var h2 = TZip.High(taps.Q0, taps.Q1).AsUInt16();
        var h3 = TZip.High(taps.Q2, taps.Q3).AsUInt16();
        WriteHalf(ref top, stride, l0, l1, l2, l3);
        WriteHalf(ref bottom, stride, h0, h1, h2, h3);

        static void WriteHalf(ref byte rows, int stride, Vector128<ushort> s0, Vector128<ushort> s1, Vector128<ushort> s2, Vector128<ushort> s3)
        {
            // 32-bit groups: bytes 0-3 / 4-7 of one row.
            var left = TZip.Low(s0, s1).AsUInt32();
            var leftHigh = TZip.High(s0, s1).AsUInt32();
            var right = TZip.Low(s2, s3).AsUInt32();
            var rightHigh = TZip.High(s2, s3).AsUInt32();
            WriteRowPair(ref rows, 0, stride, TZip.Low(left, right).AsUInt64());
            WriteRowPair(ref rows, 2 * stride, stride, TZip.High(left, right).AsUInt64());
            WriteRowPair(ref rows, 4 * stride, stride, TZip.Low(leftHigh, rightHigh).AsUInt64());
            WriteRowPair(ref rows, 6 * stride, stride, TZip.High(leftHigh, rightHigh).AsUInt64());
        }
    }

    private static Vector128<byte> Row8(ref byte rows, int offset) =>
        Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rows, offset))).AsByte();

    private static void WriteRowPair(ref byte rows, int offset, int stride, Vector128<ulong> pair)
    {
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref rows, offset), pair.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref rows, offset + stride), pair.GetElement(1));
    }

    /// <summary>
    /// The interleaves of the transposes, as SSE2's unpacks: SSE2 itself (<see cref="Sse2Zip"/>) or
    /// <see cref="VectorZip"/>'s portable operations (<see cref="PortableZip"/>, what ARM64 runs).
    /// </summary>
    internal interface IZip
    {
        static abstract Vector128<byte> Low(Vector128<byte> a, Vector128<byte> b);

        static abstract Vector128<byte> High(Vector128<byte> a, Vector128<byte> b);

        static abstract Vector128<ushort> Low(Vector128<ushort> a, Vector128<ushort> b);

        static abstract Vector128<ushort> High(Vector128<ushort> a, Vector128<ushort> b);

        static abstract Vector128<uint> Low(Vector128<uint> a, Vector128<uint> b);

        static abstract Vector128<uint> High(Vector128<uint> a, Vector128<uint> b);

        static abstract Vector128<ulong> Low(Vector128<ulong> a, Vector128<ulong> b);

        static abstract Vector128<ulong> High(Vector128<ulong> a, Vector128<ulong> b);
    }

    internal readonly struct Sse2Zip : IZip
    {
        /// <inheritdoc/>
        public static Vector128<byte> Low(Vector128<byte> a, Vector128<byte> b) => Sse2.UnpackLow(a, b);

        /// <inheritdoc/>
        public static Vector128<byte> High(Vector128<byte> a, Vector128<byte> b) => Sse2.UnpackHigh(a, b);

        /// <inheritdoc/>
        public static Vector128<ushort> Low(Vector128<ushort> a, Vector128<ushort> b) => Sse2.UnpackLow(a, b);

        /// <inheritdoc/>
        public static Vector128<ushort> High(Vector128<ushort> a, Vector128<ushort> b) => Sse2.UnpackHigh(a, b);

        /// <inheritdoc/>
        public static Vector128<uint> Low(Vector128<uint> a, Vector128<uint> b) => Sse2.UnpackLow(a, b);

        /// <inheritdoc/>
        public static Vector128<uint> High(Vector128<uint> a, Vector128<uint> b) => Sse2.UnpackHigh(a, b);

        /// <inheritdoc/>
        public static Vector128<ulong> Low(Vector128<ulong> a, Vector128<ulong> b) => Sse2.UnpackLow(a, b);

        /// <inheritdoc/>
        public static Vector128<ulong> High(Vector128<ulong> a, Vector128<ulong> b) => Sse2.UnpackHigh(a, b);
    }

    internal readonly struct PortableZip : IZip
    {
        /// <inheritdoc/>
        public static Vector128<byte> Low(Vector128<byte> a, Vector128<byte> b) => VectorZip.Low(a, b);

        /// <inheritdoc/>
        public static Vector128<byte> High(Vector128<byte> a, Vector128<byte> b) => VectorZip.High(a, b);

        /// <inheritdoc/>
        public static Vector128<ushort> Low(Vector128<ushort> a, Vector128<ushort> b) => VectorZip.Low(a, b);

        /// <inheritdoc/>
        public static Vector128<ushort> High(Vector128<ushort> a, Vector128<ushort> b) => VectorZip.High(a, b);

        /// <inheritdoc/>
        public static Vector128<uint> Low(Vector128<uint> a, Vector128<uint> b) => VectorZip.Low(a, b);

        /// <inheritdoc/>
        public static Vector128<uint> High(Vector128<uint> a, Vector128<uint> b) => VectorZip.High(a, b);

        /// <inheritdoc/>
        public static Vector128<ulong> Low(Vector128<ulong> a, Vector128<ulong> b) => VectorZip.Low(a, b);

        /// <inheritdoc/>
        public static Vector128<ulong> High(Vector128<ulong> a, Vector128<ulong> b) => VectorZip.High(a, b);
    }

    /// <summary>
    /// <see cref="Columns"/> with <see cref="PortableZip"/>, into <paramref name="taps"/> (tap k at
    /// k * 16): the transpose ARM64 runs, tested against its definition on x64 too.
    /// </summary>
    internal static void TransposeInVector128(byte[] top, int topStart, byte[] bottom, int bottomStart, int stride, Span<byte> taps)
    {
        var columns = Columns<PortableZip>(ref top[topStart], ref bottom[bottomStart], stride);
        ReadOnlySpan<Vector128<byte>> rows = [columns.P3, columns.P2, columns.P1, columns.P0, columns.Q0, columns.Q1, columns.Q2, columns.Q3];
        MemoryMarshal.AsBytes(rows).CopyTo(taps);
    }

    /// <summary><see cref="WriteColumns"/> with <see cref="PortableZip"/>, from taps as <see cref="TransposeInVector128"/> writes them.</summary>
    internal static void TransposeOutVector128(ReadOnlySpan<byte> taps, byte[] top, int topStart, byte[] bottom, int bottomStart, int stride)
    {
        EdgeBytes columns;
        columns.P3 = Vector128.Create(taps[..16]);
        columns.P2 = Vector128.Create(taps.Slice(16, 16));
        columns.P1 = Vector128.Create(taps.Slice(32, 16));
        columns.P0 = Vector128.Create(taps.Slice(48, 16));
        columns.Q0 = Vector128.Create(taps.Slice(64, 16));
        columns.Q1 = Vector128.Create(taps.Slice(80, 16));
        columns.Q2 = Vector128.Create(taps.Slice(96, 16));
        columns.Q3 = Vector128.Create(taps.Slice(112, 16));
        WriteColumns<PortableZip>(in columns, ref top[topStart], ref bottom[bottomStart], stride);
    }

    private static Vector128<short> Lower(Vector128<byte> value) => Vector128.WidenLower(value).AsInt16();

    private static Vector128<short> Upper(Vector128<byte> value) => Vector128.WidenUpper(value).AsInt16();

    /// <summary>Two vectors of eight 16-bit samples, clipped to 0 .. 255, as sixteen bytes.</summary>
    private static Vector128<byte> Pack(Vector128<short> low, Vector128<short> high) =>
        Vector128.Narrow(Clip(low).AsUInt16(), Clip(high).AsUInt16());

    private static Vector128<short> LoadTap(Span<short> taps, int tap, int offset) => Vector128.Create(taps.Slice((tap * 16) + offset, 8));

    private static void StoreTap(Span<short> taps, int tap, int offset, Vector128<short> max, Vector128<short> value) =>
        Vector128.Clamp(value, Vector128<short>.Zero, max).CopyTo(taps[((tap * 16) + offset)..]);

    private static Vector128<short> Clip(Vector128<short> value) =>
        Vector128.Clamp(value, Vector128<short>.Zero, Vector128.Create((short)255));
}

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

    /// <summary>Filters the whole picture, macroblock by macroblock in raster order (8.7).</summary>
    [SkipLocalsInit]
    public void Deblock()
    {
        if (_pairSlots)
        {
            DeblockMbaff();
            return;
        }

        // bS per edge (0..3) and per 4-sample segment along it, for vertical and horizontal edges.
        Span<byte> vertical = stackalloc byte[16];
        Span<byte> horizontal = stackalloc byte[16];

        for (int mbAddr = 0; mbAddr < _mbCount; mbAddr++)
        {
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

            // Macroblock edges (edge 0) depend on the neighbour.
            for (int k = 0; k < 4; k++)
            {
                vertical[k] = (byte)(left ? BoundaryStrength(mbAddr - 1, (k * 4) + 3, mbAddr, k * 4, mbEdge: true) : 0);
                horizontal[k] = (byte)(top ? BoundaryStrength(mbAddr - _widthInMbs, 12 + k, mbAddr, k, mbEdge: true) : 0);
            }

            // Internal edges: fixed for intra macroblocks and for macroblocks with uniform motion
            // and no luma coefficients; otherwise block by block.
            if (IsIntra(mbAddr))
            {
                vertical[4..].Fill(3);
                horizontal[4..].Fill(3);
            }
            else if (HasUniformMotionWithoutCoefficients(mbAddr))
            {
                vertical[4..].Clear();
                horizontal[4..].Clear();
            }
            else
            {
                InternalStrengths(mbAddr, vertical, horizontal);
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

            if (_mbHeightC == 16)
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
    /// both blocks in the same macroblock, so neither is intra.
    /// </summary>
    private void InternalStrengths(int mbAddr, Span<byte> vertical, Span<byte> horizontal, int limitY = 4)
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

        for (int edge = 1; edge < 4; edge++)
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
        else if (typeof(TPixel) == typeof(ushort) && vectorized && Vector128.IsHardwareAccelerated)
        {
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
    /// The same filter on up to 16 positions at once. The samples across the edge (p3 .. q3) are
    /// gathered into rows, filtered in 16-bit lanes with per-lane bS, and written back.
    /// </summary>
    [SkipLocalsInit]
    private static void FilterEdgeVector(byte[] plane, int q0, int across, int along, int length, ReadOnlySpan<byte> strengths, int segmentShift, int indexA, int alpha, int beta, bool chroma)
    {
        // taps[k * 16 + i]: sample k (0 = p3, 3 = p0, 4 = q0, 7 = q3) at position i along the edge.
        Span<byte> taps = stackalloc byte[(8 * 16) + 16];
        int firstTap = chroma ? 2 : 0;
        int lastTap = chroma ? 5 : 7;
        Gather(plane, q0, across, along, length, taps, firstTap, lastTap);

        Span<short> lanes = stackalloc short[4 * 16];
        SetLanes(lanes, 0, length, strengths, segmentShift, indexA, alpha, beta, tcShift: 0);
        FilterTaps(taps, length, lanes, chroma, 255);

        // Only p2 .. q2 (luma) or p0, q0 (chroma) can change.
        Scatter(plane, q0, across, along, length, taps, chroma ? 3 : 1, chroma ? 4 : 6);
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

        Span<short> lanes = stackalloc short[4 * 16];
        bool any = false;
        for (int c = 0; c < 2; c++)
        {
            int qpAv = c == 0 ? (qpPCb + qpQCb + 1) >> 1 : (qpPCr + qpQCr + 1) >> 1;
            int indexA = Math.Clamp(qpAv + alphaOffset, 0, 51);
            int alpha = Alpha[indexA];
            int beta = Beta[Math.Clamp(qpAv + betaOffset, 0, 51)];
            any |= alpha != 0 && beta != 0;

            // alpha 0 (or beta 0) lets no sample of the component pass the filter's test.
            SetLanes(lanes, c * 8, 8, strengths, 1, indexA, beta == 0 ? 0 : alpha, beta, tcShift: 0);
        }

        if (!any)
        {
            return;
        }

        Span<byte> taps = stackalloc byte[(8 * 16) + 16];
        if (across == 1)
        {
            TransposeIn(cb, q0 - 4, cr, q0 - 4, along, taps);
        }
        else
        {
            for (int k = 2; k <= 5; k++)
            {
                int start = q0 + ((k - 4) * across);
                MemoryMarshal.Write(taps[(k * 16)..], MemoryMarshal.Read<ulong>(cb.AsSpan(start, 8)));
                MemoryMarshal.Write(taps[((k * 16) + 8)..], MemoryMarshal.Read<ulong>(cr.AsSpan(start, 8)));
            }
        }

        FilterTaps(taps, 16, lanes, chroma: true, 255);

        if (across == 1)
        {
            TransposeOut(taps, cb, q0 - 4, cr, q0 - 4, along);
        }
        else
        {
            for (int k = 3; k <= 4; k++)
            {
                int start = q0 + ((k - 4) * across);
                MemoryMarshal.Write(cb.AsSpan(start, 8), MemoryMarshal.Read<ulong>(taps[(k * 16)..]));
                MemoryMarshal.Write(cr.AsSpan(start, 8), MemoryMarshal.Read<ulong>(taps[((k * 16) + 8)..]));
            }
        }
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
    /// The filter (8.7.2.3, 8.7.2.4) on <paramref name="length"/> positions of gathered taps, eight
    /// 16-bit lanes at a time, with the lane parameters of <see cref="SetLanes"/>. The taps are
    /// bytes (8-bit samples, widened as they are loaded) or 16-bit samples up to <paramref name="maxSample"/>.
    /// </summary>
    private static void FilterTaps<TTap>(Span<TTap> taps, int length, ReadOnlySpan<short> lanes, bool chroma, short maxSample)
        where TTap : unmanaged
    {
        var maxV = Vector128.Create(typeof(TTap) == typeof(byte) ? (short)255 : maxSample);
        for (int h = 0; h < length; h += 8)
        {
            var bs = Vector128.Create(lanes.Slice(h, 8));
            var tc0 = Vector128.Create(lanes.Slice(16 + h, 8));
            var alphaV = Vector128.Create(lanes.Slice(32 + h, 8));
            var betaV = Vector128.Create(lanes.Slice(48 + h, 8));
            var p1 = LoadTap(taps, 2, h);
            var p0 = LoadTap(taps, 3, h);
            var q0v = LoadTap(taps, 4, h);
            var q1 = LoadTap(taps, 5, h);

            var filter = Vector128.GreaterThan(bs, Vector128<short>.Zero)
                & Vector128.LessThan(Vector128.Abs(p0 - q0v), alphaV)
                & Vector128.LessThan(Vector128.Abs(p1 - p0), betaV)
                & Vector128.LessThan(Vector128.Abs(q1 - q0v), betaV);
            if (filter == Vector128<short>.Zero)
            {
                continue;
            }

            var strong = Vector128.Equals(bs, Vector128.Create((short)4));
            var one = Vector128.Create((short)1);
            var two = Vector128.Create((short)2);
            var four = Vector128.Create((short)4);

            if (chroma)
            {
                var tc = tc0 + one;
                var delta = Vector128.Clamp((((q0v - p0) << 2) + (p1 - q1) + four) >> 3, -tc, tc);
                var p0n = Vector128.Clamp(p0 + delta, Vector128<short>.Zero, maxV);
                var q0n = Vector128.Clamp(q0v - delta, Vector128<short>.Zero, maxV);
                var p0s = ((p1 << 1) + p0 + q1 + two) >> 2;
                var q0s = ((q1 << 1) + q0v + p1 + two) >> 2;
                StoreTap(taps, 3, h, maxV, Vector128.ConditionalSelect(filter, Vector128.ConditionalSelect(strong, p0s, p0n), p0));
                StoreTap(taps, 4, h, maxV, Vector128.ConditionalSelect(filter, Vector128.ConditionalSelect(strong, q0s, q0n), q0v));
                continue;
            }

            var p3 = LoadTap(taps, 0, h);
            var p2 = LoadTap(taps, 1, h);
            var q2 = LoadTap(taps, 6, h);
            var q3 = LoadTap(taps, 7, h);
            var ap = Vector128.LessThan(Vector128.Abs(p2 - p0), betaV);
            var aq = Vector128.LessThan(Vector128.Abs(q2 - q0v), betaV);

            // bS < 4 (8.7.2.3); the masks are -1 where true, so subtracting them adds one.
            var tcL = tc0 - ap - aq;
            var deltaL = Vector128.Clamp((((q0v - p0) << 2) + (p1 - q1) + four) >> 3, -tcL, tcL);
            var p0Normal = Vector128.Clamp(p0 + deltaL, Vector128<short>.Zero, maxV);
            var q0Normal = Vector128.Clamp(q0v - deltaL, Vector128<short>.Zero, maxV);
            var average = (p0 + q0v + one) >> 1;
            var p1Normal = Vector128.ConditionalSelect(ap, p1 + Vector128.Clamp((p2 + average - (p1 << 1)) >> 1, -tc0, tc0), p1);
            var q1Normal = Vector128.ConditionalSelect(aq, q1 + Vector128.Clamp((q2 + average - (q1 << 1)) >> 1, -tc0, tc0), q1);

            var strongFilter = filter & strong;
            if (strongFilter == Vector128<short>.Zero)
            {
                // Only bS < 4 here, which is every internal edge.
                StoreTap(taps, 2, h, maxV, Vector128.ConditionalSelect(filter, p1Normal, p1));
                StoreTap(taps, 3, h, maxV, Vector128.ConditionalSelect(filter, p0Normal, p0));
                StoreTap(taps, 4, h, maxV, Vector128.ConditionalSelect(filter, q0Normal, q0v));
                StoreTap(taps, 5, h, maxV, Vector128.ConditionalSelect(filter, q1Normal, q1));
                continue;
            }

            // bS == 4 (8.7.2.4).
            var small = Vector128.LessThan(Vector128.Abs(p0 - q0v), (alphaV >> 2) + Vector128.Create((short)2));
            var sp = ap & small;
            var sq = aq & small;
            var p0Strong = Vector128.ConditionalSelect(sp, (p2 + (p1 << 1) + (p0 << 1) + (q0v << 1) + q1 + four) >> 3, ((p1 << 1) + p0 + q1 + two) >> 2);
            var p1Strong = Vector128.ConditionalSelect(sp, (p2 + p1 + p0 + q0v + two) >> 2, p1);
            var p2Strong = Vector128.ConditionalSelect(sp, ((p3 << 1) + (p2 * 3) + p1 + p0 + q0v + four) >> 3, p2);
            var q0Strong = Vector128.ConditionalSelect(sq, (p1 + (p0 << 1) + (q0v << 1) + (q1 << 1) + q2 + four) >> 3, ((q1 << 1) + q0v + p1 + two) >> 2);
            var q1Strong = Vector128.ConditionalSelect(sq, (p0 + q0v + q1 + q2 + two) >> 2, q1);
            var q2Strong = Vector128.ConditionalSelect(sq, ((q3 << 1) + (q2 * 3) + q1 + q0v + p0 + four) >> 3, q2);

            StoreTap(taps, 1, h, maxV, Vector128.ConditionalSelect(strongFilter, p2Strong, p2));
            StoreTap(taps, 2, h, maxV, Vector128.ConditionalSelect(filter, Vector128.ConditionalSelect(strong, p1Strong, p1Normal), p1));
            StoreTap(taps, 3, h, maxV, Vector128.ConditionalSelect(filter, Vector128.ConditionalSelect(strong, p0Strong, p0Normal), p0));
            StoreTap(taps, 4, h, maxV, Vector128.ConditionalSelect(filter, Vector128.ConditionalSelect(strong, q0Strong, q0Normal), q0v));
            StoreTap(taps, 5, h, maxV, Vector128.ConditionalSelect(filter, Vector128.ConditionalSelect(strong, q1Strong, q1Normal), q1));
            StoreTap(taps, 6, h, maxV, Vector128.ConditionalSelect(strongFilter, q2Strong, q2));
        }
    }

    private static void Gather(byte[] plane, int q0, int across, int along, int length, Span<byte> taps, int firstTap, int lastTap)
    {
        if (across == 1 && Sse2.IsSupported)
        {
            // Vertical edges: p3 .. q3 of each row are 8 contiguous bytes. Chroma needs only
            // p1 .. q1 but takes all eight; they lie inside this macroblock and its left neighbour.
            // With 8 rows the lanes 8-15 repeat rows 0-7 and are not used.
            int start = q0 - 4;
            TransposeIn(plane, start, plane, length == 16 ? start + (8 * along) : start, along, taps);
            return;
        }

        for (int k = firstTap; k <= lastTap; k++)
        {
            int start = q0 + ((k - 4) * across);
            var row = taps.Slice(k * 16, length);
            if (along == 1)
            {
                if (length == 16)
                {
                    Vector128.Create(plane.AsSpan(start, 16)).CopyTo(row);
                }
                else
                {
                    MemoryMarshal.Write(row, MemoryMarshal.Read<ulong>(plane.AsSpan(start, 8)));
                }

                continue;
            }

            for (int i = 0; i < length; i++)
            {
                row[i] = plane[start + (i * along)];
            }
        }
    }

    private static void Scatter(byte[] plane, int q0, int across, int along, int length, ReadOnlySpan<byte> taps, int firstTap, int lastTap)
    {
        if (across == 1 && length == 16 && firstTap == 1 && Sse2.IsSupported)
        {
            TransposeOut(taps, plane, q0 - 4, plane, q0 - 4 + (8 * along), along);
            return;
        }

        for (int k = firstTap; k <= lastTap; k++)
        {
            int start = q0 + ((k - 4) * across);
            var row = taps.Slice(k * 16, length);
            if (along == 1)
            {
                if (length == 16)
                {
                    Vector128.Create(row).CopyTo(plane.AsSpan(start, 16));
                }
                else
                {
                    MemoryMarshal.Write(plane.AsSpan(start, 8), MemoryMarshal.Read<ulong>(row));
                }

                continue;
            }

            for (int i = 0; i < length; i++)
            {
                plane[start + (i * along)] = row[i];
            }
        }
    }

    /// <summary>
    /// Vertical edge: 16 rows of p3 .. q3 (8 bytes each), rows 0-7 from <paramref name="top"/> at
    /// <paramref name="topStart"/> and rows 8-15 from <paramref name="bottom"/> at
    /// <paramref name="bottomStart"/>, become the 8 tap rows of 16 lanes, by interleaving at 8, 16,
    /// 32 and 64 bits.
    /// </summary>
    [SkipLocalsInit]
    private static void TransposeIn(byte[] top, int topStart, byte[] bottom, int bottomStart, int stride, Span<byte> taps)
    {
        Span<Vector128<byte>> pairs = stackalloc Vector128<byte>[8];
        for (int k = 0; k < 4; k++)
        {
            int even = topStart + (2 * k * stride);
            pairs[k] = Sse2.UnpackLow(Row8(top, even), Row8(top, even + stride));
            even = bottomStart + (2 * k * stride);
            pairs[k + 4] = Sse2.UnpackLow(Row8(bottom, even), Row8(bottom, even + stride));
        }

        // quads[m] / quadsHigh[m]: bytes 0-3 / 4-7 of rows 4m .. 4m + 3, one 32-bit group per byte.
        Span<Vector128<uint>> quads = stackalloc Vector128<uint>[4];
        Span<Vector128<uint>> quadsHigh = stackalloc Vector128<uint>[4];
        for (int m = 0; m < 4; m++)
        {
            quads[m] = Sse2.UnpackLow(pairs[2 * m].AsUInt16(), pairs[(2 * m) + 1].AsUInt16()).AsUInt32();
            quadsHigh[m] = Sse2.UnpackHigh(pairs[2 * m].AsUInt16(), pairs[(2 * m) + 1].AsUInt16()).AsUInt32();
        }

        for (int half = 0; half < 2; half++)
        {
            var source = half == 0 ? quads : quadsHigh;

            // Each 64-bit group: one byte position for 8 rows.
            var upper = Sse2.UnpackLow(source[0], source[1]).AsUInt64();
            var upperHigh = Sse2.UnpackHigh(source[0], source[1]).AsUInt64();
            var lower = Sse2.UnpackLow(source[2], source[3]).AsUInt64();
            var lowerHigh = Sse2.UnpackHigh(source[2], source[3]).AsUInt64();

            int tap = half * 4;
            Sse2.UnpackLow(upper, lower).AsByte().CopyTo(taps[(tap * 16)..]);
            Sse2.UnpackHigh(upper, lower).AsByte().CopyTo(taps[((tap + 1) * 16)..]);
            Sse2.UnpackLow(upperHigh, lowerHigh).AsByte().CopyTo(taps[((tap + 2) * 16)..]);
            Sse2.UnpackHigh(upperHigh, lowerHigh).AsByte().CopyTo(taps[((tap + 3) * 16)..]);
        }
    }

    /// <summary>The inverse of <see cref="TransposeIn"/>: writes all 8 bytes of each of the 16 rows.</summary>
    [SkipLocalsInit]
    private static void TransposeOut(ReadOnlySpan<byte> taps, byte[] top, int topStart, byte[] bottom, int bottomStart, int stride)
    {
        Span<Vector128<ushort>> low = stackalloc Vector128<ushort>[4];
        Span<Vector128<ushort>> high = stackalloc Vector128<ushort>[4];
        for (int k = 0; k < 4; k++)
        {
            var a = Vector128.Create(taps.Slice(2 * k * 16, 16));
            var b = Vector128.Create(taps.Slice(((2 * k) + 1) * 16, 16));
            low[k] = Sse2.UnpackLow(a, b).AsUInt16();   // rows 0-7: bytes 2k, 2k + 1
            high[k] = Sse2.UnpackHigh(a, b).AsUInt16(); // rows 8-15
        }

        for (int half = 0; half < 2; half++)
        {
            var source = half == 0 ? low : high;

            // 32-bit groups: bytes 0-3 / 4-7 of one row.
            var left = Sse2.UnpackLow(source[0], source[1]).AsUInt32();      // rows 0-3, bytes 0-3
            var leftHigh = Sse2.UnpackHigh(source[0], source[1]).AsUInt32(); // rows 4-7, bytes 0-3
            var right = Sse2.UnpackLow(source[2], source[3]).AsUInt32();
            var rightHigh = Sse2.UnpackHigh(source[2], source[3]).AsUInt32();

            byte[] plane = half == 0 ? top : bottom;
            int start = half == 0 ? topStart : bottomStart;
            WriteRows(plane, start, stride, Sse2.UnpackLow(left, right).AsUInt64());
            WriteRows(plane, start + (2 * stride), stride, Sse2.UnpackHigh(left, right).AsUInt64());
            WriteRows(plane, start + (4 * stride), stride, Sse2.UnpackLow(leftHigh, rightHigh).AsUInt64());
            WriteRows(plane, start + (6 * stride), stride, Sse2.UnpackHigh(leftHigh, rightHigh).AsUInt64());
        }
    }

    private static Vector128<byte> Row8(byte[] plane, int offset) =>
        Vector128.CreateScalarUnsafe(MemoryMarshal.Read<ulong>(plane.AsSpan(offset, 8))).AsByte();

    private static void WriteRows(byte[] plane, int offset, int stride, Vector128<ulong> rows)
    {
        MemoryMarshal.Write(plane.AsSpan(offset, 8), rows.GetElement(0));
        MemoryMarshal.Write(plane.AsSpan(offset + stride, 8), rows.GetElement(1));
    }

    private static Vector128<short> LoadTap<TTap>(Span<TTap> taps, int tap, int offset)
        where TTap : unmanaged =>
        typeof(TTap) == typeof(byte)
            ? Load(MemoryMarshal.Cast<TTap, byte>(taps), tap, offset)
            : Vector128.Create(MemoryMarshal.Cast<TTap, short>(taps).Slice((tap * 16) + offset, 8));

    private static void StoreTap<TTap>(Span<TTap> taps, int tap, int offset, Vector128<short> max, Vector128<short> value)
        where TTap : unmanaged
    {
        if (typeof(TTap) == typeof(byte))
        {
            Store(MemoryMarshal.Cast<TTap, byte>(taps), tap, offset, value);
            return;
        }

        Vector128.Clamp(value, Vector128<short>.Zero, max).CopyTo(MemoryMarshal.Cast<TTap, short>(taps)[((tap * 16) + offset)..]);
    }

    private static Vector128<short> Load(ReadOnlySpan<byte> taps, int tap, int offset) =>
        Vector128.WidenLower(Vector128.Create(taps.Slice((tap * 16) + offset, 16))).AsInt16();

    private static void Store(Span<byte> taps, int tap, int offset, Vector128<short> value)
    {
        var bytes = Vector128.Narrow(Clip(value).AsUInt16(), Vector128<ushort>.Zero);
        MemoryMarshal.Write(taps[((tap * 16) + offset)..], bytes.AsUInt64().ToScalar());
    }

    private static Vector128<short> Clip(Vector128<short> value) =>
        Vector128.Clamp(value, Vector128<short>.Zero, Vector128.Create((short)255));
}

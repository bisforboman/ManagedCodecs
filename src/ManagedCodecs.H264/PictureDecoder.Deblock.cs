using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ManagedCodecs.H264;

/// <summary>The deblocking filter (H.264 clause 8.7) for frames.</summary>
internal sealed partial class PictureDecoder
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

            var (lumaOrigin, chromaOrigin) = Origins(mbAddr);
            int lumaStride = Picture.LumaStride;
            int chromaStride = Picture.ChromaStride;
            int qp = _qp[mbAddr];

            // Luma: all vertical edges, then all horizontal ones.
            for (int edge = 0; edge < 4; edge++)
            {
                int qpP = edge == 0 && mbX > 0 ? _qp[mbAddr - 1] : qp;
                FilterEdge(Picture.Y, lumaOrigin + (edge * 4), 1, lumaStride, 16, vertical.Slice(edge * 4, 4), qpP, qp, slice.AlphaOffset, slice.BetaOffset, chroma: false);
            }

            for (int edge = 0; edge < 4; edge++)
            {
                int qpP = edge == 0 && mbY > 0 ? _qp[mbAddr - _widthInMbs] : qp;
                FilterEdge(Picture.Y, lumaOrigin + (edge * 4 * lumaStride), lumaStride, 1, 16, horizontal.Slice(edge * 4, 4), qpP, qp, slice.AlphaOffset, slice.BetaOffset, chroma: false);
            }

            // Chroma (4:2:0): edges 0 and 4 use the bS of luma edges 0 and 8.
            int qpc = ChromaQp(qp, slice.ChromaQpIndexOffset);
            for (int c = 0; c < 2; c++)
            {
                byte[] plane = c == 0 ? Picture.Cb : Picture.Cr;
                for (int edge = 0; edge < 2; edge++)
                {
                    int qpcP = edge == 0 && mbX > 0 ? ChromaQp(_qp[mbAddr - 1], slice.ChromaQpIndexOffset) : qpc;
                    FilterEdge(plane, chromaOrigin + (edge * 4), 1, chromaStride, 8, vertical.Slice(edge * 8, 4), qpcP, qpc, slice.AlphaOffset, slice.BetaOffset, chroma: true);
                }

                for (int edge = 0; edge < 2; edge++)
                {
                    int qpcP = edge == 0 && mbY > 0 ? ChromaQp(_qp[mbAddr - _widthInMbs], slice.ChromaQpIndexOffset) : qpc;
                    FilterEdge(plane, chromaOrigin + (edge * 4 * chromaStride), chromaStride, 1, 8, horizontal.Slice(edge * 8, 4), qpcP, qpc, slice.AlphaOffset, slice.BetaOffset, chroma: true);
                }
            }
        }
    }

    /// <summary>bS for the edge between luma 4x4 blocks p and q (raster indices), 8.7.2.1.</summary>
    private int BoundaryStrength(int mbP, int blockP, int mbQ, int blockQ, bool mbEdge)
    {
        if (IsIntra(mbP) || IsIntra(mbQ))
        {
            return mbEdge ? 4 : 3;
        }

        if (_totalCoeffLuma[(mbP * 16) + blockP] != 0 || _totalCoeffLuma[(mbQ * 16) + blockQ] != 0)
        {
            return 2;
        }

        int partP = (mbP * 4) + ((blockP >> 3) * 2) + ((blockP & 3) >> 1);
        int partQ = (mbQ * 4) + ((blockQ >> 3) * 2) + ((blockQ & 3) >> 1);
        if (_refPic[partP] != _refPic[partQ])
        {
            return 1;
        }

        int mvP = (mbP * 16) + blockP;
        int mvQ = (mbQ * 16) + blockQ;
        return Math.Abs(_mvX[mvP] - _mvX[mvQ]) >= 4 || Math.Abs(_mvY[mvP] - _mvY[mvQ]) >= 4 ? 1 : 0;
    }

    /// <summary>
    /// bS of the internal edges (1..3) of an inter macroblock: <see cref="BoundaryStrength"/> with
    /// both blocks in the same macroblock, so neither is intra and one reference list applies.
    /// </summary>
    private void InternalStrengths(int mbAddr, Span<byte> vertical, Span<byte> horizontal)
    {
        var coefficients = _totalCoeffLuma.AsSpan(mbAddr * 16, 16);
        var mvX = _mvX.AsSpan(mbAddr * 16, 16);
        var mvY = _mvY.AsSpan(mbAddr * 16, 16);
        var refs = _refPic.AsSpan(mbAddr * 4, 4);

        int Strength(int p, int q, Span<byte> coefficients, Span<short> mvX, Span<short> mvY, Span<Picture?> refs)
        {
            if ((coefficients[p] | coefficients[q]) != 0)
            {
                return 2;
            }

            // The 8x8 partition of 4x4 block b is (b >> 3) * 2 + ((b & 3) >> 1).
            if (refs[((p >> 3) * 2) + ((p & 3) >> 1)] != refs[((q >> 3) * 2) + ((q & 3) >> 1)])
            {
                return 1;
            }

            return Math.Abs(mvX[p] - mvX[q]) >= 4 || Math.Abs(mvY[p] - mvY[q]) >= 4 ? 1 : 0;
        }

        for (int edge = 1; edge < 4; edge++)
        {
            for (int k = 0; k < 4; k++)
            {
                vertical[(edge * 4) + k] = (byte)Strength((k * 4) + edge - 1, (k * 4) + edge, coefficients, mvX, mvY, refs);
                horizontal[(edge * 4) + k] = (byte)Strength(((edge - 1) * 4) + k, (edge * 4) + k, coefficients, mvX, mvY, refs);
            }
        }
    }

    /// <summary>
    /// True when every internal edge of an inter macroblock has bS 0: no luma coefficients and
    /// the same reference picture and motion vector in all 16 blocks (P_Skip, uncoded 16x16).
    /// </summary>
    private bool HasUniformMotionWithoutCoefficients(int mbAddr)
    {
        if (Vector128.Create(_totalCoeffLuma.AsSpan(mbAddr * 16, 16)) != Vector128<byte>.Zero)
        {
            return false;
        }

        var refs = _refPic.AsSpan(mbAddr * 4, 4);
        if (refs[1] != refs[0] || refs[2] != refs[0] || refs[3] != refs[0])
        {
            return false;
        }

        var mvX = _mvX.AsSpan(mbAddr * 16, 16);
        var mvY = _mvY.AsSpan(mbAddr * 16, 16);
        var x0 = Vector128.Create(mvX[0]);
        var y0 = Vector128.Create(mvY[0]);
        return Vector128.Create(mvX) == x0 && Vector128.Create(mvX[8..]) == x0
            && Vector128.Create(mvY) == y0 && Vector128.Create(mvY[8..]) == y0;
    }

    private bool IsIntra(int mbAddr) => _kind[mbAddr] is MbKind.Intra4x4 or MbKind.Intra16x16 or MbKind.IPcm;

    /// <summary>
    /// Filters one edge (8.7.2). <paramref name="q0"/> is the first q0 sample; p samples lie at
    /// -<paramref name="across"/>, -2 * across, ... and the edge runs along <paramref name="along"/>.
    /// <paramref name="strengths"/> holds bS per luma 4x4 segment; chroma samples use bS of segment i / 2.
    /// </summary>
    internal static void FilterEdge(byte[] plane, int q0, int across, int along, int length, ReadOnlySpan<byte> strengths, int qpP, int qpQ, int alphaOffset, int betaOffset, bool chroma, bool vectorized = true)
    {
        if (MemoryMarshal.Read<uint>(strengths) == 0)
        {
            return; // all four segments have bS 0
        }

        int qpAv = (qpP + qpQ + 1) >> 1;
        int indexA = Math.Clamp(qpAv + alphaOffset, 0, 51);
        int indexB = Math.Clamp(qpAv + betaOffset, 0, 51);
        int alpha = Alpha[indexA];
        int beta = Beta[indexB];
        if (alpha == 0 || beta == 0)
        {
            return; // no sample can pass |p0 - q0| < alpha
        }

        if (vectorized && Vector128.IsHardwareAccelerated)
        {
            FilterEdgeVector(plane, q0, across, along, length, strengths, indexA, alpha, beta, chroma);
        }
        else
        {
            FilterEdgeScalar(plane, q0, across, along, length, strengths, indexA, alpha, beta, chroma);
        }
    }

    /// <summary>The filter of 8.7.2.3 / 8.7.2.4 sample by sample; the reference for the vector version.</summary>
    private static void FilterEdgeScalar(byte[] plane, int q0, int across, int along, int length, ReadOnlySpan<byte> strengths, int indexA, int alpha, int beta, bool chroma)
    {
        int segmentShift = chroma ? 1 : 2; // 2 or 4 samples per segment
        for (int i = 0; i < length; i++)
        {
            int bS = strengths[i >> segmentShift];
            if (bS == 0)
            {
                continue;
            }

            int pos = q0 + (i * along);
            int p0 = plane[pos - across];
            int p1 = plane[pos - (2 * across)];
            int q0v = plane[pos];
            int q1 = plane[pos + across];

            if (Math.Abs(p0 - q0v) >= alpha || Math.Abs(p1 - p0) >= beta || Math.Abs(q1 - q0v) >= beta)
            {
                continue;
            }

            if (chroma)
            {
                if (bS < 4)
                {
                    int tc = Tc0[(indexA * 3) + bS - 1] + 1;
                    int delta = Math.Clamp((((q0v - p0) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
                    plane[pos - across] = Clip(p0 + delta);
                    plane[pos] = Clip(q0v - delta);
                }
                else
                {
                    plane[pos - across] = (byte)(((2 * p1) + p0 + q1 + 2) >> 2);
                    plane[pos] = (byte)(((2 * q1) + q0v + p1 + 2) >> 2);
                }

                continue;
            }

            int p2 = plane[pos - (3 * across)];
            int q2 = plane[pos + (2 * across)];
            bool ap = Math.Abs(p2 - p0) < beta;
            bool aq = Math.Abs(q2 - q0v) < beta;

            if (bS < 4)
            {
                int tc0 = Tc0[(indexA * 3) + bS - 1];
                int tc = tc0 + (ap ? 1 : 0) + (aq ? 1 : 0);
                int delta = Math.Clamp((((q0v - p0) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
                plane[pos - across] = Clip(p0 + delta);
                plane[pos] = Clip(q0v - delta);
                if (ap)
                {
                    plane[pos - (2 * across)] = (byte)(p1 + Math.Clamp((p2 + ((p0 + q0v + 1) >> 1) - (p1 << 1)) >> 1, -tc0, tc0));
                }

                if (aq)
                {
                    plane[pos + across] = (byte)(q1 + Math.Clamp((q2 + ((p0 + q0v + 1) >> 1) - (q1 << 1)) >> 1, -tc0, tc0));
                }

                continue;
            }

            // bS == 4: the strong filter where the edge is smooth enough.
            bool small = Math.Abs(p0 - q0v) < ((alpha >> 2) + 2);
            if (ap && small)
            {
                int p3 = plane[pos - (4 * across)];
                plane[pos - across] = (byte)((p2 + (2 * p1) + (2 * p0) + (2 * q0v) + q1 + 4) >> 3);
                plane[pos - (2 * across)] = (byte)((p2 + p1 + p0 + q0v + 2) >> 2);
                plane[pos - (3 * across)] = (byte)(((2 * p3) + (3 * p2) + p1 + p0 + q0v + 4) >> 3);
            }
            else
            {
                plane[pos - across] = (byte)(((2 * p1) + p0 + q1 + 2) >> 2);
            }

            if (aq && small)
            {
                int q3 = plane[pos + (3 * across)];
                plane[pos] = (byte)((p1 + (2 * p0) + (2 * q0v) + (2 * q1) + q2 + 4) >> 3);
                plane[pos + across] = (byte)((p0 + q0v + q1 + q2 + 2) >> 2);
                plane[pos + (2 * across)] = (byte)(((2 * q3) + (3 * q2) + q1 + q0v + p0 + 4) >> 3);
            }
            else
            {
                plane[pos] = (byte)(((2 * q1) + q0v + p1 + 2) >> 2);
            }
        }
    }

    /// <summary>
    /// The same filter on up to 16 positions at once. The samples across the edge (p3 .. q3) are
    /// gathered into rows, filtered in 16-bit lanes with per-lane bS, and written back.
    /// </summary>
    [SkipLocalsInit]
    private static void FilterEdgeVector(byte[] plane, int q0, int across, int along, int length, ReadOnlySpan<byte> strengths, int indexA, int alpha, int beta, bool chroma)
    {
        // taps[k * 16 + i]: sample k (0 = p3, 3 = p0, 4 = q0, 7 = q3) at position i along the edge.
        Span<byte> taps = stackalloc byte[(8 * 16) + 16];
        int firstTap = chroma ? 2 : 0;
        int lastTap = chroma ? 5 : 7;
        Gather(plane, q0, across, along, length, taps, firstTap, lastTap);

        int segmentShift = chroma ? 1 : 2; // 2 or 4 samples per segment
        Span<short> tc0OfSegment = stackalloc short[4];
        for (int s = 0; s < 4; s++)
        {
            int bS = strengths[s];
            tc0OfSegment[s] = bS is > 0 and < 4 ? Tc0[(indexA * 3) + bS - 1] : (short)0;
        }

        Span<short> bsLanes = stackalloc short[16];
        Span<short> tc0Lanes = stackalloc short[16];
        for (int i = 0; i < length; i++)
        {
            bsLanes[i] = strengths[i >> segmentShift];
            tc0Lanes[i] = tc0OfSegment[i >> segmentShift];
        }

        var alphaV = Vector128.Create((short)alpha);
        var betaV = Vector128.Create((short)beta);
        for (int h = 0; h < length; h += 8)
        {
            var bs = Vector128.Create(bsLanes.Slice(h, 8));
            var tc0 = Vector128.Create(tc0Lanes.Slice(h, 8));
            var p1 = Load(taps, 2, h);
            var p0 = Load(taps, 3, h);
            var q0v = Load(taps, 4, h);
            var q1 = Load(taps, 5, h);

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
                var p0n = Clip(p0 + delta);
                var q0n = Clip(q0v - delta);
                var p0s = ((p1 << 1) + p0 + q1 + two) >> 2;
                var q0s = ((q1 << 1) + q0v + p1 + two) >> 2;
                Store(taps, 3, h, Vector128.ConditionalSelect(filter, Vector128.ConditionalSelect(strong, p0s, p0n), p0));
                Store(taps, 4, h, Vector128.ConditionalSelect(filter, Vector128.ConditionalSelect(strong, q0s, q0n), q0v));
                continue;
            }

            var p3 = Load(taps, 0, h);
            var p2 = Load(taps, 1, h);
            var q2 = Load(taps, 6, h);
            var q3 = Load(taps, 7, h);
            var ap = Vector128.LessThan(Vector128.Abs(p2 - p0), betaV);
            var aq = Vector128.LessThan(Vector128.Abs(q2 - q0v), betaV);

            // bS < 4 (8.7.2.3); the masks are -1 where true, so subtracting them adds one.
            var tcL = tc0 - ap - aq;
            var deltaL = Vector128.Clamp((((q0v - p0) << 2) + (p1 - q1) + four) >> 3, -tcL, tcL);
            var p0Normal = Clip(p0 + deltaL);
            var q0Normal = Clip(q0v - deltaL);
            var average = (p0 + q0v + one) >> 1;
            var p1Normal = Vector128.ConditionalSelect(ap, p1 + Vector128.Clamp((p2 + average - (p1 << 1)) >> 1, -tc0, tc0), p1);
            var q1Normal = Vector128.ConditionalSelect(aq, q1 + Vector128.Clamp((q2 + average - (q1 << 1)) >> 1, -tc0, tc0), q1);

            var strongFilter = filter & strong;
            if (strongFilter == Vector128<short>.Zero)
            {
                // Only bS < 4 here, which is every internal edge.
                Store(taps, 2, h, Vector128.ConditionalSelect(filter, p1Normal, p1));
                Store(taps, 3, h, Vector128.ConditionalSelect(filter, p0Normal, p0));
                Store(taps, 4, h, Vector128.ConditionalSelect(filter, q0Normal, q0v));
                Store(taps, 5, h, Vector128.ConditionalSelect(filter, q1Normal, q1));
                continue;
            }

            // bS == 4 (8.7.2.4).
            var small = Vector128.LessThan(Vector128.Abs(p0 - q0v), Vector128.Create((short)((alpha >> 2) + 2)));
            var sp = ap & small;
            var sq = aq & small;
            var p0Strong = Vector128.ConditionalSelect(sp, (p2 + (p1 << 1) + (p0 << 1) + (q0v << 1) + q1 + four) >> 3, ((p1 << 1) + p0 + q1 + two) >> 2);
            var p1Strong = Vector128.ConditionalSelect(sp, (p2 + p1 + p0 + q0v + two) >> 2, p1);
            var p2Strong = Vector128.ConditionalSelect(sp, ((p3 << 1) + (p2 * 3) + p1 + p0 + q0v + four) >> 3, p2);
            var q0Strong = Vector128.ConditionalSelect(sq, (p1 + (p0 << 1) + (q0v << 1) + (q1 << 1) + q2 + four) >> 3, ((q1 << 1) + q0v + p1 + two) >> 2);
            var q1Strong = Vector128.ConditionalSelect(sq, (p0 + q0v + q1 + q2 + two) >> 2, q1);
            var q2Strong = Vector128.ConditionalSelect(sq, ((q3 << 1) + (q2 * 3) + q1 + q0v + p0 + four) >> 3, q2);

            Store(taps, 1, h, Vector128.ConditionalSelect(strongFilter, p2Strong, p2));
            Store(taps, 2, h, Vector128.ConditionalSelect(filter, Vector128.ConditionalSelect(strong, p1Strong, p1Normal), p1));
            Store(taps, 3, h, Vector128.ConditionalSelect(filter, Vector128.ConditionalSelect(strong, p0Strong, p0Normal), p0));
            Store(taps, 4, h, Vector128.ConditionalSelect(filter, Vector128.ConditionalSelect(strong, q0Strong, q0Normal), q0v));
            Store(taps, 5, h, Vector128.ConditionalSelect(filter, Vector128.ConditionalSelect(strong, q1Strong, q1Normal), q1));
            Store(taps, 6, h, Vector128.ConditionalSelect(strongFilter, q2Strong, q2));
        }

        // Only p2 .. q2 (luma) or p0, q0 (chroma) can change.
        Scatter(plane, q0, across, along, length, taps, chroma ? 3 : 1, chroma ? 4 : 6);
    }

    private static void Gather(byte[] plane, int q0, int across, int along, int length, Span<byte> taps, int firstTap, int lastTap)
    {
        if (across == 1 && Sse2.IsSupported)
        {
            // Vertical edges: p3 .. q3 of each row are 8 contiguous bytes. Chroma needs only
            // p1 .. q1 but takes all eight; they lie inside this macroblock and its left neighbour.
            TransposeIn(plane, q0 - 4, along, taps, length);
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
            TransposeOut(taps, plane, q0 - 4, along);
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
    /// Vertical edge: <paramref name="rows"/> (16 or 8) rows of p3 .. q3 (8 bytes each, starting at
    /// <paramref name="start"/>) become the 8 tap rows of 16 lanes, by interleaving at 8, 16, 32 and
    /// 64 bits. With 8 rows, lanes 8 .. 15 repeat row 7 and are not used.
    /// </summary>
    [SkipLocalsInit]
    private static void TransposeIn(byte[] plane, int start, int stride, Span<byte> taps, int rows)
    {
        Span<Vector128<byte>> pairs = stackalloc Vector128<byte>[8];
        for (int k = 0; k < 8; k++)
        {
            int even = Math.Min(2 * k, rows - 1);
            int odd = Math.Min((2 * k) + 1, rows - 1);
            pairs[k] = Sse2.UnpackLow(Row8(plane, start + (even * stride)), Row8(plane, start + (odd * stride)));
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
            var top = Sse2.UnpackLow(source[0], source[1]).AsUInt64();
            var topHigh = Sse2.UnpackHigh(source[0], source[1]).AsUInt64();
            var bottom = Sse2.UnpackLow(source[2], source[3]).AsUInt64();
            var bottomHigh = Sse2.UnpackHigh(source[2], source[3]).AsUInt64();

            int tap = half * 4;
            Sse2.UnpackLow(top, bottom).AsByte().CopyTo(taps[(tap * 16)..]);
            Sse2.UnpackHigh(top, bottom).AsByte().CopyTo(taps[((tap + 1) * 16)..]);
            Sse2.UnpackLow(topHigh, bottomHigh).AsByte().CopyTo(taps[((tap + 2) * 16)..]);
            Sse2.UnpackHigh(topHigh, bottomHigh).AsByte().CopyTo(taps[((tap + 3) * 16)..]);
        }
    }

    /// <summary>The inverse of <see cref="TransposeIn"/>: writes all 8 bytes of each of the 16 rows.</summary>
    [SkipLocalsInit]
    private static void TransposeOut(ReadOnlySpan<byte> taps, byte[] plane, int start, int stride)
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

            int row = half * 8;
            WriteRows(plane, start + (row * stride), stride, Sse2.UnpackLow(left, right).AsUInt64());
            WriteRows(plane, start + ((row + 2) * stride), stride, Sse2.UnpackHigh(left, right).AsUInt64());
            WriteRows(plane, start + ((row + 4) * stride), stride, Sse2.UnpackLow(leftHigh, rightHigh).AsUInt64());
            WriteRows(plane, start + ((row + 6) * stride), stride, Sse2.UnpackHigh(leftHigh, rightHigh).AsUInt64());
        }
    }

    private static Vector128<byte> Row8(byte[] plane, int offset) =>
        Vector128.CreateScalarUnsafe(MemoryMarshal.Read<ulong>(plane.AsSpan(offset, 8))).AsByte();

    private static void WriteRows(byte[] plane, int offset, int stride, Vector128<ulong> rows)
    {
        MemoryMarshal.Write(plane.AsSpan(offset, 8), rows.GetElement(0));
        MemoryMarshal.Write(plane.AsSpan(offset + stride, 8), rows.GetElement(1));
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

    private static byte Clip(int value) => (byte)Math.Clamp(value, 0, 255);
}

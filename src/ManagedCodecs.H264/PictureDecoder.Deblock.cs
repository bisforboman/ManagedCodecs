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

            for (int edge = 0; edge < 4; edge++)
            {
                for (int k = 0; k < 4; k++)
                {
                    vertical[(edge * 4) + k] = edge == 0
                        ? (byte)(left ? BoundaryStrength(mbAddr - 1, (k * 4) + 3, mbAddr, k * 4, mbEdge: true) : 0)
                        : (byte)BoundaryStrength(mbAddr, (k * 4) + edge - 1, mbAddr, (k * 4) + edge, mbEdge: false);

                    horizontal[(edge * 4) + k] = edge == 0
                        ? (byte)(top ? BoundaryStrength(mbAddr - _widthInMbs, 12 + k, mbAddr, k, mbEdge: true) : 0)
                        : (byte)BoundaryStrength(mbAddr, ((edge - 1) * 4) + k, mbAddr, (edge * 4) + k, mbEdge: false);
                }
            }

            var (lumaOrigin, chromaOrigin) = Origins(mbAddr);
            int lumaStride = Picture.LumaStride;
            int chromaStride = Picture.ChromaStride;
            int qp = _qp[mbAddr];

            // Luma: all vertical edges, then all horizontal ones.
            for (int edge = 0; edge < 4; edge++)
            {
                int qpP = edge == 0 && mbX > 0 ? _qp[mbAddr - 1] : qp;
                FilterEdge(Picture.Y, lumaOrigin + (edge * 4), 1, lumaStride, 16, vertical.Slice(edge * 4, 4), qpP, qp, slice, chroma: false);
            }

            for (int edge = 0; edge < 4; edge++)
            {
                int qpP = edge == 0 && mbY > 0 ? _qp[mbAddr - _widthInMbs] : qp;
                FilterEdge(Picture.Y, lumaOrigin + (edge * 4 * lumaStride), lumaStride, 1, 16, horizontal.Slice(edge * 4, 4), qpP, qp, slice, chroma: false);
            }

            // Chroma (4:2:0): edges 0 and 4 use the bS of luma edges 0 and 8.
            int qpc = ChromaQp(qp, slice.ChromaQpIndexOffset);
            foreach (var plane in new[] { Picture.Cb, Picture.Cr })
            {
                for (int edge = 0; edge < 2; edge++)
                {
                    int qpcP = edge == 0 && mbX > 0 ? ChromaQp(_qp[mbAddr - 1], slice.ChromaQpIndexOffset) : qpc;
                    FilterEdge(plane, chromaOrigin + (edge * 4), 1, chromaStride, 8, vertical.Slice(edge * 8, 4), qpcP, qpc, slice, chroma: true);
                }

                for (int edge = 0; edge < 2; edge++)
                {
                    int qpcP = edge == 0 && mbY > 0 ? ChromaQp(_qp[mbAddr - _widthInMbs], slice.ChromaQpIndexOffset) : qpc;
                    FilterEdge(plane, chromaOrigin + (edge * 4 * chromaStride), chromaStride, 1, 8, horizontal.Slice(edge * 8, 4), qpcP, qpc, slice, chroma: true);
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

    private bool IsIntra(int mbAddr) => _kind[mbAddr] is MbKind.Intra4x4 or MbKind.Intra16x16 or MbKind.IPcm;

    /// <summary>
    /// Filters one edge (8.7.2). <paramref name="q0"/> is the first q0 sample; p samples lie at
    /// -<paramref name="across"/>, -2 * across, ... and the edge runs along <paramref name="along"/>.
    /// <paramref name="strengths"/> holds bS per luma 4x4 segment; chroma samples use bS of segment i / 2.
    /// </summary>
    private static void FilterEdge(byte[] plane, int q0, int across, int along, int length, ReadOnlySpan<byte> strengths, int qpP, int qpQ, SliceInfo slice, bool chroma)
    {
        if (strengths.IndexOfAnyExcept((byte)0) < 0)
        {
            return;
        }

        int qpAv = (qpP + qpQ + 1) >> 1;
        int indexA = Math.Clamp(qpAv + slice.AlphaOffset, 0, 51);
        int indexB = Math.Clamp(qpAv + slice.BetaOffset, 0, 51);
        int alpha = Alpha[indexA];
        int beta = Beta[indexB];
        if (alpha == 0 || beta == 0)
        {
            return; // no sample can pass |p0 - q0| < alpha
        }

        int samplesPerSegment = chroma ? 2 : 4;
        for (int i = 0; i < length; i++)
        {
            int bS = strengths[i / samplesPerSegment];
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

    private static byte Clip(int value) => (byte)Math.Clamp(value, 0, 255);
}

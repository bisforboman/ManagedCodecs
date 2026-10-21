namespace ManagedCodecs.H264;

/// <summary>
/// Macroblock-adaptive frame/field coding (MBAFF): frames whose macroblocks come in vertical
/// pairs, each pair coded either as two frame macroblocks (the upper and lower 16 rows) or as two
/// field macroblocks (the even and the odd rows of the pair's 32). Macroblock addresses count
/// pairs first, then top and bottom (7.4.1): pair <c>mbAddr / 2</c> sits at raster position
/// (<c>pair % PicWidthInMbs</c>, <c>pair / PicWidthInMbs</c>).
/// </summary>
/// <remarks>
/// Neighbouring locations follow 6.4.12.2 (Table 6-4). The ITU text was not at hand here; the table
/// was read from the ITU's reference decoder (JM, getAffNeighbour) and each case checked as a sample
/// position: a frame macroblock's neighbour is the adjacent frame row, a field macroblock's the
/// adjacent row of its own field, in whichever macroblock holds it.
/// </remarks>
internal sealed partial class PictureDecoder
{
    // Whether the current picture is an MBAFF frame, and per macroblock whether it is a field macroblock.
    private bool _mbaff;
    private bool[] _fieldMb = [];

    // Intra prediction in MBAFF frames reconstructs into these, with the neighbouring samples
    // gathered around the macroblock (row 0 and column 0), then copies the result out.
    private const int ScratchLumaStride = 32;
    private const int ScratchChromaStride = 16;
    private readonly byte[] _scratchY = new byte[ScratchLumaStride * 20];
    private readonly byte[] _scratchCb = new byte[ScratchChromaStride * 12];
    private readonly byte[] _scratchCr = new byte[ScratchChromaStride * 12];

    // Inverse scans of field macroblocks (8.5.6, Tables 8-13 and 8-14), as raster indices.
    private static ReadOnlySpan<byte> Field4x4 => [0, 4, 1, 8, 12, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];

    private static ReadOnlySpan<byte> Field4x4Ac => [4, 1, 8, 12, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];

    private static ReadOnlySpan<byte> Field8x8 =>
    [
        0, 8, 16, 1, 9, 24, 32, 17, 2, 25, 40, 48, 56, 33, 10, 3, 18, 41, 49, 57, 26, 11, 4, 19, 34, 42, 50, 58, 27, 12, 5, 20,
        35, 43, 51, 59, 28, 13, 6, 21, 36, 44, 52, 60, 29, 14, 22, 37, 45, 53, 61, 30, 7, 15, 38, 46, 54, 62, 23, 31, 39, 47, 55, 63,
    ];

    private static readonly byte[] CavlcField8x8Scan = [.. Enumerable.Range(0, 64).Select(i => Field8x8[(4 * (i % 16)) + (i / 16)])];

    /// <summary>Counts of MBAFF pair kinds (shared by every picture of an <see cref="H264Decoder"/>).</summary>
    public MbaffStatistics Statistics { get; init; } = new();

    /// <summary>Where a macroblock is the planes being reconstructed into: the picture, or the MBAFF scratch.</summary>
    private readonly record struct MbTarget(byte[] Y, int YOrigin, int YStride, byte[] Cb, byte[] Cr, int COrigin, int CStride);

    private bool IsFieldMb(int mbAddr) => _mbaff && _fieldMb[mbAddr];

    /// <summary>The macroblock's top-left luma sample and the row stride of its samples.</summary>
    private (int Origin, int Stride) LumaPlace(int mbAddr)
    {
        int stride = Picture.LumaStride;
        if (!_mbaff)
        {
            return (((mbAddr / _widthInMbs) * 16 * stride) + ((mbAddr % _widthInMbs) * 16), stride);
        }

        int pair = mbAddr >> 1;
        int row = (pair / _widthInMbs) * 32;
        int bottom = mbAddr & 1;
        return _fieldMb[mbAddr]
            ? (((row + bottom) * stride) + ((pair % _widthInMbs) * 16), stride * 2)
            : (((row + (bottom * 16)) * stride) + ((pair % _widthInMbs) * 16), stride);
    }

    /// <summary>The macroblock's top-left chroma sample and the row stride of its samples.</summary>
    private (int Origin, int Stride) ChromaPlace(int mbAddr)
    {
        int stride = Picture.ChromaStride;
        if (!_mbaff)
        {
            return (((mbAddr / _widthInMbs) * 8 * stride) + ((mbAddr % _widthInMbs) * 8), stride);
        }

        int pair = mbAddr >> 1;
        int row = (pair / _widthInMbs) * 16;
        int bottom = mbAddr & 1;
        return _fieldMb[mbAddr]
            ? (((row + bottom) * stride) + ((pair % _widthInMbs) * 8), stride * 2)
            : (((row + (bottom * 8)) * stride) + ((pair % _widthInMbs) * 8), stride);
    }

    private MbTarget PictureTarget(int mbAddr)
    {
        var (y, yStride) = LumaPlace(mbAddr);
        var (c, cStride) = ChromaPlace(mbAddr);
        return new MbTarget(Picture.Y, y, yStride, Picture.Cb, Picture.Cr, c, cStride);
    }

    /// <summary>
    /// The neighbouring location (6.4.12): the macroblock covering (<paramref name="xN"/>,
    /// <paramref name="yN"/>) relative to the current macroblock's top-left in a block of
    /// <paramref name="maxW"/> x <paramref name="maxH"/> samples (16 for luma, 8 for chroma), and the
    /// location inside it; Mb is -1 when it is not available.
    /// </summary>
    private (int Mb, int X, int Y) Neighbor(int mbAddr, int xN, int yN, int maxW = 16, int maxH = 16)
    {
        if (_mbaff)
        {
            return AffNeighbor(mbAddr, xN, yN, maxW, maxH);
        }

        int n = NeighborMb(mbAddr, xN, yN, maxW);
        return (n, (xN + maxW) % maxW, (yN + maxH) % maxH);
    }

    /// <summary>
    /// The top macroblock of the pair at (<paramref name="pairX"/>, <paramref name="pairY"/>), or -1 if
    /// it is outside the picture, not decoded or (unless <paramref name="anySlice"/>) in another slice.
    /// </summary>
    private int PairIfAvailable(int mbAddr, int pairX, int pairY, bool anySlice = false) =>
        PairIfAvailable(_sliceOfMb, _widthInMbs, mbAddr, pairX, pairY, anySlice);

    private static int PairIfAvailable(ReadOnlySpan<int> sliceOfMb, int widthInMbs, int mbAddr, int pairX, int pairY, bool anySlice)
    {
        if (pairX < 0 || pairX >= widthInMbs || pairY < 0)
        {
            return -1;
        }

        int top = ((pairY * widthInMbs) + pairX) * 2;
        return sliceOfMb[top] >= 0 && (anySlice || sliceOfMb[top] == sliceOfMb[mbAddr]) ? top : -1;
    }

    /// <summary>Table 6-4: neighbouring locations in MBAFF frames (the deblocking filter also looks into other slices).</summary>
    private (int Mb, int X, int Y) AffNeighbor(int mbAddr, int xN, int yN, int maxW, int maxH, bool anySlice = false) =>
        AffNeighbor(_fieldMb, _sliceOfMb, _widthInMbs, mbAddr, xN, yN, maxW, maxH, anySlice);

    /// <summary>
    /// Table 6-4 for macroblock <paramref name="mbAddr"/> of an MBAFF frame <paramref name="widthInMbs"/>
    /// pairs wide, given which macroblocks are field macroblocks and the slice of each (-1: not decoded).
    /// </summary>
    internal static (int Mb, int X, int Y) AffNeighbor(ReadOnlySpan<bool> fieldMb, ReadOnlySpan<int> sliceOfMb, int widthInMbs, int mbAddr, int xN, int yN, int maxW, int maxH, bool anySlice = false)
    {
        if (yN >= maxH || (xN >= maxW && yN >= 0))
        {
            return (-1, 0, 0);
        }

        int pair = mbAddr >> 1;
        int pairX = pair % widthInMbs;
        int pairY = pair / widthInMbs;
        bool isTop = (mbAddr & 1) == 0;
        bool frame = !fieldMb[mbAddr];
        int n;
        int yM;

        if (xN < 0 && yN < 0)
        {
            // D: above-left; a frame macroblock's bottom half looks into the left pair instead.
            if (frame && !isTop)
            {
                n = PairIfAvailable(sliceOfMb, widthInMbs, mbAddr, pairX - 1, pairY, anySlice);
                if (n < 0)
                {
                    return (-1, 0, 0);
                }

                (n, yM) = fieldMb[n] ? (n + 1, (yN + maxH) >> 1) : (n, yN);
            }
            else
            {
                n = PairIfAvailable(sliceOfMb, widthInMbs, mbAddr, pairX - 1, pairY - 1, anySlice);
                if (n < 0)
                {
                    return (-1, 0, 0);
                }

                (n, yM) = frame || !isTop ? (n + 1, yN) : fieldMb[n] ? (n, yN) : (n + 1, 2 * yN);
            }
        }
        else if (xN < 0)
        {
            // A: the left pair, row by row.
            n = PairIfAvailable(sliceOfMb, widthInMbs, mbAddr, pairX - 1, pairY, anySlice);
            if (n < 0)
            {
                return (-1, 0, 0);
            }

            bool leftField = fieldMb[n];
            if (frame)
            {
                // Frame row (isTop ? 0 : 16) + yN of the pair.
                int row = (isTop ? 0 : maxH) + yN;
                (n, yM) = leftField ? (n + (row & 1), row >> 1) : (n + (row >= maxH ? 1 : 0), row % maxH);
            }
            else if (leftField)
            {
                (n, yM) = (n + (isTop ? 0 : 1), yN);
            }
            else
            {
                // Field row yN of the current parity is frame row 2 * yN (+ 1 for the bottom field).
                int row = (2 * yN) + (isTop ? 0 : 1);
                (n, yM) = (n + (row >= maxH ? 1 : 0), row % maxH);
            }
        }
        else if (xN < maxW && yN >= 0)
        {
            return (mbAddr, xN, yN);
        }
        else
        {
            // B (xN < maxW) or C (xN >= maxW), above.
            bool c = xN >= maxW;
            if (frame && !isTop)
            {
                if (c)
                {
                    return (-1, 0, 0);
                }

                (n, yM) = (mbAddr - 1, yN);
            }
            else
            {
                n = PairIfAvailable(sliceOfMb, widthInMbs, mbAddr, pairX + (c ? 1 : 0), pairY - 1, anySlice);
                if (n < 0)
                {
                    return (-1, 0, 0);
                }

                (n, yM) = frame || !isTop ? (n + 1, yN) : fieldMb[n] ? (n, yN) : (n + 1, 2 * yN);
            }
        }

        return (n, xN & (maxW - 1), yM & (maxH - 1));
    }

    /// <summary>mb_field_decoding_flag of a pair (7.3.4): one bit, or with CABAC ctxIdx 70 + whether the left and the above pair are field pairs.</summary>
    private bool ReadFieldDecodingFlag(SliceState slice, BitReader r, int mbAddr)
    {
        if (slice.Cabac is not { } c)
        {
            return r.ReadFlag();
        }

        int pair = mbAddr >> 1;
        int a = PairIfAvailable(mbAddr, (pair % _widthInMbs) - 1, pair / _widthInMbs);
        int b = PairIfAvailable(mbAddr, pair % _widthInMbs, (pair / _widthInMbs) - 1);
        int inc = (a >= 0 && _fieldMb[a] ? 1 : 0) + (b >= 0 && _fieldMb[b] ? 1 : 0);
        return c.Decision(70 + inc) == 1;
    }

    /// <summary>
    /// At the top macroblock of a pair in an MBAFF frame, reads whether the pair is a field pair;
    /// the bottom macroblock is the same kind. (Only I slices so far, where the flag is always sent.)
    /// </summary>
    private void StartPair(SliceState slice, BitReader r, int mbAddr)
    {
        if (!_mbaff || (mbAddr & 1) != 0)
        {
            return;
        }

        bool field = ReadFieldDecodingFlag(slice, r, mbAddr);
        _fieldMb[mbAddr] = field;
        _fieldMb[mbAddr + 1] = field;

        int pair = mbAddr >> 1;
        int left = PairIfAvailable(mbAddr, (pair % _widthInMbs) - 1, pair / _widthInMbs, anySlice: true);
        int above = PairIfAvailable(mbAddr, pair % _widthInMbs, (pair / _widthInMbs) - 1, anySlice: true);
        var stats = Statistics;
        if (field)
        {
            stats.FieldPairs++;
        }
        else
        {
            stats.FramePairs++;
        }

        if (left >= 0 && _fieldMb[left] != field)
        {
            stats.MixedLeft++;
        }

        if (above >= 0 && _fieldMb[above] && !field)
        {
            stats.FrameBelowField++;
        }

        if (above >= 0 && !_fieldMb[above] && field)
        {
            stats.FieldBelowFrame++;
        }
    }

    /// <summary>
    /// The planes an intra macroblock of an MBAFF frame is reconstructed into: the scratch blocks,
    /// with every neighbouring sample prediction may read gathered through Table 6-4 (luma row -1
    /// from x = -1 to 23 and column -1; chroma row -1 and column -1).
    /// </summary>
    private MbTarget GatherMbaffNeighbors(int mbAddr)
    {
        GatherPlane(mbAddr, Picture.Y, _scratchY, ScratchLumaStride, 16, 24);
        GatherPlane(mbAddr, Picture.Cb, _scratchCb, ScratchChromaStride, 8, 8);
        GatherPlane(mbAddr, Picture.Cr, _scratchCr, ScratchChromaStride, 8, 8);
        return new MbTarget(_scratchY, ScratchLumaStride + 1, ScratchLumaStride, _scratchCb, _scratchCr, ScratchChromaStride + 1, ScratchChromaStride);
    }

    private void GatherPlane(int mbAddr, byte[] plane, byte[] scratch, int scratchStride, int size, int topWidth)
    {
        for (int yN = -1; yN < size; yN++)
        {
            Copy(-1, yN);
        }

        for (int xN = 0; xN < topWidth; xN++)
        {
            Copy(xN, -1);
        }

        void Copy(int xN, int yN)
        {
            var (n, x, y) = AffNeighbor(mbAddr, xN, yN, size, size);
            if (n >= 0)
            {
                var (origin, stride) = size == 16 ? LumaPlace(n) : ChromaPlace(n);
                scratch[((yN + 1) * scratchStride) + xN + 1] = plane[origin + (y * stride) + x];
            }
        }
    }

    /// <summary>Copies a macroblock reconstructed in the scratch blocks to its place in the picture.</summary>
    private void ScatterMbaff(int mbAddr, MbTarget target)
    {
        var (luma, lumaStride) = LumaPlace(mbAddr);
        for (int y = 0; y < 16; y++)
        {
            target.Y.AsSpan(target.YOrigin + (y * target.YStride), 16).CopyTo(Picture.Y.AsSpan(luma + (y * lumaStride)));
        }

        var (chroma, chromaStride) = ChromaPlace(mbAddr);
        for (int y = 0; y < 8; y++)
        {
            target.Cb.AsSpan(target.COrigin + (y * target.CStride), 8).CopyTo(Picture.Cb.AsSpan(chroma + (y * chromaStride)));
            target.Cr.AsSpan(target.COrigin + (y * target.CStride), 8).CopyTo(Picture.Cr.AsSpan(chroma + (y * chromaStride)));
        }
    }

    /// <summary>
    /// The deblocking filter of an MBAFF frame (8.7), macroblock by macroblock in address order,
    /// sample line by sample line where the two sides of a macroblock edge differ in field/frame
    /// mode. Only intra macroblocks so far: bS is 4 on macroblock edges, except 3 on horizontal
    /// ones that touch a field macroblock, and 3 inside.
    /// </summary>
    private void DeblockMbaff()
    {
        ReadOnlySpan<byte> internalStrengths = [3, 3, 3, 3];
        int lumaRow = Picture.LumaStride;
        int chromaRow = Picture.ChromaStride;
        for (int mbAddr = 0; mbAddr < _mbCount; mbAddr++)
        {
            var slice = _slices[_sliceOfMb[mbAddr]];
            if (slice.DisableDeblockingFilterIdc == 1)
            {
                continue;
            }

            int pair = mbAddr >> 1;
            int pairX = pair % _widthInMbs;
            int pairY = pair / _widthInMbs;
            bool isTop = (mbAddr & 1) == 0;
            bool field = _fieldMb[mbAddr];
            bool sameSliceOnly = slice.DisableDeblockingFilterIdc == 2;

            // The left pair, and the macroblock above: the top one of a frame pair has its own
            // pair's top macroblock above it; the others the pair above.
            bool left = pairX > 0 && (!sameSliceOnly || PairIfAvailable(mbAddr, pairX - 1, pairY) >= 0);
            bool withinPair = !isTop && !field;
            bool top = withinPair || (pairY > 0 && (!sameSliceOnly || PairIfAvailable(mbAddr, pairX, pairY - 1) >= 0));

            int qp = _qp[mbAddr];
            int qpCb = ChromaQp(qp, slice.CbQpOffset);
            int qpCr = ChromaQp(qp, slice.CrQpOffset);
            var (luma, lumaStride) = LumaPlace(mbAddr);
            var (chroma, chromaStride) = ChromaPlace(mbAddr);
            bool transform8x8 = _transform8x8[mbAddr];

            // Luma: vertical edges, then horizontal ones.
            if (left)
            {
                for (int y = 0; y < 16; y++)
                {
                    var (p, xP, yP) = AffNeighbor(mbAddr, -1, y, 16, 16, anySlice: true);
                    var (pOrigin, pStride) = LumaPlace(p);
                    FilterMbaffLine(Picture.Y, pOrigin + (yP * pStride) + xP, 1, luma + (y * lumaStride), 1, 4, _qp[p], qp, slice, chroma: false);
                }
            }

            for (int edge = 1; edge < 4; edge++)
            {
                if ((edge & 1) == 0 || !transform8x8)
                {
                    FilterEdge(Picture.Y, luma + (edge * 4), 1, lumaStride, 16, internalStrengths, qp, qp, slice.AlphaOffset, slice.BetaOffset, chroma: false);
                }
            }

            if (top)
            {
                foreach (var (p, pRow, pStep, qOffset, qStep, bS) in TopEdges(mbAddr, 16))
                {
                    var (pOrigin, pStride) = LumaPlace(p);
                    for (int x = 0; x < 16; x++)
                    {
                        FilterMbaffLine(Picture.Y, pOrigin + (pRow * pStride) + x, pStep * lumaRow, luma + (qOffset * lumaRow) + x, qStep * lumaRow, bS, _qp[p], qp, slice, chroma: false);
                    }
                }
            }

            for (int edge = 1; edge < 4; edge++)
            {
                if ((edge & 1) == 0 || !transform8x8)
                {
                    FilterEdge(Picture.Y, luma + (edge * 4 * lumaStride), lumaStride, 1, 16, internalStrengths, qp, qp, slice.AlphaOffset, slice.BetaOffset, chroma: false);
                }
            }

            // Chroma (4:2:0): the macroblock edges and the edge in the middle.
            if (left)
            {
                for (int y = 0; y < 8; y++)
                {
                    var (p, xP, yP) = AffNeighbor(mbAddr, -1, y, 8, 8, anySlice: true);
                    var (pOrigin, pStride) = ChromaPlace(p);
                    FilterMbaffChromaLine(pOrigin + (yP * pStride) + xP, 1, chroma + (y * chromaStride), 1, 4, _qp[p], qp, slice);
                }
            }

            FilterChromaEdge(Picture.Cb, Picture.Cr, chroma + 4, 1, chromaStride, internalStrengths, qpCb, qpCb, qpCr, qpCr, slice.AlphaOffset, slice.BetaOffset);

            if (top)
            {
                foreach (var (p, pRow, pStep, qOffset, qStep, bS) in TopEdges(mbAddr, 8))
                {
                    var (pOrigin, pStride) = ChromaPlace(p);
                    for (int x = 0; x < 8; x++)
                    {
                        FilterMbaffChromaLine(pOrigin + (pRow * pStride) + x, pStep * chromaRow, chroma + (qOffset * chromaRow) + x, qStep * chromaRow, bS, _qp[p], qp, slice);
                    }
                }
            }

            FilterChromaEdge(Picture.Cb, Picture.Cr, chroma + (4 * chromaStride), chromaStride, 1, internalStrengths, qpCb, qpCb, qpCr, qpCr, slice.AlphaOffset, slice.BetaOffset);
        }
    }

    /// <summary>
    /// The top macroblock edge of an MBAFF macroblock as one or two sets of sample lines (8.7.1):
    /// the macroblock p above, its row holding p0 (in p's own rows), the steps from p0 to p1 and
    /// from q0 to q1 in picture rows, q0's picture row below the macroblock's first, and bS. A frame
    /// macroblock at the top of its pair under a field pair is filtered twice, as two field edges:
    /// its even rows against the top field macroblock above, its odd rows against the bottom one.
    /// </summary>
    private (int P, int PRow, int PStep, int QOffset, int QStep, int BS)[] TopEdges(int mbAddr, int size)
    {
        bool field = _fieldMb[mbAddr];
        bool isTop = (mbAddr & 1) == 0;
        if (!field && isTop)
        {
            int pair = mbAddr >> 1;
            int above = (pair - _widthInMbs) * 2;
            if (_fieldMb[above])
            {
                return [(above, size - 1, 2, 0, 2, 3), (above + 1, size - 1, 2, 1, 2, 3)];
            }
        }

        var (p, _, pRow) = AffNeighbor(mbAddr, 0, -1, size, size, anySlice: true);
        bool fieldEdge = field || _fieldMb[p];
        return [(p, pRow, fieldEdge ? 2 : 1, 0, field ? 2 : 1, fieldEdge ? 3 : 4)];
    }

    /// <summary>One luma line across an MBAFF macroblock edge, with the QPs of its two macroblocks.</summary>
    private static void FilterMbaffLine(byte[] plane, int p0, int pStep, int q0, int qStep, int bS, int qpP, int qpQ, SliceInfo slice, bool chroma)
    {
        int qpAv = (qpP + qpQ + 1) >> 1;
        int indexA = Math.Clamp(qpAv + slice.AlphaOffset, 0, 51);
        int alpha = Alpha[indexA];
        int beta = Beta[Math.Clamp(qpAv + slice.BetaOffset, 0, 51)];
        if (alpha != 0 && beta != 0)
        {
            FilterSamples(plane, p0, pStep, q0, qStep, bS, indexA, alpha, beta, chroma);
        }
    }

    /// <summary>One Cb and one Cr line across an MBAFF macroblock edge, each with its own QP_C.</summary>
    private void FilterMbaffChromaLine(int p0, int pStep, int q0, int qStep, int bS, int qpP, int qpQ, SliceInfo slice)
    {
        FilterMbaffLine(Picture.Cb, p0, pStep, q0, qStep, bS, ChromaQp(qpP, slice.CbQpOffset), ChromaQp(qpQ, slice.CbQpOffset), slice, chroma: true);
        FilterMbaffLine(Picture.Cr, p0, pStep, q0, qStep, bS, ChromaQp(qpP, slice.CrQpOffset), ChromaQp(qpQ, slice.CrQpOffset), slice, chroma: true);
    }
}

/// <summary>Counts of MBAFF macroblock pairs: by kind, and by how they meet their left and upper neighbours.</summary>
internal sealed class MbaffStatistics
{
    public int FieldPairs { get; set; }

    public int FramePairs { get; set; }

    /// <summary>Pairs whose left neighbour pair is of the other kind.</summary>
    public int MixedLeft { get; set; }

    /// <summary>Frame pairs under a field pair: their top edge is filtered as two field edges.</summary>
    public int FrameBelowField { get; set; }

    public int FieldBelowFrame { get; set; }
}

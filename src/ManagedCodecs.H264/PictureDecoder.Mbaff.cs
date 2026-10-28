using System.Numerics;
using System.Runtime.CompilerServices;

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
internal sealed partial class PictureDecoder<TPixel>
{
    // Whether the current picture is an MBAFF frame, and per macroblock whether it is a field macroblock.
    private bool _mbaff;

    // Whether macroblocks are stored by pair (MBAFF frames, and field pictures, whose macroblock n
    // of field parity p is stored at 2 * n + p, as a field macroblock of an MBAFF frame), and the
    // parity of the field picture being decoded (-1 for a frame).
    private bool _pairSlots;
    private int _fieldParity = -1;
    private bool[] _fieldMb = [];

    // Intra prediction in MBAFF frames reconstructs into these, with the neighbouring samples
    // gathered around the macroblock (row 0 and column 0), then copies the result out.
    private const int ScratchLumaStride = 32;
    private const int ScratchChromaStride = 32;
    private readonly TPixel[] _scratchY = new TPixel[ScratchLumaStride * 20];
    private readonly TPixel[] _scratchCb = new TPixel[ScratchChromaStride * 20]; // row -1, 8 or 16 rows (4:2:2, 4:4:4), slack
    private readonly TPixel[] _scratchCr = new TPixel[ScratchChromaStride * 20];

    // Inverse scans of field macroblocks (8.5.6, Tables 8-13 and 8-14), as raster indices.
    private static ReadOnlySpan<byte> Field4x4 => [0, 4, 1, 8, 12, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];

    private static ReadOnlySpan<byte> Field4x4Ac => [4, 1, 8, 12, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];

    private static ReadOnlySpan<byte> Field8x8 =>
    [
        0, 8, 16, 1, 9, 24, 32, 17, 2, 25, 40, 48, 56, 33, 10, 3, 18, 41, 49, 57, 26, 11, 4, 19, 34, 42, 50, 58, 27, 12, 5, 20,
        35, 43, 51, 59, 28, 13, 6, 21, 36, 44, 52, 60, 29, 14, 22, 37, 45, 53, 61, 30, 7, 15, 38, 46, 54, 62, 23, 31, 39, 47, 55, 63,
    ];

    private static readonly byte[] CavlcField8x8Scan = [.. Enumerable.Range(0, 64).Select(i => Field8x8[(4 * (i % 16)) + (i / 16)])];

    // Which rows of the current intra macroblock's left neighbouring samples intra prediction may
    // use (bit y: luma row y; chroma rows in their own mask). The same for every row, except in
    // MBAFF frames with constrained intra prediction, where the rows can come from two macroblocks
    // of a left pair of the other kind, one intra and one inter.
    private int _leftRows;
    private int _leftChromaRows;

    /// <summary>Counts of MBAFF pair kinds (shared by every picture of an <see cref="H264Decoder"/>).</summary>
    public MbaffStatistics Statistics { get; init; } = new();

    public FieldStatistics FieldStatistics { get; init; } = new();

    /// <summary>Where a macroblock is the planes being reconstructed into: the picture, or the MBAFF scratch.</summary>
    private readonly record struct MbTarget(TPixel[] Y, int YOrigin, int YStride, TPixel[] Cb, TPixel[] Cr, int COrigin, int CStride);

    /// <summary>Whether the macroblock is a field macroblock: of an MBAFF frame, or of a field picture (field scans and contexts, field geometry).</summary>
    private bool IsFieldMb(int mbAddr) => _pairSlots && _fieldMb[mbAddr];

    /// <summary>A field macroblock of an MBAFF frame: its reference indices name fields of the frame lists (8.4.2.1).</summary>
    private bool IsMbaffFieldMb(int mbAddr) => _mbaff && _fieldMb[mbAddr];

    /// <summary>The macroblock's top-left luma sample and the row stride of its samples.</summary>
    private (int Origin, int Stride) LumaPlace(int mbAddr)
    {
        int stride = Picture.LumaStride;
        if (!_pairSlots)
        {
            return ((_mbRow[mbAddr] * 16 * stride) + (_mbColumn[mbAddr] * 16), stride);
        }

        int pair = mbAddr >> 1;
        int row = _mbRow[pair] * 32;
        int bottom = mbAddr & 1;
        return _fieldMb[mbAddr]
            ? (((row + bottom) * stride) + (_mbColumn[pair] * 16), stride * 2)
            : (((row + (bottom * 16)) * stride) + (_mbColumn[pair] * 16), stride);
    }

    /// <summary>The macroblock's top-left chroma sample and the row stride of its samples.</summary>
    private (int Origin, int Stride) ChromaPlace(int mbAddr)
    {
        int stride = Picture.ChromaStride;
        if (!_pairSlots)
        {
            return ((_mbRow[mbAddr] * _mbHeightC * stride) + (_mbColumn[mbAddr] * _mbWidthC), stride);
        }

        int pair = mbAddr >> 1;
        int row = _mbRow[pair] * 2 * _mbHeightC;
        int bottom = mbAddr & 1;
        return _fieldMb[mbAddr]
            ? (((row + bottom) * stride) + (_mbColumn[pair] * _mbWidthC), stride * 2)
            : (((row + (bottom * _mbHeightC)) * stride) + (_mbColumn[pair] * _mbWidthC), stride);
    }

    private MbTarget PictureTarget(int mbAddr)
    {
        var (y, yStride) = LumaPlace(mbAddr);
        var (c, cStride) = ChromaPlace(mbAddr);
        return new MbTarget(PlaneY, y, yStride, PlaneCb, PlaneCr, c, cStride);
    }

    /// <summary>
    /// The neighbouring location (6.4.12): the macroblock covering (<paramref name="xN"/>,
    /// <paramref name="yN"/>) relative to the current macroblock's top-left in a block of
    /// <paramref name="maxW"/> x <paramref name="maxH"/> samples (16 for luma, 8 for chroma), and the
    /// location inside it; Mb is -1 when it is not available.
    /// </summary>
    private (int Mb, int X, int Y) Neighbor(int mbAddr, int xN, int yN, int maxW = 16, int maxH = 16)
    {
        if (_pairSlots)
        {
            // Most lookups are inside the macroblock (or right of or below it): Table 6-4's first cases, without the call.
            if (yN >= maxH || (xN >= maxW && yN >= 0))
            {
                return (-1, 0, 0);
            }

            return xN >= 0 && yN >= 0 ? (mbAddr, xN, yN) : AffNeighbor(mbAddr, xN, yN, maxW, maxH);
        }

        // maxW and maxH are 8 or 16: the mask is (xN + maxW) % maxW for xN from -maxW on.
        int n = NeighborMb(mbAddr, xN, yN, maxW, maxH);
        return (n, xN & (maxW - 1), yN & (maxH - 1));
    }

    /// <summary>
    /// The top macroblock of the pair at (<paramref name="pairX"/>, <paramref name="pairY"/>), or -1 if
    /// it is outside the picture, not decoded or (unless <paramref name="anySlice"/>) in another slice.
    /// </summary>
    private int PairIfAvailable(int mbAddr, int pairX, int pairY, bool anySlice = false) =>
        PairIfAvailable(_sliceOfMb, _widthInMbs, mbAddr, pairX, pairY, anySlice, Math.Max(_fieldParity, 0));

    /// <remarks>A field picture's pairs hold only one of its macroblocks, at <paramref name="slotParity"/>: availability is that one's.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PairIfAvailable(ReadOnlySpan<int> sliceOfMb, int widthInMbs, int mbAddr, int pairX, int pairY, bool anySlice, int slotParity = 0)
    {
        if (pairX < 0 || pairX >= widthInMbs || pairY < 0)
        {
            return -1;
        }

        int top = ((pairY * widthInMbs) + pairX) * 2;
        int slot = top + slotParity;
        return sliceOfMb[slot] >= 0 && (anySlice || sliceOfMb[slot] == sliceOfMb[mbAddr]) ? top : -1;
    }

    /// <summary>Table 6-4: neighbouring locations in MBAFF frames (the deblocking filter also looks into other slices).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (int Mb, int X, int Y) AffNeighbor(int mbAddr, int xN, int yN, int maxW, int maxH, bool anySlice = false) =>
        AffNeighbor(_fieldMb, _sliceOfMb, _widthInMbs, mbAddr, xN, yN, maxW, maxH, anySlice, Math.Max(_fieldParity, 0));

    /// <summary>
    /// Table 6-4 for macroblock <paramref name="mbAddr"/> of an MBAFF frame <paramref name="widthInMbs"/>
    /// pairs wide, given which macroblocks are field macroblocks and the slice of each (-1: not decoded).
    /// </summary>
    internal static (int Mb, int X, int Y) AffNeighbor(ReadOnlySpan<bool> fieldMb, ReadOnlySpan<int> sliceOfMb, int widthInMbs, int mbAddr, int xN, int yN, int maxW, int maxH, bool anySlice = false, int slotParity = 0)
    {
        if (yN >= maxH || (xN >= maxW && yN >= 0))
        {
            return (-1, 0, 0);
        }

        if (xN >= 0 && yN >= 0)
        {
            return (mbAddr, xN, yN);
        }

        // One division: the inside case above needs none, and the remainder is a multiplication away.
        int pair = mbAddr >> 1;
        int pairY = pair / widthInMbs;
        int pairX = pair - (pairY * widthInMbs);
        bool isTop = (mbAddr & 1) == 0;
        bool frame = !fieldMb[mbAddr];
        int n;
        int yM;

        if (xN < 0 && yN < 0)
        {
            // D: above-left; a frame macroblock's bottom half looks into the left pair instead.
            if (frame && !isTop)
            {
                n = PairIfAvailable(sliceOfMb, widthInMbs, mbAddr, pairX - 1, pairY, anySlice, slotParity);
                if (n < 0)
                {
                    return (-1, 0, 0);
                }

                (n, yM) = fieldMb[n] ? (n + 1, (yN + maxH) >> 1) : (n, yN);
            }
            else
            {
                n = PairIfAvailable(sliceOfMb, widthInMbs, mbAddr, pairX - 1, pairY - 1, anySlice, slotParity);
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
            n = PairIfAvailable(sliceOfMb, widthInMbs, mbAddr, pairX - 1, pairY, anySlice, slotParity);
            if (n < 0)
            {
                return (-1, 0, 0);
            }

            (n, yM) = AffLeft(n, fieldMb[n], frame, isTop, yN, maxH);
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
                n = PairIfAvailable(sliceOfMb, widthInMbs, mbAddr, pairX + (c ? 1 : 0), pairY - 1, anySlice, slotParity);
                if (n < 0)
                {
                    return (-1, 0, 0);
                }

                (n, yM) = frame || !isTop ? (n + 1, yN) : fieldMb[n] ? (n, yN) : (n + 1, 2 * yN);
            }
        }

        return (n, xN & (maxW - 1), yM & (maxH - 1));
    }

    /// <summary>
    /// Table 6-4's A rows: the macroblock of the left pair (top macroblock <paramref name="leftTop"/>, available) that
    /// row <paramref name="yN"/> of a block <paramref name="maxH"/> rows high looks into, and the row in it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int Mb, int Y) AffLeft(int leftTop, bool leftField, bool frame, bool isTop, int yN, int maxH)
    {
        if (frame)
        {
            // Frame row (isTop ? 0 : maxH) + yN of the pair.
            int row = (isTop ? 0 : maxH) + yN;
            return leftField ? (leftTop + (row & 1), row >> 1) : (leftTop + (row >= maxH ? 1 : 0), row & (maxH - 1));
        }

        if (leftField)
        {
            return (leftTop + (isTop ? 0 : 1), yN);
        }

        // Field row yN of the current parity is frame row 2 * yN (+ 1 for the bottom field).
        int fieldRow = (2 * yN) + (isTop ? 0 : 1);
        return (leftTop + (fieldRow >= maxH ? 1 : 0), fieldRow & (maxH - 1));
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

    private void SetLeftRows(SliceState slice, int mbAddr, bool left)
    {
        if (!_mbaff || !slice.Pps.ConstrainedIntraPred)
        {
            _leftRows = left ? 0xFFFF : 0;
            _leftChromaRows = left ? 0xFFFF : 0;
            return;
        }

        _leftRows = 0;
        _leftChromaRows = 0;
        for (int y = 0; y < 16; y++)
        {
            int n = AffNeighbor(mbAddr, -1, y, 16, 16).Mb;
            _leftRows |= n >= 0 && !IsInter(n) ? 1 << y : 0;
        }

        for (int y = 0; y < _mbHeightC; y++)
        {
            int n = AffNeighbor(mbAddr, -1, y, _mbWidthC, _mbHeightC).Mb;
            _leftChromaRows |= n >= 0 && !IsInter(n) ? 1 << y : 0;
        }
    }

    /// <summary>Whether luma rows <paramref name="first"/> .. first + count - 1 of the left neighbouring samples may be used (8.3.1.2: all of a block's, or none).</summary>
    private bool LeftRows(int first, int count)
    {
        int mask = ((1 << count) - 1) << first;
        return (_leftRows & mask) == mask;
    }

    /// <summary>Whether the four chroma rows from <paramref name="first"/> of the left neighbouring samples may be used.</summary>
    private bool LeftChromaRows(int first) => ((_leftChromaRows >> first) & 15) == 15;

    /// <summary>Per row of chroma 4x4 blocks (bit k: rows 4k to 4k + 3), whether its left neighbouring samples may be used.</summary>
    private int LeftChromaBlocks()
    {
        int blocks = 0;
        for (int k = 0; k < _mbHeightC / 4; k++)
        {
            blocks |= LeftChromaRows(k * 4) ? 1 << k : 0;
        }

        return blocks;
    }

    /// <summary>Sets whether the pair holding <paramref name="mbAddr"/> is a field pair.</summary>
    private void SetPairField(int mbAddr, bool field)
    {
        int top = mbAddr & ~1;
        _fieldMb[top] = field;
        _fieldMb[top + 1] = field;
    }

    /// <summary>
    /// The kind a pair is inferred to be while its mb_field_decoding_flag has not come (7.4.4):
    /// the left pair's, else the upper pair's (in the same slice), else a frame pair.
    /// </summary>
    private bool InferPairField(int mbAddr)
    {
        int pair = mbAddr >> 1;
        int left = PairIfAvailable(mbAddr, (pair % _widthInMbs) - 1, pair / _widthInMbs);
        if (left >= 0)
        {
            return _fieldMb[left];
        }

        int above = PairIfAvailable(mbAddr, pair % _widthInMbs, (pair / _widthInMbs) - 1);
        return above >= 0 && _fieldMb[above];
    }

    /// <summary>After a bottom macroblock of an MBAFF frame: counts the pair's kind and how it meets its neighbours.</summary>
    private void PairDone(int mbAddr)
    {
        if (_mbaff && (mbAddr & 1) != 0)
        {
            CountPair(mbAddr);
        }
    }

    private void CountPair(int bottom)
    {
        int pair = bottom >> 1;
        bool field = _fieldMb[bottom];
        int left = PairIfAvailable(bottom, (pair % _widthInMbs) - 1, pair / _widthInMbs, anySlice: true);
        int above = PairIfAvailable(bottom, pair % _widthInMbs, (pair / _widthInMbs) - 1, anySlice: true);
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

        bool topSkipped = _kind[bottom - 1] == MbKind.PSkip;
        bool bottomSkipped = _kind[bottom] == MbKind.PSkip;
        if (topSkipped && bottomSkipped)
        {
            stats.SkippedPairs++;
        }
        else if (topSkipped)
        {
            stats.TopOnlySkipped++;
        }
    }

    /// <summary>
    /// The planes an intra macroblock of an MBAFF frame is reconstructed into: the scratch blocks,
    /// with every neighbouring sample prediction may read gathered through Table 6-4 (luma row -1
    /// from x = -1 to 23 and column -1; chroma row -1 and column -1, 8 or 16 rows, in 4:4:4 as luma).
    /// </summary>
    private MbTarget GatherMbaffNeighbors(int mbAddr)
    {
        GatherPlane(mbAddr, PlaneY, _scratchY, ScratchLumaStride, 16, 16, 24);

        // 4:4:4 chroma takes the luma intra modes, which read row -1 up to x = 23 too.
        int topWidth = _chroma444 ? 24 : 8;
        if (!_monochrome)
        {
            GatherPlane(mbAddr, PlaneCb, _scratchCb, ScratchChromaStride, _mbWidthC, _mbHeightC, topWidth);
            GatherPlane(mbAddr, PlaneCr, _scratchCr, ScratchChromaStride, _mbWidthC, _mbHeightC, topWidth);
        }

        return new MbTarget(_scratchY, ScratchLumaStride + 1, ScratchLumaStride, _scratchCb, _scratchCr, ScratchChromaStride + 1, ScratchChromaStride);
    }

    private void GatherPlane(int mbAddr, TPixel[] plane, TPixel[] scratch, int scratchStride, int maxW, int maxH, int topWidth)
    {
        bool luma = plane == PlaneY;
        Copy(-1, -1);

        // Column -1 from A: the left pair found once, then Table 6-4's rows.
        int pair = mbAddr >> 1;
        int pairY = pair / _widthInMbs;
        int leftTop = PairIfAvailable(mbAddr, pair - (pairY * _widthInMbs) - 1, pairY);
        if (leftTop >= 0)
        {
            bool leftField = _fieldMb[leftTop];
            bool frame = !_fieldMb[mbAddr];
            bool isTop = (mbAddr & 1) == 0;
            for (int yN = 0; yN < maxH; yN++)
            {
                var (n, y) = AffLeft(leftTop, leftField, frame, isTop, yN, maxH);
                var (origin, stride) = luma ? LumaPlace(n) : ChromaPlace(n);
                scratch[(yN + 1) * scratchStride] = plane[origin + (y * stride) + maxW - 1];
            }
        }

        // Row -1 comes from B (x below maxW) and C (from maxW on), each a run of one row of one macroblock.
        for (int xN = 0; xN < topWidth; xN += maxW)
        {
            var (n, x, y) = AffNeighbor(mbAddr, xN, -1, maxW, maxH);
            if (n >= 0)
            {
                var (origin, stride) = luma ? LumaPlace(n) : ChromaPlace(n);
                plane.AsSpan(origin + (y * stride) + x, Math.Min(maxW, topWidth - xN)).CopyTo(scratch.AsSpan(xN + 1));
            }
        }

        void Copy(int xN, int yN)
        {
            var (n, x, y) = AffNeighbor(mbAddr, xN, yN, maxW, maxH);
            if (n >= 0)
            {
                var (origin, stride) = luma ? LumaPlace(n) : ChromaPlace(n);
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
            target.Y.AsSpan(target.YOrigin + (y * target.YStride), 16).CopyTo(PlaneY.AsSpan(luma + (y * lumaStride)));
        }

        var (chroma, chromaStride) = ChromaPlace(mbAddr);
        for (int y = 0; y < _mbHeightC; y++)
        {
            target.Cb.AsSpan(target.COrigin + (y * target.CStride), _mbWidthC).CopyTo(PlaneCb.AsSpan(chroma + (y * chromaStride)));
            target.Cr.AsSpan(target.COrigin + (y * target.CStride), _mbWidthC).CopyTo(PlaneCr.AsSpan(chroma + (y * chromaStride)));
        }
    }

    /// <summary>
    /// The deblocking filter of the pair rows from <paramref name="firstRow"/> up to <paramref name="endRow"/> of an
    /// MBAFF frame or a field picture (8.7), macroblock by macroblock in address order, sample line by sample line
    /// across macroblock edges, where the two sides may differ in field/frame mode and so each line can meet another
    /// macroblock and another bS.
    /// </summary>
    [SkipLocalsInit]
    private void DeblockMbaff(int firstRow, int endRow)
    {
        Span<byte> vertical = stackalloc byte[16];
        Span<byte> horizontal = stackalloc byte[16];
        Span<byte> leftLines = stackalloc byte[16];
        Span<byte> topSegments = stackalloc byte[4];
        Span<byte> leftSegments = stackalloc byte[4];
        int lumaRow = Picture.LumaStride;
        int chromaRow = Picture.ChromaStride;

        // A field picture filters its own macroblocks only (the other field is another picture).
        int first = Math.Max(_fieldParity, 0);
        int step = _fieldParity >= 0 ? 2 : 1;
        for (int mbAddr = (firstRow * 2 * _widthInMbs) + first; mbAddr < endRow * 2 * _widthInMbs; mbAddr += step)
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
            int leftTop = (pair - 1) * 2;
            bool withinPair = !isTop && !field;
            bool top = withinPair || (pairY > 0 && (!sameSliceOnly || PairIfAvailable(mbAddr, pairX, pairY - 1) >= 0));

            int qp = _qp[mbAddr];
            int qpCb = ChromaQp(qp, slice.CbQpOffset, _qpBdOffset);
            int qpCr = ChromaQp(qp, slice.CrQpOffset, _qpBdOffset);
            var (luma, lumaStride) = LumaPlace(mbAddr);
            var (chroma, chromaStride) = ChromaPlace(mbAddr);
            bool transform8x8 = _transform8x8[mbAddr];

            // Internal edges as in frames, with field macroblocks' vertical motion in field units.
            if (IsIntra(mbAddr))
            {
                vertical.Fill(3);
                horizontal.Fill(3);
            }
            else if (HasUniformMotionWithoutCoefficients(mbAddr))
            {
                vertical.Clear();
                horizontal.Clear();
            }
            else
            {
                InternalStrengths(mbAddr, vertical, horizontal, field ? 2 : 4);
            }

            // Luma (and 4:4:4 chroma, at the same places): vertical edges, then horizontal ones.
            if (left && _fieldMb[leftTop] == field)
            {
                // Both pairs of one kind: line y meets line y of the left pair's macroblock of the same position, a
                // plain vertical edge of four segments.
                int p = leftTop + (mbAddr & 1);
                for (int k = 0; k < 4; k++)
                {
                    leftLines.Slice(k * 4, 4).Fill((byte)MbaffStrength(p, (k * 4) + 3, mbAddr, k * 4, horizontalEdge: false));
                    leftSegments[k] = leftLines[k * 4];
                }

                FilterLumaEdge(luma, 1, lumaStride, leftSegments, _qp[p], qp, slice);
            }
            else if (left)
            {
                bool leftField = _fieldMb[leftTop];
                for (int y = 0; y < 16; y++)
                {
                    var (p, yP) = AffLeft(leftTop, leftField, !field, isTop, y, 16);
                    const int xP = 15;
                    leftLines[y] = (byte)MbaffStrength(p, ((yP >> 2) * 4) + 3, mbAddr, (y >> 2) * 4, horizontalEdge: false);
                    if (leftLines[y] != 0)
                    {
                        var (pOrigin, pStride) = LumaPlace(p);
                        FilterMbaffLumaLine(pOrigin + (yP * pStride) + xP, 1, luma + (y * lumaStride), 1, leftLines[y], _qp[p], qp, slice);
                    }
                }
            }

            for (int edge = 1; edge < 4; edge++)
            {
                if ((edge & 1) == 0 || !transform8x8)
                {
                    FilterInternalLumaEdge(luma + (edge * 4), 1, lumaStride, vertical.Slice(edge * 4, 4), qp, slice);
                }
            }

            var topEdges = top ? TopEdges(mbAddr, 16) : [];
            foreach (var (p, pRow, pStep, qOffset, qStep) in topEdges)
            {
                for (int k = 0; k < 4; k++)
                {
                    topSegments[k] = (byte)MbaffStrength(p, ((pRow >> 2) * 4) + k, mbAddr, ((qOffset >> 2) * 4) + k, horizontalEdge: true);
                }

                var (pOrigin, pStride) = LumaPlace(p);
                int q0 = luma + (qOffset * lumaRow);
                if (pStep == qStep && pOrigin + (pRow * pStride) == q0 - (qStep * lumaRow))
                {
                    // p0 is the line above q0 at q's step: a plain horizontal edge.
                    FilterLumaEdge(q0, qStep * lumaRow, 1, topSegments, _qp[p], qp, slice);
                    continue;
                }

                for (int x = 0; x < 16; x++)
                {
                    if (topSegments[x >> 2] != 0)
                    {
                        FilterMbaffLumaLine(pOrigin + (pRow * pStride) + x, pStep * lumaRow, luma + (qOffset * lumaRow) + x, qStep * lumaRow, topSegments[x >> 2], _qp[p], qp, slice);
                    }
                }
            }

            for (int edge = 1; edge < 4; edge++)
            {
                if ((edge & 1) == 0 || !transform8x8)
                {
                    FilterInternalLumaEdge(luma + (edge * 4 * lumaStride), lumaStride, 1, horizontal.Slice(edge * 4, 4), qp, slice);
                }
            }

            if (_chroma444 || _monochrome)
            {
                continue;
            }

            if (_mbHeightC == 16)
            {
                DeblockMbaffChroma422(slice, mbAddr, left, leftLines, vertical, horizontal, topEdges);
                continue;
            }

            // Chroma (4:2:0): the macroblock edges, with the bS of luma lines of the same parity
            // as each chroma line, and the edge in the middle.
            if (left && _fieldMb[leftTop] == field)
            {
                // As luma's: chroma segment k (lines 2k, 2k + 1) is luma segment k's.
                int p = leftTop + (mbAddr & 1);
                FilterChromaEdge(PlaneCb, PlaneCr, chroma, 1, chromaStride, leftSegments, ChromaQp(_qp[p], slice.CbQpOffset, _qpBdOffset), qpCb, ChromaQp(_qp[p], slice.CrQpOffset, _qpBdOffset), qpCr, slice.AlphaOffset, slice.BetaOffset, bitDepth: _bitDepth);
            }
            else if (left)
            {
                bool leftField = _fieldMb[leftTop];
                for (int y = 0; y < 8; y++)
                {
                    var (p, yP) = AffLeft(leftTop, leftField, !field, isTop, y, 8);
                    const int xP = 7;
                    int bS = leftLines[field && !_fieldMb[p] ? y << 1 : ((y >> 1) << 2) + (y & 1)];
                    if (bS != 0)
                    {
                        var (pOrigin, pStride) = ChromaPlace(p);
                        FilterMbaffChromaLine(pOrigin + (yP * pStride) + xP, 1, chroma + (y * chromaStride), 1, bS, _qp[p], qp, slice);
                    }
                }
            }

            FilterChromaEdge(PlaneCb, PlaneCr, chroma + 4, 1, chromaStride, vertical.Slice(8, 4), qpCb, qpCb, qpCr, qpCr, slice.AlphaOffset, slice.BetaOffset, bitDepth: _bitDepth);

            foreach (var (p, pRow, pStep, qOffset, qStep) in top ? TopEdges(mbAddr, 8) : [])
            {
                for (int k = 0; k < 4; k++)
                {
                    // The luma segments of the same pass: chroma pRow is in chroma rows.
                    var (pLuma, pLumaRow, _, qLumaOffset, _) = topEdges[qOffset];
                    topSegments[k] = (byte)MbaffStrength(pLuma, ((pLumaRow >> 2) * 4) + k, mbAddr, ((qLumaOffset >> 2) * 4) + k, horizontalEdge: true);
                }

                var (pOrigin, pStride) = ChromaPlace(p);
                int q0 = chroma + (qOffset * chromaRow);
                if (pStep == qStep && pOrigin + (pRow * pStride) == q0 - (qStep * chromaRow))
                {
                    FilterChromaEdge(PlaneCb, PlaneCr, q0, qStep * chromaRow, 1, topSegments, ChromaQp(_qp[p], slice.CbQpOffset, _qpBdOffset), qpCb, ChromaQp(_qp[p], slice.CrQpOffset, _qpBdOffset), qpCr, slice.AlphaOffset, slice.BetaOffset, bitDepth: _bitDepth);
                    continue;
                }

                for (int x = 0; x < 8; x++)
                {
                    if (topSegments[x >> 1] != 0)
                    {
                        FilterMbaffChromaLine(pOrigin + (pRow * pStride) + x, pStep * chromaRow, chroma + (qOffset * chromaRow) + x, qStep * chromaRow, topSegments[x >> 1], _qp[p], qp, slice);
                    }
                }
            }

            FilterChromaEdge(PlaneCb, PlaneCr, chroma + (4 * chromaStride), chromaStride, 1, horizontal.Slice(8, 4), qpCb, qpCb, qpCr, qpCr, slice.AlphaOffset, slice.BetaOffset, bitDepth: _bitDepth);
        }
    }

    /// <summary>
    /// The chroma edges of a 4:2:2 macroblock of an MBAFF frame or a field picture. Its chroma
    /// rows are the luma's (16 a macroblock), so every chroma line meets the macroblock its luma
    /// line meets and takes that line's bS: <paramref name="leftLines"/> across the left edge,
    /// the luma pass's <paramref name="topEdges"/> (two of them under a field pair) across the
    /// top one, chroma column x the luma segment x / 2. Inside, as in frames: vertical edge 4
    /// along 16 rows, horizontal edges 4, 8 and 12 with the bS of luma edges 1 to 3.
    /// </summary>
    private void DeblockMbaffChroma422(SliceInfo slice, int mbAddr, bool left, ReadOnlySpan<byte> leftLines, ReadOnlySpan<byte> vertical, ReadOnlySpan<byte> horizontal, (int P, int PRow, int PStep, int QOffset, int QStep)[] topEdges)
    {
        int qp = _qp[mbAddr];
        int qpCb = ChromaQp(qp, slice.CbQpOffset, _qpBdOffset);
        int qpCr = ChromaQp(qp, slice.CrQpOffset, _qpBdOffset);
        var (chroma, chromaStride) = ChromaPlace(mbAddr);
        int chromaRow = Picture.ChromaStride;
        if (left)
        {
            for (int y = 0; y < 16; y++)
            {
                if (leftLines[y] != 0)
                {
                    var (p, xP, yP) = AffNeighbor(mbAddr, -1, y, 8, 16, anySlice: true);
                    var (pOrigin, pStride) = ChromaPlace(p);
                    FilterMbaffChromaLine(pOrigin + (yP * pStride) + xP, 1, chroma + (y * chromaStride), 1, leftLines[y], _qp[p], qp, slice);
                }
            }
        }

        var strengths = vertical.Slice(8, 4);
        FilterEdge(PlaneCb, chroma + 4, 1, chromaStride, 16, strengths, qpCb, qpCb, slice.AlphaOffset, slice.BetaOffset, chroma: true, segmentShift: 2, bitDepth: _bitDepth);
        FilterEdge(PlaneCr, chroma + 4, 1, chromaStride, 16, strengths, qpCr, qpCr, slice.AlphaOffset, slice.BetaOffset, chroma: true, segmentShift: 2, bitDepth: _bitDepth);

        Span<byte> topSegments = stackalloc byte[4];
        foreach (var (p, pRow, pStep, qOffset, qStep) in topEdges)
        {
            for (int k = 0; k < 4; k++)
            {
                topSegments[k] = (byte)MbaffStrength(p, ((pRow >> 2) * 4) + k, mbAddr, ((qOffset >> 2) * 4) + k, horizontalEdge: true);
            }

            var (pOrigin, pStride) = ChromaPlace(p);
            for (int x = 0; x < 8; x++)
            {
                if (topSegments[x >> 1] != 0)
                {
                    FilterMbaffChromaLine(pOrigin + (pRow * pStride) + x, pStep * chromaRow, chroma + (qOffset * chromaRow) + x, qStep * chromaRow, topSegments[x >> 1], _qp[p], qp, slice);
                }
            }
        }

        for (int edge = 1; edge < 4; edge++)
        {
            FilterChromaEdge(PlaneCb, PlaneCr, chroma + (edge * 4 * chromaStride), chromaStride, 1, horizontal.Slice(edge * 4, 4), qpCb, qpCb, qpCr, qpCr, slice.AlphaOffset, slice.BetaOffset, bitDepth: _bitDepth);
        }
    }

    /// <summary>
    /// bS across a macroblock edge of an MBAFF frame between luma 4x4 blocks p and q (raster
    /// indices): 4 for intra on vertical edges and on horizontal ones between frame macroblocks,
    /// else 3 for intra; 2 for coefficients; 1 where a frame and a field macroblock meet (their
    /// references and motion are not comparable); else from the motion, with a field
    /// macroblock's vertical motion compared in field units.
    /// </summary>
    private int MbaffStrength(int mbP, int blockP, int mbQ, int blockQ, bool horizontalEdge)
    {
        bool mixed = _fieldMb[mbP] != _fieldMb[mbQ];
        if (IsIntra(mbP) || IsIntra(mbQ))
        {
            return horizontalEdge && (_fieldMb[mbP] || _fieldMb[mbQ]) ? 3 : 4;
        }

        if (HasCoefficients(mbP, blockP) || HasCoefficients(mbQ, blockQ))
        {
            return 2;
        }

        return mixed ? 1 : MotionStrength((mbP * 16) + blockP, (mbQ * 16) + blockQ, _fieldMb[mbQ] ? 2 : 4);
    }

    /// <summary>
    /// The top macroblock edge of an MBAFF macroblock as one or two sets of sample lines (8.7.1):
    /// the macroblock p above, its row holding p0 (in p's own rows), the steps from p0 to p1 and
    /// from q0 to q1 in picture rows, and q0's picture row below the macroblock's first. A frame
    /// macroblock at the top of its pair under a field pair is filtered twice, as two field edges:
    /// its even rows against the top field macroblock above, its odd rows against the bottom one.
    /// </summary>
    private (int P, int PRow, int PStep, int QOffset, int QStep)[] TopEdges(int mbAddr, int size)
    {
        bool field = _fieldMb[mbAddr];
        bool isTop = (mbAddr & 1) == 0;
        if (!field && isTop)
        {
            int pair = mbAddr >> 1;
            int above = (pair - _widthInMbs) * 2;
            if (_fieldMb[above])
            {
                return [(above, size - 1, 2, 0, 2), (above + 1, size - 1, 2, 1, 2)];
            }
        }

        var (p, _, pRow) = AffNeighbor(mbAddr, 0, -1, size, size, anySlice: true);
        bool fieldEdge = field || _fieldMb[p];
        return [(p, pRow, fieldEdge ? 2 : 1, 0, field ? 2 : 1)];
    }

    /// <summary>One luma line across an MBAFF macroblock edge, with the QPs of its two macroblocks.</summary>
    private void FilterMbaffLine(TPixel[] plane, int p0, int pStep, int q0, int qStep, int bS, int qpP, int qpQ, SliceInfo slice, bool chroma)
    {
        int qpAv = (qpP + qpQ + 1) >> 1;
        int indexA = Math.Clamp(qpAv + slice.AlphaOffset, 0, 51);
        int alpha = Alpha[indexA] << (_bitDepth - 8);
        int beta = Beta[Math.Clamp(qpAv + slice.BetaOffset, 0, 51)] << (_bitDepth - 8);
        if (alpha != 0 && beta != 0)
        {
            FilterSamples(plane, p0, pStep, q0, qStep, bS, indexA, alpha, beta, chroma, _bitDepth);
        }
    }

    /// <summary>
    /// One luma line across an MBAFF macroblock edge, and in 4:4:4 the Cb and Cr lines at the same
    /// place, with the luma filter at their QP_C (8.7.2: chromaStyleFilteringFlag 0).
    /// </summary>
    private void FilterMbaffLumaLine(int p0, int pStep, int q0, int qStep, int bS, int qpP, int qpQ, SliceInfo slice)
    {
        FilterMbaffLine(PlaneY, p0, pStep, q0, qStep, bS, qpP, qpQ, slice, chroma: false);
        if (_chroma444)
        {
            FilterMbaffLine(PlaneCb, p0, pStep, q0, qStep, bS, ChromaQp(qpP, slice.CbQpOffset, _qpBdOffset), ChromaQp(qpQ, slice.CbQpOffset, _qpBdOffset), slice, chroma: false);
            FilterMbaffLine(PlaneCr, p0, pStep, q0, qStep, bS, ChromaQp(qpP, slice.CrQpOffset, _qpBdOffset), ChromaQp(qpQ, slice.CrQpOffset, _qpBdOffset), slice, chroma: false);
        }
    }

    /// <summary>An internal luma edge of an MBAFF macroblock, and in 4:4:4 the Cb and Cr edges at the same place.</summary>
    private void FilterInternalLumaEdge(int q0, int across, int along, ReadOnlySpan<byte> strengths, int qp, SliceInfo slice) =>
        FilterLumaEdge(q0, across, along, strengths, qp, qp, slice);

    /// <summary>
    /// A luma edge of 16 lines whose p samples continue q's at the same step (p0 at q0 - across), between macroblocks of
    /// QPs <paramref name="qpP"/> and <paramref name="qpQ"/>, and in 4:4:4 the Cb and Cr edges at the same place.
    /// </summary>
    private void FilterLumaEdge(int q0, int across, int along, ReadOnlySpan<byte> strengths, int qpP, int qpQ, SliceInfo slice)
    {
        FilterEdge(PlaneY, q0, across, along, 16, strengths, qpP, qpQ, slice.AlphaOffset, slice.BetaOffset, chroma: false, bitDepth: _bitDepth);
        if (_chroma444)
        {
            FilterEdge(PlaneCb, q0, across, along, 16, strengths, ChromaQp(qpP, slice.CbQpOffset, _qpBdOffset), ChromaQp(qpQ, slice.CbQpOffset, _qpBdOffset), slice.AlphaOffset, slice.BetaOffset, chroma: false, bitDepth: _bitDepth);
            FilterEdge(PlaneCr, q0, across, along, 16, strengths, ChromaQp(qpP, slice.CrQpOffset, _qpBdOffset), ChromaQp(qpQ, slice.CrQpOffset, _qpBdOffset), slice.AlphaOffset, slice.BetaOffset, chroma: false, bitDepth: _bitDepth);
        }
    }

    /// <summary>One Cb and one Cr line across an MBAFF macroblock edge, each with its own QP_C.</summary>
    private void FilterMbaffChromaLine(int p0, int pStep, int q0, int qStep, int bS, int qpP, int qpQ, SliceInfo slice)
    {
        FilterMbaffLine(PlaneCb, p0, pStep, q0, qStep, bS, ChromaQp(qpP, slice.CbQpOffset, _qpBdOffset), ChromaQp(qpQ, slice.CbQpOffset, _qpBdOffset), slice, chroma: true);
        FilterMbaffLine(PlaneCr, p0, pStep, q0, qStep, bS, ChromaQp(qpP, slice.CrQpOffset, _qpBdOffset), ChromaQp(qpQ, slice.CrQpOffset, _qpBdOffset), slice, chroma: true);
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

    /// <summary>Pairs with both macroblocks skipped (the kind inferred) and with only the top one skipped (the kind sent with the bottom one).</summary>
    public int SkippedPairs { get; set; }

    public int TopOnlySkipped { get; set; }

    /// <summary>Field macroblock partitions predicted from the field of the other parity.</summary>
    public int OppositeParity { get; set; }

    /// <summary>Direct predicted 8x8 blocks of field macroblocks, and how many of them temporal.</summary>
    public int DirectField { get; set; }

    public int TemporalField { get; set; }

    /// <summary>Co-located blocks in a pair of the other kind (Frm_To_Fld or Fld_To_Frm).</summary>
    public int MixedCoLocated { get; set; }

    /// <summary>Bi-predicted field macroblock partitions with implicit weights.</summary>
    public int ImplicitField { get; set; }
}

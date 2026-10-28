namespace ManagedCodecs.H264;

/// <summary>
/// The neighbour context of the macroblock being decoded (docs/decisions/2026-10-28-h264-neighbour-context.md): its
/// neighbouring macroblocks A, B, C and D (6.4.11.1) found once, and grids of the quantities that neighbouring 4x4
/// blocks are read for, filled from the picture-wide arrays the first time the macroblock reads them and kept up to
/// date as it writes them. Frames only (not MBAFF frames or field pictures, whose neighbours follow Table 6-4): there
/// the reads below go through <see cref="Neighbor"/> as before.
/// </summary>
/// <remarks>
/// A grid holds 6 x 5 blocks of 4x4 samples, row-major: the macroblock's 4 x 4, the column left of it (A's right
/// column), the row above it (B's bottom row), C's first block right of that row and D's last block left of it, so a
/// neighbour at any offset of 6.4.12 is one index: <see cref="Cell"/>. Its fifth column below the top row is outside
/// the picture for every block (6.4.12: xN beyond the macroblock with yN inside it) and stays unavailable.
/// </remarks>
internal sealed partial class PictureDecoder<TPixel>
{
    private const int GridStride = 6;
    private const int GridCells = GridStride * 5;

    // A neighbour that is not available: in a reference index grid, and in a coefficient count or intra mode grid.
    private const sbyte NoMotion = -2;
    private const sbyte NoBlock = -1;

    // The quantities filled for the current macroblock, as bits: motion of list 0 and 1, luma (and 4:4:4 Cb and Cr)
    // coefficient counts, chroma coefficient counts, intra modes.
    private const int MotionFilled = 1;
    private const int LumaCountsFilled = 4;
    private const int ChromaCountsFilled = 32;
    private const int IntraModesFilled = 128;

    private readonly short[][] _gridMvX = [new short[GridCells], new short[GridCells]];
    private readonly short[][] _gridMvY = [new short[GridCells], new short[GridCells]];
    private readonly sbyte[][] _gridRefIdx = [new sbyte[GridCells], new sbyte[GridCells]];
    private readonly sbyte[][] _gridCounts = [new sbyte[GridCells], new sbyte[GridCells], new sbyte[GridCells]];
    private readonly sbyte[] _gridChromaCounts = new sbyte[2 * GridCells];
    private readonly sbyte[] _gridIntraModes = new sbyte[GridCells];

    private int _contextMb = -1;
    private int _contextA;
    private int _contextB;
    private int _contextC;
    private int _contextD;
    private int _contextFilled;

    /// <summary>
    /// For tests: derive every value the neighbour context gives (nC, neighbouring motion, intra mode prediction,
    /// intra availability) the old way too, by <see cref="Neighbor"/>, and throw where they differ.
    /// </summary>
    public bool VerifyNeighbourContext { get; init; }

    /// <summary>Whether the reads below take the context: frames, not MBAFF frames or field pictures.</summary>
    private bool UseContext => !_pairSlots;

    /// <summary>The grid index of the 4x4 block at (<paramref name="bx"/>, <paramref name="by"/>), -1 .. 4 and -1 .. 3.</summary>
    private static int Cell(int bx, int by) => ((by + 1) * GridStride) + bx + 1;

    /// <summary>The 4x4 block column or row of a luma offset of 6.4.12 (-1 for the neighbour left of or above the macroblock).</summary>
    private static int BlockOf(int sample) => sample < 0 ? -1 : sample >> 2;

    /// <summary>Finds A, B, C and D once for a macroblock (6.4.11.1); forgets the grids of the one before.</summary>
    private void EnsureContext(int mbAddr)
    {
        if (_contextMb == mbAddr)
        {
            return;
        }

        _contextMb = mbAddr;
        _contextFilled = 0;
        int slice = _sliceOfMb[mbAddr];
        int Available(int n) => n >= 0 && _sliceOfMb[n] == slice ? n : -1;
        _contextA = Available(_neighborMbs[mbAddr * 4]);
        _contextB = Available(_neighborMbs[(mbAddr * 4) + 1]);
        _contextC = Available(_neighborMbs[(mbAddr * 4) + 2]);
        _contextD = Available(_neighborMbs[(mbAddr * 4) + 3]);
    }

    /// <summary>Called by <see cref="Begin"/>: a macroblock address of the next picture is a new macroblock.</summary>
    private void ResetContext() => _contextMb = -1;

    // ---- Motion (8.4.1.3) ----

    /// <summary>The motion grid of <paramref name="list"/> for the current macroblock, filled on first use.</summary>
    private void FillMotion(int list, int mbAddr)
    {
        EnsureContext(mbAddr);
        int bit = MotionFilled << list;
        if ((_contextFilled & bit) != 0)
        {
            return;
        }

        _contextFilled |= bit;
        var mvX = _gridMvX[list];
        var mvY = _gridMvY[list];
        var refIdx = _gridRefIdx[list];
        void Copy(int cell, int n, int block)
        {
            if (n < 0)
            {
                mvX[cell] = 0;
                mvY[cell] = 0;
                refIdx[cell] = NoMotion;
                return;
            }

            mvX[cell] = _mvX[list][(n * 16) + block];
            mvY[cell] = _mvY[list][(n * 16) + block];
            refIdx[cell] = _refIdx[list][(n * 4) + ((block >> 3) * 2) + ((block & 3) >> 1)];
        }

        // The macroblock itself as stored so far (direct sub-macroblocks are stored before other partitions read).
        for (int by = 0; by < 4; by++)
        {
            for (int bx = 0; bx < 4; bx++)
            {
                Copy(Cell(bx, by), mbAddr, (by * 4) + bx);
            }

            Copy(Cell(4, by), -1, 0);
            Copy(Cell(-1, by), _contextA, (by * 4) + 3);
        }

        for (int bx = 0; bx < 4; bx++)
        {
            Copy(Cell(bx, -1), _contextB, 12 + bx);
        }

        Copy(Cell(4, -1), _contextC, 12);
        Copy(Cell(-1, -1), _contextD, 15);
    }

    /// <summary>
    /// <see cref="MotionNeighbor"/> from the context. Inside the macroblock a block is available only before
    /// <paramref name="currentBlock"/> in decoding order, whatever is stored there (6.4.11.7: B_Direct_8x8
    /// sub-macroblocks are stored before the partitions that precede them are decoded).
    /// </summary>
    private Motion ContextMotion(int list, int mbAddr, int x, int y, int currentBlock)
    {
        FillMotion(list, mbAddr);
        int bx = BlockOf(x);
        int by = BlockOf(y);
        if (bx is >= 0 and < 4 && by >= 0 && BlockIndex(bx, by) >= currentBlock)
        {
            return new Motion(false, -1, 0, 0);
        }

        int cell = Cell(bx, by);
        int refIdx = _gridRefIdx[list][cell];
        return refIdx == NoMotion ? new Motion(false, -1, 0, 0) : new Motion(true, refIdx, _gridMvX[list][cell], _gridMvY[list][cell]);
    }

    /// <summary>Keeps the motion grid of the current macroblock up to date with what <see cref="StoreMotion"/> stores.</summary>
    private void StoreContextMotion(int list, int mbAddr, int x, int y, int w, int h, int refIdx, int mvX, int mvY)
    {
        if (_contextMb != mbAddr || (_contextFilled & (MotionFilled << list)) == 0)
        {
            return;
        }

        for (int by = y >> 2; by < (y + h) >> 2; by++)
        {
            for (int bx = x >> 2; bx < (x + w) >> 2; bx++)
            {
                int cell = Cell(bx, by);
                _gridMvX[list][cell] = (short)mvX;
                _gridMvY[list][cell] = (short)mvY;
                _gridRefIdx[list][cell] = (sbyte)refIdx;
            }
        }
    }

    // ---- Coefficient counts, for CAVLC's nC (9.2.1) ----

    /// <summary>The luma-shaped count grid of <paramref name="component"/> (luma, or 4:4:4's Cb or Cr), filled on first use.</summary>
    private sbyte[] LumaCounts(int mbAddr, int component)
    {
        EnsureContext(mbAddr);
        var grid = _gridCounts[component];
        int bit = LumaCountsFilled << component;
        if ((_contextFilled & bit) != 0)
        {
            return grid;
        }

        _contextFilled |= bit;
        var counts = _totalCoeff[component];
        for (int by = 0; by < 4; by++)
        {
            for (int bx = 0; bx < 4; bx++)
            {
                grid[Cell(bx, by)] = (sbyte)counts[(mbAddr * 16) + (by * 4) + bx];
            }

            grid[Cell(-1, by)] = _contextA < 0 ? NoBlock : (sbyte)counts[(_contextA * 16) + (by * 4) + 3];
        }

        for (int bx = 0; bx < 4; bx++)
        {
            grid[Cell(bx, -1)] = _contextB < 0 ? NoBlock : (sbyte)counts[(_contextB * 16) + 12 + bx];
        }

        return grid;
    }

    /// <summary>
    /// The chroma count grid of component <paramref name="c"/> (0 Cb, 1 Cr), 4:2:0 and 4:2:2: two blocks across, two or
    /// four down, in the same 6 x 5 layout (columns 2 to 4 unused), filled on first use.
    /// </summary>
    private Span<sbyte> ChromaCounts(int mbAddr, int c)
    {
        EnsureContext(mbAddr);
        var grid = _gridChromaCounts.AsSpan(c * GridCells, GridCells);
        int bit = ChromaCountsFilled << c;
        if ((_contextFilled & bit) != 0)
        {
            return grid;
        }

        _contextFilled |= bit;
        int rows = _chromaBlocks / 2;
        for (int by = 0; by < rows; by++)
        {
            for (int bx = 0; bx < 2; bx++)
            {
                grid[Cell(bx, by)] = (sbyte)_totalCoeffChroma[(mbAddr * 16) + (c * 8) + (by * 2) + bx];
            }

            grid[Cell(-1, by)] = _contextA < 0 ? NoBlock : (sbyte)_totalCoeffChroma[(_contextA * 16) + (c * 8) + (by * 2) + 1];
        }

        for (int bx = 0; bx < 2; bx++)
        {
            grid[Cell(bx, -1)] = _contextB < 0 ? NoBlock : (sbyte)_totalCoeffChroma[(_contextB * 16) + (c * 8) + ((rows - 1) * 2) + bx];
        }

        return grid;
    }

    /// <summary>Keeps a luma-shaped count grid up to date with a 4x4 block's TotalCoeff, once it is filled.</summary>
    private void StoreContextCount(int mbAddr, int component, int x, int y, int count)
    {
        if (_contextMb == mbAddr && (_contextFilled & (LumaCountsFilled << component)) != 0)
        {
            _gridCounts[component][Cell(x, y)] = (sbyte)count;
        }
    }

    /// <summary>The same for a chroma AC block.</summary>
    private void StoreContextChromaCount(int mbAddr, int c, int x, int y, int count)
    {
        if (_contextMb == mbAddr && (_contextFilled & (ChromaCountsFilled << c)) != 0)
        {
            _gridChromaCounts[(c * GridCells) + Cell(x, y)] = (sbyte)count;
        }
    }

    /// <summary><see cref="LumaNc"/> from the context.</summary>
    private int ContextLumaNc(int mbAddr, int x, int y, int component)
    {
        var grid = LumaCounts(mbAddr, component);
        return CombineNc(grid[Cell(x - 1, y)], grid[Cell(x, y - 1)]);
    }

    /// <summary><see cref="ChromaNc"/> from the context.</summary>
    private int ContextChromaNc(int mbAddr, int c, int x, int y)
    {
        var grid = ChromaCounts(mbAddr, c);
        return CombineNc(grid[Cell(x - 1, y)], grid[Cell(x, y - 1)]);
    }

    // ---- Intra prediction (8.3.1.1, 8.3.1.2) ----

    /// <summary>
    /// The intra mode grid: each neighbouring 4x4 block's Intra4x4PredMode (2 unless its macroblock is Intra_4x4),
    /// <see cref="NoBlock"/> where it is not available for intra prediction (constrained_intra_pred_flag makes inter
    /// macroblocks so), and the current macroblock's modes as decoded so far.
    /// </summary>
    private sbyte[] IntraModes(SliceState slice, int mbAddr)
    {
        EnsureContext(mbAddr);
        var grid = _gridIntraModes;
        if ((_contextFilled & IntraModesFilled) != 0)
        {
            return grid;
        }

        _contextFilled |= IntraModesFilled;
        bool constrained = slice.Pps.ConstrainedIntraPred;
        sbyte Mode(int n, int block) =>
            n < 0 || (constrained && IsInter(n)) ? NoBlock : _kind[n] == MbKind.Intra4x4 ? _intra4x4Mode[(n * 16) + block] : (sbyte)2;

        for (int by = 0; by < 4; by++)
        {
            for (int bx = 0; bx < 4; bx++)
            {
                grid[Cell(bx, by)] = Mode(mbAddr, (by * 4) + bx);
            }

            grid[Cell(-1, by)] = Mode(_contextA, (by * 4) + 3);
        }

        for (int bx = 0; bx < 4; bx++)
        {
            grid[Cell(bx, -1)] = Mode(_contextB, 12 + bx);
        }

        return grid;
    }

    /// <summary>Keeps the intra mode grid up to date with a decoded Intra4x4PredMode (or an 8x8 one, for its four 4x4 blocks).</summary>
    private void StoreContextIntraMode(int mbAddr, int x, int y, int mode, bool eightByEight)
    {
        if (_contextMb != mbAddr || (_contextFilled & IntraModesFilled) == 0)
        {
            return;
        }

        var grid = _gridIntraModes;
        grid[Cell(x, y)] = (sbyte)mode;
        if (eightByEight)
        {
            grid[Cell(x + 1, y)] = (sbyte)mode;
            grid[Cell(x, y + 1)] = (sbyte)mode;
            grid[Cell(x + 1, y + 1)] = (sbyte)mode;
        }
    }

    /// <summary><see cref="PredictIntra4x4Mode"/> from the context: 2 if A or B is not available, else the lesser mode.</summary>
    private int ContextIntra4x4Mode(SliceState slice, int mbAddr, int x, int y)
    {
        var grid = IntraModes(slice, mbAddr);
        int a = grid[Cell(x - 1, y)];
        int b = grid[Cell(x, y - 1)];
        return a < 0 || b < 0 ? 2 : Math.Min(a, b);
    }

    /// <summary><see cref="IntraMbNeighbors"/> from the context.</summary>
    private (bool A, bool B, bool C, bool D) ContextIntraMbNeighbors(SliceState slice, int mbAddr)
    {
        EnsureContext(mbAddr);
        bool Usable(int n) => n >= 0 && !(slice.Pps.ConstrainedIntraPred && IsInter(n));
        return (Usable(_contextA), Usable(_contextB), Usable(_contextC), Usable(_contextD));
    }

    private static T Verified<T>(T context, T reference, string what)
        where T : IEquatable<T> =>
        context.Equals(reference) ? context : throw new InvalidOperationException($"Neighbour context: {what} {context}, the lookup gives {reference}.");
}

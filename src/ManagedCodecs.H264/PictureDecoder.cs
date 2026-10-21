using System.Runtime.CompilerServices;

namespace ManagedCodecs.H264;

internal enum MbKind : byte
{
    NotDecoded,
    Intra4x4,
    Intra16x16,
    IPcm,
    Inter,
    PSkip,
}

/// <summary>Per-slice values that later stages (deblocking) need for every macroblock.</summary>
internal sealed record SliceInfo(int DisableDeblockingFilterIdc, int AlphaOffset, int BetaOffset, int CbQpOffset, int CrQpOffset);

/// <summary>
/// Decodes the slices of one picture into a <see cref="Picture"/> and keeps the per-macroblock
/// state that neighbouring macroblocks and the deblocking filter read. One instance serves every
/// picture of a sequence: <see cref="Begin"/> resets it for the next.
/// </summary>
/// <remarks>
/// Block arrays use raster order inside the macroblock: luma 4x4 block (x, y) is y * 4 + x,
/// chroma 4x4 block (x, y) of component c (0 = Cb, 1 = Cr) is c * 4 + y * 2 + x.
/// </remarks>
internal sealed partial class PictureDecoder
{
    private readonly int _widthInMbs;
    private readonly int _mbCount;

    // Per macroblock.
    private readonly int[] _sliceOfMb;
    private readonly MbKind[] _kind;
    private readonly int[] _qp;
    private readonly bool[] _transform8x8;

    // Per 4x4 block.
    private readonly byte[] _totalCoeffLuma;
    private readonly byte[] _totalCoeffChroma;
    private readonly sbyte[] _intra4x4Mode;

    private readonly List<SliceInfo> _slices = [];

    // Coefficients of the macroblock being decoded, reused from one macroblock to the next.
    private readonly Residual _residual = new();

    // The CABAC engine and contexts, reinitialized for every CABAC slice.
    private readonly Cabac _cabac = new();

    public PictureDecoder(SequenceParameterSet sps)
    {
        Sps = sps;
        _widthInMbs = sps.WidthInMbs;
        _mbCount = sps.WidthInMbs * sps.HeightInMbs;

        _sliceOfMb = new int[_mbCount];
        Array.Fill(_sliceOfMb, -1);
        _kind = new MbKind[_mbCount];
        _qp = new int[_mbCount];
        _transform8x8 = new bool[_mbCount];
        _totalCoeffLuma = new byte[_mbCount * 16];
        _totalCoeffChroma = new byte[_mbCount * 8];
        _intra4x4Mode = new sbyte[_mbCount * 16];

        InitInter();
        InitCabacState();
    }

    public SequenceParameterSet Sps { get; }

    public Picture Picture { get; private set; } = null!;

    public int DecodedMbCount { get; private set; }

    /// <summary>Starts decoding into <paramref name="picture"/>, forgetting the previous picture's state.</summary>
    public void Begin(Picture picture)
    {
        Picture = picture;
        DecodedMbCount = 0;
        _slices.Clear();
        Array.Fill(_sliceOfMb, -1);
        Array.Clear(_kind);
        Array.Clear(_qp);
        Array.Clear(_transform8x8);
        Array.Clear(_totalCoeffLuma);
        Array.Clear(_totalCoeffChroma);
        Array.Clear(_intra4x4Mode);
        ResetInter(picture);
        ResetCabacState();
    }

    public bool IsComplete => DecodedMbCount == _mbCount;

    /// <summary>slice_data (7.3.4).</summary>
    public void DecodeSlice(SliceHeader header, PictureParameterSet pps, BitReader r, IReadOnlyList<Picture> refList0, IReadOnlyList<Picture> refList1)
    {
        int sliceNum = _slices.Count;
        _slices.Add(new SliceInfo(header.DisableDeblockingFilterIdc, header.SliceAlphaC0Offset, header.SliceBetaOffset, pps.ChromaQpIndexOffset, pps.SecondChromaQpIndexOffset));

        var slice = new SliceState(header, pps, sliceNum, refList0, refList1)
        {
            Qp = pps.PicInitQp + header.SliceQpDelta,
            Cabac = pps.EntropyCodingModeCabac ? _cabac : null,
        };

        if (slice.Qp is < 0 or > 51)
        {
            throw new InvalidDataException($"Slice QP {slice.Qp} is out of range.");
        }

        if (slice.Cabac is not null)
        {
            DecodeSliceDataCabac(slice, r);
            return;
        }

        int mbAddr = header.FirstMbInSlice;
        bool moreData = true;
        do
        {
            if (header.Type != SliceType.I)
            {
                int skipRun = (int)r.ReadUe();
                for (int i = 0; i < skipRun; i++)
                {
                    DecodeSkip(slice, CheckAddress(mbAddr));
                    mbAddr++;
                }

                if (skipRun > 0)
                {
                    moreData = r.MoreRbspData();
                }
            }

            if (moreData)
            {
                DecodeMacroblock(slice, r, CheckAddress(mbAddr));
                mbAddr++;
                moreData = r.MoreRbspData();
            }
        }
        while (moreData);
    }

    private int CheckAddress(int mbAddr)
    {
        if (mbAddr >= _mbCount)
        {
            throw new InvalidDataException("Slice runs past the end of the picture.");
        }

        if (_sliceOfMb[mbAddr] >= 0)
        {
            throw new InvalidDataException($"Macroblock {mbAddr} is decoded twice.");
        }

        return mbAddr;
    }

    /// <summary>State that lives for one slice.</summary>
    private sealed class SliceState(SliceHeader header, PictureParameterSet pps, int number, IReadOnlyList<Picture> refList0, IReadOnlyList<Picture> refList1)
    {
        public SliceHeader Header { get; } = header;

        public PictureParameterSet Pps { get; } = pps;

        public int Number { get; } = number;

        public IReadOnlyList<Picture> RefList0 { get; } = refList0;

        public IReadOnlyList<Picture> RefList1 { get; } = refList1;

        /// <summary>QP_Y of the previous macroblock in the slice (QP_Y,PRED).</summary>
        public int Qp { get; set; }

        public IReadOnlyList<Picture> RefList(int list) => list == 0 ? RefList0 : RefList1;

        /// <summary>The CABAC engine for a CABAC slice, null for CAVLC.</summary>
        public Cabac? Cabac { get; init; }

        /// <summary>Whether the previous macroblock in the slice had a nonzero mb_qp_delta (its CABAC context).</summary>
        public bool LastQpDeltaNonZero { get; set; }
    }

    // ---- Neighbours (6.4.12) ----

    /// <summary>
    /// The macroblock covering luma location (<paramref name="x"/>, <paramref name="y"/>)
    /// relative to the current macroblock's top-left, or -1 when it is not available
    /// (outside the picture, in another slice, or not decoded yet). Locations inside the
    /// current macroblock return the current address; callers check decoding order there.
    /// </summary>
    private int NeighborMb(int mbAddr, int x, int y, int size = 16)
    {
        if (y >= size || (x >= size && y >= 0))
        {
            return -1;
        }

        int mbX = mbAddr % _widthInMbs;
        int mbY = mbAddr / _widthInMbs;
        int nx = mbX + (x < 0 ? -1 : x >= size ? 1 : 0);
        int ny = mbY + (y < 0 ? -1 : 0);
        if (nx < 0 || nx >= _widthInMbs || ny < 0)
        {
            return -1;
        }

        int n = (ny * _widthInMbs) + nx;
        if (n == mbAddr)
        {
            return n;
        }

        return _sliceOfMb[n] == _sliceOfMb[mbAddr] ? n : -1;
    }

    /// <summary>Decoding order of the luma 4x4 block at raster position (x, y) inside a macroblock (6.4.3).</summary>
    private static int BlockIndex(int x, int y) => ((y >> 1) * 8) + ((x >> 1) * 4) + ((y & 1) * 2) + (x & 1);

    /// <summary>Raster position of the luma 4x4 block with decoding index <paramref name="index"/> (6.4.3).</summary>
    private static (int X, int Y) BlockPosition(int index) =>
        ((((index >> 2) & 1) * 2) + (index & 1), (((index >> 3) & 1) * 2) + ((index >> 1) & 1));

    /// <summary>nC for a luma 4x4 block (9.2.1).</summary>
    private int LumaNc(int mbAddr, int x, int y) =>
        CombineNc(
            NeighborTotalCoeff(mbAddr, (x * 4) - 1, y * 4, _totalCoeffLuma, 16, 0),
            NeighborTotalCoeff(mbAddr, x * 4, (y * 4) - 1, _totalCoeffLuma, 16, 0));

    /// <summary>nC for a chroma AC 4x4 block of component <paramref name="c"/> (9.2.1).</summary>
    private int ChromaNc(int mbAddr, int c, int x, int y) =>
        CombineNc(
            NeighborTotalCoeff(mbAddr, (x * 4) - 1, y * 4, _totalCoeffChroma, 8, c),
            NeighborTotalCoeff(mbAddr, x * 4, (y * 4) - 1, _totalCoeffChroma, 8, c));

    private static int CombineNc(int a, int b) => (a, b) switch
    {
        ( >= 0, >= 0) => (a + b + 1) >> 1,
        ( >= 0, _) => a,
        (_, >= 0) => b,
        _ => 0,
    };

    /// <summary>TotalCoeff of the 4x4 block covering a location, or -1 when unavailable.</summary>
    private int NeighborTotalCoeff(int mbAddr, int x, int y, byte[] counts, int size, int component)
    {
        int n = NeighborMb(mbAddr, x, y, size);
        if (n < 0)
        {
            return -1;
        }

        int bx = ((x + size) % size) / 4;
        int by = ((y + size) % size) / 4;
        return size == 16
            ? counts[(n * 16) + (by * 4) + bx]
            : counts[(n * 8) + (component * 4) + (by * 2) + bx];
    }

    // ---- Macroblock layer (7.3.5) ----
    private void DecodeMacroblock(SliceState slice, BitReader r, int mbAddr)
    {
        var type = slice.Header.Type;
        int mbType = slice.Cabac is not { } c ? (int)r.ReadUe()
            : type == SliceType.P ? CabacPMbType(c, mbAddr)
            : type == SliceType.B ? CabacBMbType(c, mbAddr)
            : CabacIntraMbType(c, mbAddr);

        // Inter types come first in P (5, Table 7-13) and B slices (23, Table 7-14), then the intra ones.
        int interTypes = type switch { SliceType.P => 5, SliceType.B => 23, _ => 0 };
        if (mbType < interTypes)
        {
            DecodeInterMacroblock(slice, r, mbAddr, mbType);
            return;
        }

        mbType -= interTypes;

        DecodeIntraMacroblock(slice, r, mbAddr, mbType);
    }

    private void DecodeIntraMacroblock(SliceState slice, BitReader r, int mbAddr, int mbType)
    {
        if (mbType > 25)
        {
            throw new InvalidDataException($"Invalid intra mb_type {mbType}.");
        }

        _sliceOfMb[mbAddr] = slice.Number;
        DecodedMbCount++;
        _dcCoded[mbAddr] = 0;
        _transform8x8[mbAddr] = false;

        if (mbType == 25)
        {
            DecodePcm(slice, r, mbAddr);
            return;
        }

        bool is16x16 = mbType > 0;
        _kind[mbAddr] = is16x16 ? MbKind.Intra16x16 : MbKind.Intra4x4;
        Span<sbyte> modes = _intra4x4Mode.AsSpan(mbAddr * 16, 16);

        int predMode16x16 = 0;
        int cbpLuma;
        int cbpChroma;
        if (is16x16)
        {
            // Table 7-11: mb_type 1..24 encode the prediction mode and both CBP parts.
            int t = mbType - 1;
            predMode16x16 = t % 4;
            cbpChroma = (t / 4) % 3;
            cbpLuma = t >= 12 ? 15 : 0;
            modes.Fill(-1);
        }
        else
        {
            bool transform8x8 = slice.Pps.Transform8x8Mode && ReadTransformSize8x8Flag(slice, r, mbAddr);
            _transform8x8[mbAddr] = transform8x8;

            // Intra_8x8 has one mode per 8x8 block, stored in its four 4x4 blocks so that the
            // mode prediction of either block size reads it as 8.3.1.1 and 8.3.2.1 say.
            int step = transform8x8 ? 4 : 1;
            for (int idx = 0; idx < 16; idx += step)
            {
                var (x, y) = BlockPosition(idx);
                int mode = PredictIntra4x4Mode(slice, mbAddr, x, y);

                // prev_intra4x4_pred_mode_flag / prev_intra8x8_pred_mode_flag, else the remaining mode.
                int remaining = slice.Cabac is { } c ? CabacIntra4x4Mode(c) : r.ReadFlag() ? -1 : (int)r.ReadBits(3);
                if (remaining >= 0)
                {
                    mode = remaining < mode ? remaining : remaining + 1;
                }

                modes[(y * 4) + x] = (sbyte)mode;
                if (transform8x8)
                {
                    modes[(y * 4) + x + 1] = (sbyte)mode;
                    modes[((y + 1) * 4) + x] = (sbyte)mode;
                    modes[((y + 1) * 4) + x + 1] = (sbyte)mode;
                }
            }

            cbpLuma = 0;
            cbpChroma = 0;
        }

        int chromaPredMode = slice.Cabac is { } cabac ? CabacChromaPredMode(cabac, mbAddr) : r.ReadUe(3, "intra_chroma_pred_mode");
        _chromaPredMode[mbAddr] = (byte)chromaPredMode;

        if (!is16x16)
        {
            int cbp = slice.Cabac is { } cabacCbp ? CabacCbp(cabacCbp, mbAddr) : CavlcTables.CodedBlockPatternIntra[r.ReadUe(47, "coded_block_pattern")];
            cbpLuma = cbp & 15;
            cbpChroma = cbp >> 4;
        }

        _cbp[mbAddr] = (byte)(cbpLuma | (cbpChroma << 4));

        var residual = _residual;
        if (cbpLuma > 0 || cbpChroma > 0 || is16x16)
        {
            UpdateQp(slice, r);
            ReadResidual(slice, r, mbAddr, residual, is16x16, cbpLuma, cbpChroma);
        }
        else
        {
            ClearCounts(mbAddr);
            slice.LastQpDeltaNonZero = false;
        }

        _qp[mbAddr] = slice.Qp;

        var neighbors = IntraMbNeighbors(slice, mbAddr);
        ReconstructIntraLuma(slice, mbAddr, residual, is16x16, predMode16x16, cbpLuma, neighbors);
        ReconstructChroma(mbAddr, residual, cbpChroma, slice.Pps, intraMode: chromaPredMode, neighbors);
    }

    /// <summary>transform_size_8x8_flag: one bit, or with CABAC ctxIdx 399 + the flags of macroblocks A and B.</summary>
    private bool ReadTransformSize8x8Flag(SliceState slice, BitReader r, int mbAddr)
    {
        if (slice.Cabac is not { } c)
        {
            return r.ReadFlag();
        }

        int a = NeighborMb(mbAddr, -1, 0);
        int b = NeighborMb(mbAddr, 0, -1);
        int inc = (a >= 0 && _transform8x8[a] ? 1 : 0) + (b >= 0 && _transform8x8[b] ? 1 : 0);
        return c.Decision(399 + inc) == 1;
    }

    private void DecodePcm(SliceState slice, BitReader r, int mbAddr)
    {
        _kind[mbAddr] = MbKind.IPcm;
        _transform8x8[mbAddr] = false;
        _qp[mbAddr] = 0; // what the deblocking filter uses for I_PCM; QP_Y,PRED carries on unchanged
        _intra4x4Mode.AsSpan(mbAddr * 16, 16).Fill(-1);
        _totalCoeffLuma.AsSpan(mbAddr * 16, 16).Fill(16);
        _totalCoeffChroma.AsSpan(mbAddr * 8, 8).Fill(16);

        // For the CABAC contexts of later macroblocks I_PCM counts as fully coded.
        _cbp[mbAddr] = 15 | (2 << 4);
        _dcCoded[mbAddr] = 7;
        _chromaPredMode[mbAddr] = 0;
        slice.LastQpDeltaNonZero = false;

        while (!r.IsByteAligned)
        {
            r.Skip(1); // pcm_alignment_zero_bit
        }

        var (lumaOrigin, chromaOrigin) = Origins(mbAddr);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                Picture.Y[lumaOrigin + (y * Picture.LumaStride) + x] = (byte)r.ReadBits(8);
            }
        }

        foreach (var plane in new[] { Picture.Cb, Picture.Cr })
        {
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    plane[chromaOrigin + (y * Picture.ChromaStride) + x] = (byte)r.ReadBits(8);
                }
            }
        }

        // After the samples the arithmetic decoder starts afresh (9.3.1.2).
        slice.Cabac?.InitEngine(r);
    }

    private void UpdateQp(SliceState slice, BitReader r)
    {
        int delta = slice.Cabac is { } c ? CabacQpDelta(slice, c) : r.ReadSe();
        slice.LastQpDeltaNonZero = delta != 0;
        if (delta is < -26 or > 25)
        {
            throw new InvalidDataException($"mb_qp_delta {delta} is out of range.");
        }

        slice.Qp = (slice.Qp + delta + 52) % 52;
    }

    private void ClearCounts(int mbAddr)
    {
        _totalCoeffLuma.AsSpan(mbAddr * 16, 16).Clear();
        _totalCoeffChroma.AsSpan(mbAddr * 8, 8).Clear();
    }

    /// <summary>Intra4x4PredMode prediction (8.3.1.1).</summary>
    private int PredictIntra4x4Mode(SliceState slice, int mbAddr, int x, int y)
    {
        int a = NeighborMb(mbAddr, (x * 4) - 1, y * 4);
        int b = NeighborMb(mbAddr, x * 4, (y * 4) - 1);
        bool constrained = slice.Pps.ConstrainedIntraPred;
        if (a < 0 || b < 0 || (constrained && (IsInter(a) || IsInter(b))))
        {
            return 2;
        }

        int modeA = NeighborIntraMode(a, x == 0 ? 3 : x - 1, y);
        int modeB = NeighborIntraMode(b, x, y == 0 ? 3 : y - 1);
        return Math.Min(modeA, modeB);
    }

    /// <summary>Intra4x4PredMode of a neighbouring block; 2 (DC) unless its macroblock is Intra_4x4.</summary>
    private int NeighborIntraMode(int mbAddr, int x, int y) =>
        _kind[mbAddr] == MbKind.Intra4x4 ? _intra4x4Mode[(mbAddr * 16) + (y * 4) + x] : 2;

    private bool IsInter(int mbAddr) => _kind[mbAddr] is MbKind.Inter or MbKind.PSkip;

    /// <summary>Availability of the A, B, C and D macroblocks for intra prediction of the current one.</summary>
    private (bool A, bool B, bool C, bool D) IntraMbNeighbors(SliceState slice, int mbAddr)
    {
        bool Usable(int n) => n >= 0 && !(slice.Pps.ConstrainedIntraPred && IsInter(n));
        return (Usable(NeighborMb(mbAddr, -1, 0)), Usable(NeighborMb(mbAddr, 0, -1)), Usable(NeighborMb(mbAddr, 16, -1)), Usable(NeighborMb(mbAddr, -1, -1)));
    }

    private (int Luma, int Chroma) Origins(int mbAddr)
    {
        int mbX = mbAddr % _widthInMbs;
        int mbY = mbAddr / _widthInMbs;
        return ((mbY * 16 * Picture.LumaStride) + (mbX * 16), (mbY * 8 * Picture.ChromaStride) + (mbX * 8));
    }

    // ---- Residual (7.3.5.3) ----

    /// <summary>Parsed and inverse-scanned coefficient levels of one macroblock.</summary>
    private sealed class Residual
    {
        /// <summary>Luma levels per raster 4x4 block, row-major inside the block.</summary>
        public int[][] Luma { get; } = [.. Enumerable.Range(0, 16).Select(_ => new int[16])];

        /// <summary>Luma levels per 8x8 block (with transform_size_8x8_flag), row-major inside the block.</summary>
        public int[][] Luma8x8 { get; } = [.. Enumerable.Range(0, 4).Select(_ => new int[64])];

        /// <summary>Intra16x16 DC levels, row-major by block position.</summary>
        public int[] LumaDc { get; } = new int[16];

        public int[][] ChromaDc { get; } = [new int[4], new int[4]];

        /// <summary>Chroma levels per component and raster 4x4 block.</summary>
        public int[][] ChromaAc { get; } = [.. Enumerable.Range(0, 8).Select(_ => new int[16])];
    }

    /// <summary>One residual block by either entropy coder; levels by scan position, returns the number of nonzero ones.</summary>
    private int ReadBlock(SliceState slice, BitReader r, int mbAddr, BlockCategory category, int x, int y, int component, int maxNumCoeff, Span<int> levels)
    {
        if (slice.Cabac is { } c)
        {
            int inc = category == BlockCategory.Luma8x8 ? 0 : CodedBlockFlagInc(mbAddr, category, x, y, component);
            return CabacResidualBlock(c, category, inc, maxNumCoeff, levels);
        }

        int nC = category switch
        {
            BlockCategory.ChromaDc => Cavlc.ChromaDcNc,
            BlockCategory.ChromaAc => ChromaNc(mbAddr, component, x, y),
            _ => LumaNc(mbAddr, x, y),
        };
        return Cavlc.ReadResidualBlock(r, nC, maxNumCoeff, levels);
    }

    [SkipLocalsInit]
    private void ReadResidual(SliceState slice, BitReader r, int mbAddr, Residual residual, bool is16x16, int cbpLuma, int cbpChroma)
    {
        Span<int> levels = stackalloc int[64];
        var zigZag = Transform.ZigZag4x4;
        Span<byte> lumaCounts = _totalCoeffLuma.AsSpan(mbAddr * 16, 16);
        Span<byte> chromaCounts = _totalCoeffChroma.AsSpan(mbAddr * 8, 8);
        lumaCounts.Clear();
        chromaCounts.Clear();

        if (is16x16)
        {
            if (ReadBlock(slice, r, mbAddr, BlockCategory.LumaDc, 0, 0, 0, 16, levels) != 0)
            {
                _dcCoded[mbAddr] |= 1;
            }

            for (int k = 0; k < 16; k++)
            {
                residual.LumaDc[zigZag[k]] = levels[k];
            }
        }

        if (_transform8x8[mbAddr])
        {
            ReadResidual8x8(slice, r, mbAddr, residual, cbpLuma, levels);
        }
        else
        {
            for (int idx = 0; idx < 16; idx++)
            {
                if ((cbpLuma & (1 << (idx >> 2))) == 0)
                {
                    continue;
                }

                var (x, y) = BlockPosition(idx);
                var block = residual.Luma[(y * 4) + x];
                int first = is16x16 ? 1 : 0;
                int count = ReadBlock(slice, r, mbAddr, is16x16 ? BlockCategory.LumaAc : BlockCategory.Luma4x4, x, y, 0, 16 - first, levels);
                lumaCounts[(y * 4) + x] = (byte)count;
                if (count == 0)
                {
                    continue; // reconstruction reads the counts and leaves this block alone
                }

                for (int k = first; k < 16; k++)
                {
                    block[zigZag[k]] = levels[k - first];
                }
            }
        }

        if (cbpChroma == 0)
        {
            return;
        }

        for (int c = 0; c < 2; c++)
        {
            if (ReadBlock(slice, r, mbAddr, BlockCategory.ChromaDc, 0, 0, c, 4, levels) != 0)
            {
                _dcCoded[mbAddr] |= (byte)(2 << c);
            }

            levels[..4].CopyTo(residual.ChromaDc[c]);
        }

        if (cbpChroma < 2)
        {
            return;
        }

        for (int c = 0; c < 2; c++)
        {
            for (int b = 0; b < 4; b++)
            {
                int x = b & 1;
                int y = b >> 1;
                var block = residual.ChromaAc[(c * 4) + b];
                int count = ReadBlock(slice, r, mbAddr, BlockCategory.ChromaAc, x, y, c, 15, levels);
                chromaCounts[(c * 4) + b] = (byte)count;
                if (count == 0)
                {
                    continue;
                }

                for (int k = 0; k < 15; k++)
                {
                    block[zigZag[k + 1]] = levels[k];
                }
            }
        }
    }

    /// <summary>
    /// The luma levels of a macroblock with 8x8 transforms: with CABAC one block of 64 per coded
    /// 8x8 block, whose count stands for all four of its 4x4 blocks; with CAVLC four interleaved
    /// 4x4 blocks (7.3.5.3), each keeping its own TotalCoeff for the nC of later blocks.
    /// </summary>
    private void ReadResidual8x8(SliceState slice, BitReader r, int mbAddr, Residual residual, int cbpLuma, Span<int> levels)
    {
        var zigZag = Transform.ZigZag8x8;
        Span<byte> counts = _totalCoeffLuma.AsSpan(mbAddr * 16, 16);
        for (int b8 = 0; b8 < 4; b8++)
        {
            if ((cbpLuma & (1 << b8)) == 0)
            {
                continue;
            }

            var block = residual.Luma8x8[b8];
            int x8 = (b8 & 1) * 2;
            int y8 = (b8 >> 1) * 2;
            if (slice.Cabac is not null)
            {
                byte count = (byte)ReadBlock(slice, r, mbAddr, BlockCategory.Luma8x8, x8, y8, 0, 64, levels);
                counts[(y8 * 4) + x8] = count;
                counts[(y8 * 4) + x8 + 1] = count;
                counts[((y8 + 1) * 4) + x8] = count;
                counts[((y8 + 1) * 4) + x8 + 1] = count;
                for (int k = 0; k < 64; k++)
                {
                    block[zigZag[k]] = levels[k];
                }

                continue;
            }

            block.AsSpan().Clear();
            for (int i4 = 0; i4 < 4; i4++)
            {
                var (x, y) = BlockPosition((b8 * 4) + i4);
                int count = ReadBlock(slice, r, mbAddr, BlockCategory.Luma4x4, x, y, 0, 16, levels);
                counts[(y * 4) + x] = (byte)count;
                if (count == 0)
                {
                    continue;
                }

                for (int k = 0; k < 16; k++)
                {
                    block[zigZag[(4 * k) + i4]] = levels[k];
                }
            }
        }
    }

    /// <summary>Whether any of the four 4x4 blocks of 8x8 block <paramref name="b8"/> has coefficients.</summary>
    private bool Has8x8Coefficients(int mbAddr, int b8)
    {
        int i = (mbAddr * 16) + ((b8 >> 1) * 8) + ((b8 & 1) * 2);
        return (_totalCoeffLuma[i] | _totalCoeffLuma[i + 1] | _totalCoeffLuma[i + 4] | _totalCoeffLuma[i + 5]) != 0;
    }

    /// <summary>Adds the residual of each coded 8x8 block to the luma prediction.</summary>
    private void AddLuma8x8Residual(int mbAddr, Residual residual, int cbpLuma, int b8, int qp)
    {
        if ((cbpLuma & (1 << b8)) == 0 || !Has8x8Coefficients(mbAddr, b8))
        {
            return;
        }

        var (origin, _) = Origins(mbAddr);
        int stride = Picture.LumaStride;
        var block = residual.Luma8x8[b8];
        Transform.Dequantize8x8(block, qp);
        Transform.InverseTransform8x8Add(block, Picture.Y.AsSpan(origin + ((b8 >> 1) * 8 * stride) + ((b8 & 1) * 8)), stride);
    }

    // ---- Reconstruction ----
    private void ReconstructIntraLuma(SliceState slice, int mbAddr, Residual residual, bool is16x16, int predMode16x16, int cbpLuma, (bool A, bool B, bool C, bool D) mbs)
    {
        var (origin, _) = Origins(mbAddr);
        int stride = Picture.LumaStride;
        byte[] plane = Picture.Y;
        int qp = _qp[mbAddr];

        if (is16x16)
        {
            IntraPrediction.Predict16x16(predMode16x16, plane, origin, stride, new IntraNeighbors(mbs.A, mbs.B, mbs.C, mbs.D));
            Span<int> dc = stackalloc int[16];
            residual.LumaDc.CopyTo(dc);
            Transform.InverseLumaDc(dc, qp);

            for (int i = 0; i < 16; i++)
            {
                var dest = plane.AsSpan(origin + ((i >> 2) * 4 * stride) + ((i & 3) * 4));
                if (_totalCoeffLuma[(mbAddr * 16) + i] == 0)
                {
                    Transform.AddDc(dc[i], dest, stride);
                    continue;
                }

                var block = residual.Luma[i];
                block[0] = dc[i];
                Transform.Dequantize(block, qp, skipDc: true);
                Transform.InverseTransformAdd(block, dest, stride);
            }

            return;
        }

        if (_transform8x8[mbAddr])
        {
            for (int b8 = 0; b8 < 4; b8++)
            {
                // Within the macroblock: block 3 has no top-right samples, blocks 1 and 2 have the others.
                int x = (b8 & 1) * 2;
                int y = (b8 >> 1) * 2;
                var n = b8 switch
                {
                    0 => new IntraNeighbors(mbs.A, mbs.B, mbs.B, mbs.D),
                    1 => new IntraNeighbors(true, mbs.B, mbs.C, mbs.B),
                    2 => new IntraNeighbors(mbs.A, true, true, mbs.A),
                    _ => new IntraNeighbors(true, true, false, true),
                };
                int offset8 = origin + (y * 4 * stride) + (x * 4);
                IntraPrediction.Predict8x8(_intra4x4Mode[(mbAddr * 16) + (y * 4) + x], plane, offset8, stride, n);
                AddLuma8x8Residual(mbAddr, residual, cbpLuma, b8, qp);
            }

            return;
        }

        for (int idx = 0; idx < 16; idx++)
        {
            var (x, y) = BlockPosition(idx);
            int offset = origin + (y * 4 * stride) + (x * 4);

            bool left = x > 0 || mbs.A;
            bool top = y > 0 || mbs.B;
            bool topLeft = (x > 0, y > 0) switch
            {
                (true, true) => true,
                (false, true) => mbs.A,
                (true, false) => mbs.B,
                _ => mbs.D,
            };
            bool topRight = y == 0
                ? (x < 3 ? mbs.B : mbs.C)
                : x < 3 && BlockIndex(x + 1, y - 1) < idx;

            IntraPrediction.Predict4x4(_intra4x4Mode[(mbAddr * 16) + (y * 4) + x], plane, offset, stride, new IntraNeighbors(left, top, topRight, topLeft));

            if ((cbpLuma & (1 << (idx >> 2))) != 0 && _totalCoeffLuma[(mbAddr * 16) + (y * 4) + x] != 0)
            {
                var block = residual.Luma[(y * 4) + x];
                Transform.Dequantize(block, qp, skipDc: false);
                Transform.InverseTransformAdd(block, plane.AsSpan(offset), stride);
            }
        }
    }

    /// <summary>
    /// Chroma prediction (intra only; inter prediction is done before) and residual for both
    /// components. <paramref name="intraMode"/> is -1 for inter macroblocks.
    /// </summary>
    private void ReconstructChroma(int mbAddr, Residual residual, int cbpChroma, PictureParameterSet pps, int intraMode, (bool A, bool B, bool C, bool D) mbs)
    {
        var (_, origin) = Origins(mbAddr);
        int stride = Picture.ChromaStride;
        Span<int> dc = stackalloc int[4];

        for (int c = 0; c < 2; c++)
        {
            int qpc = ChromaQp(_qp[mbAddr], c == 0 ? pps.ChromaQpIndexOffset : pps.SecondChromaQpIndexOffset);
            byte[] plane = c == 0 ? Picture.Cb : Picture.Cr;
            if (intraMode >= 0)
            {
                IntraPrediction.PredictChroma(intraMode, plane, origin, stride, new IntraNeighbors(mbs.A, mbs.B, mbs.C, mbs.D));
            }

            if (cbpChroma == 0)
            {
                continue;
            }

            residual.ChromaDc[c].CopyTo(dc);
            Transform.InverseChromaDc(dc, qpc);

            for (int b = 0; b < 4; b++)
            {
                var dest = plane.AsSpan(origin + ((b >> 1) * 4 * stride) + ((b & 1) * 4));
                if (_totalCoeffChroma[(mbAddr * 8) + (c * 4) + b] == 0)
                {
                    Transform.AddDc(dc[b], dest, stride);
                    continue;
                }

                var block = residual.ChromaAc[(c * 4) + b];
                block[0] = dc[b];
                Transform.Dequantize(block, qpc, skipDc: true);
                Transform.InverseTransformAdd(block, dest, stride);
            }
        }
    }

    /// <summary>QP_C from QP_Y and the chroma offset (Table 8-15).</summary>
    public static int ChromaQp(int qpY, int offset)
    {
        int qpi = Math.Clamp(qpY + offset, 0, 51);
        return qpi < 30 ? qpi : ChromaQpTable[qpi - 30];
    }

    private static ReadOnlySpan<byte> ChromaQpTable => [29, 30, 31, 32, 32, 33, 34, 34, 35, 35, 36, 36, 37, 37, 37, 38, 38, 38, 39, 39, 39, 39];
}

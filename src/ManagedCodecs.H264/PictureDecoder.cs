using System.Numerics;
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

/// <summary>A <see cref="PictureDecoder{TPixel}"/> of either sample type, as <see cref="H264Decoder"/> holds it.</summary>
internal interface IPictureDecoder
{
    SequenceParameterSet Sps { get; }

    Picture Picture { get; }

    bool IsComplete { get; }

    void Begin(Picture picture, SequenceParameterSet sps, int fieldParity = -1, bool secondField = false);

    void DecodeSlice(SliceHeader header, PictureParameterSet pps, BitReader r, IReadOnlyList<Picture> refList0, IReadOnlyList<Picture> refList1, MarkSnapshot marks);

    /// <summary>For tests of frame threading: sleep up to this many milliseconds after each macroblock row, at random.</summary>
    int RowDelay { get; set; }

    /// <summary>Whether a slice whose data fails is concealed (<see cref="H264Decoder.ConcealErrors"/>) rather than failing the picture.</summary>
    bool ConcealErrors { get; set; }

    /// <summary>The macroblocks <see cref="Conceal"/> filled in.</summary>
    int ConcealedMbCount { get; }

    void Deblock();

    /// <summary>Fills in the macroblocks no slice decoded, from a reference (else <paramref name="fallback"/>, else grey).</summary>
    void Conceal(Picture? fallback);
}

/// <summary>
/// Decodes the slices of one picture into a <see cref="Picture"/> and keeps the per-macroblock
/// state that neighbouring macroblocks and the deblocking filter read. One instance serves every
/// picture of a sequence: <see cref="Begin"/> resets it for the next.
/// </summary>
/// <remarks>
/// Samples are <typeparamref name="TPixel"/>: byte for 8-bit video, ushort for deeper. Block arrays use raster order inside the macroblock: luma 4x4 block (x, y) is y * 4 + x,
/// chroma 4x4 block (x, y) of component c (0 = Cb, 1 = Cr) is c * 8 + y * 2 + x (y up to 1 in
/// 4:2:0, up to 3 in 4:2:2, whose chroma blocks are 8x16 samples).
/// </remarks>
internal sealed partial class PictureDecoder<TPixel> : IPictureDecoder
    where TPixel : unmanaged, IBinaryInteger<TPixel>
{
    private readonly int _widthInMbs;
    private readonly int _mbCount;

    // MbWidthC and MbHeightC (8 or 16) and the chroma 4x4 blocks of a component (4 or 8). In
    // 4:4:4 (ChromaArrayType 3) Cb and Cr are coded and reconstructed as luma is, each with its
    // own coefficients, QP and scaling lists, so the per-component state below has three entries.
    private readonly int _mbWidthC;
    private readonly int _mbHeightC;
    private readonly int _chromaBlocks;
    private readonly bool _chroma444;

    // Monochrome (ChromaArrayType 0): no chroma at all. It shares 4:4:4's syntax where chroma
    // would be (no intra_chroma_pred_mode, coded_block_pattern without a chroma part).
    private readonly bool _monochrome;
    private readonly bool _noChromaSyntax;

    // Bits per sample (8 for byte samples, more for ushort ones), and QpBdOffset (7-4): QP_Y runs
    // from -QpBdOffset to 51, dequantization uses QP'Y = QP_Y + QpBdOffset (8.5.12.1), the
    // deblocking filter QP_Y itself.
    private readonly int _bitDepth;
    private readonly int _qpBdOffset;

    // Per macroblock.
    private readonly int[] _sliceOfMb;
    private readonly MbKind[] _kind;
    private readonly int[] _qp;
    private readonly bool[] _transform8x8;

    // Per 4x4 block: TotalCoeff of luma (and in 4:4:4 of Cb and Cr, as luma blocks), of 4:2:0 and 4:2:2 chroma.
    private readonly byte[][] _totalCoeff;
    private readonly byte[] _totalCoeffLuma;
    private readonly byte[] _totalCoeffChroma;
    private readonly sbyte[] _intra4x4Mode;

    private readonly List<SliceInfo> _slices = [];

    // Coefficients of the macroblock being decoded, reused from one macroblock to the next; in
    // 4:4:4 also the luma-style levels of Cb and Cr.
    private readonly Residual _residual = new();
    private readonly Residual[] _residuals;

    // The CABAC engine and contexts, reinitialized for every CABAC slice.
    private readonly Cabac _cabac = new();

    // The scaling matrix of the last slice's parameter sets.
    private (SequenceParameterSet? Sps, PictureParameterSet? Pps, ScalingMatrix Matrix) _scaling = (null, null, ScalingMatrix.Flat);

    public PictureDecoder(SequenceParameterSet sps)
    {
        Sps = sps;
        _widthInMbs = sps.WidthInMbs;
        _mbCount = sps.WidthInMbs * sps.HeightInMbs;
        _mbWidthC = sps.MbWidthC;
        _mbHeightC = sps.MbHeightC;
        _chromaBlocks = _mbHeightC / 2;
        _chroma444 = sps.ChromaFormatIdc == 3;
        _monochrome = sps.ChromaFormatIdc == 0;
        _noChromaSyntax = _chroma444 || _monochrome;
        _bitDepth = sps.BitDepthLuma;
        _qpBdOffset = 6 * (_bitDepth - 8);

        _sliceOfMb = new int[_mbCount];
        Array.Fill(_sliceOfMb, -1);
        _kind = new MbKind[_mbCount];
        _qp = new int[_mbCount];
        _transform8x8 = new bool[_mbCount];
        _totalCoeff = _chroma444 ? [new byte[_mbCount * 16], new byte[_mbCount * 16], new byte[_mbCount * 16]] : [new byte[_mbCount * 16], [], []];
        _totalCoeffLuma = _totalCoeff[0];
        _residuals = _chroma444 ? [_residual, new(), new()] : [_residual];
        _totalCoeffChroma = new byte[_mbCount * 16];
        _intra4x4Mode = new sbyte[_mbCount * 16];

        InitInter();
        InitCabacState();
    }

    /// <summary>The current picture's SPS; another one of the same size may follow, so <see cref="Begin"/> sets it.</summary>
    public SequenceParameterSet Sps { get; private set; }

    public Picture Picture { get; private set; } = null!;

    // The current picture's planes as samples of TPixel.
    private TPixel[] PlaneY => Picture.Plane<TPixel>(0);

    private TPixel[] PlaneCb => Picture.Plane<TPixel>(1);

    private TPixel[] PlaneCr => Picture.Plane<TPixel>(2);

    public int DecodedMbCount { get; private set; }

    public int RowDelay { get; set; }

    /// <summary>
    /// Starts decoding into <paramref name="picture"/>, forgetting the previous picture's state: a
    /// frame, or the field of parity <paramref name="fieldParity"/>. The second field of a frame
    /// keeps the first one's macroblocks (its other slots) as they are.
    /// </summary>
    public void Begin(Picture picture, SequenceParameterSet sps, int fieldParity = -1, bool secondField = false)
    {
        Sps = sps;
        Picture = picture;
        DecodedMbCount = 0;
        _decodedPrefix = Math.Max(fieldParity, 0);
        _deblockedRows = 0;
        _reportedRows = 0;
        _slices.Clear();
        BeginConcealment();
        _fieldParity = fieldParity;
        _pairSlots = fieldParity >= 0;
        _mbaff = false;
        _fieldMb = picture.FieldMb;
        if (secondField)
        {
            (_mvX, _mvY, _refIdx, _refId, _refParity) = (picture.MvX, picture.MvY, picture.RefIdx, picture.RefId, picture.RefParity);
            ClearSlots(fieldParity);
            return;
        }

        picture.Damaged = false;
        Array.Fill(_sliceOfMb, -1);
        Array.Clear(_kind);
        Array.Clear(_qp);
        Array.Clear(_transform8x8);
        Array.Clear(_totalCoeff[0]);
        Array.Clear(_totalCoeff[1]);
        Array.Clear(_totalCoeff[2]);
        Array.Clear(_totalCoeffChroma);
        Array.Clear(_intra4x4Mode);
        Array.Fill(_fieldMb, fieldParity >= 0); // a frame coded as fields: all field macroblocks, by pair
        picture.Mbaff = fieldParity >= 0;
        picture.CodedAsFields = fieldParity >= 0;
        ResetInter(picture);
        ResetCabacState();
    }

    /// <summary>Clears the state of the macroblock slots of one field (every other slot).</summary>
    private void ClearSlots(int parity)
    {
        for (int mb = parity; mb < _mbCount; mb += 2)
        {
            _sliceOfMb[mb] = -1;
            _kind[mb] = MbKind.NotDecoded;
            _qp[mb] = 0;
            _transform8x8[mb] = false;
            ClearCounts(mb);
            _intra4x4Mode.AsSpan(mb * 16, 16).Clear();
            _cbp[mb] = 0;
            _chromaPredMode[mb] = 0;
            _dcCoded[mb] = 0;
            _directMb[mb] = false;
            _direct.AsSpan(mb * 4, 4).Clear();
            for (int list = 0; list < 2; list++)
            {
                _mvX[list].AsSpan(mb * 16, 16).Clear();
                _mvY[list].AsSpan(mb * 16, 16).Clear();
                _refIdx[list].AsSpan(mb * 4, 4).Fill(-1);
                _refId[list].AsSpan(mb * 4, 4).Clear();
                _refPic[list].AsSpan(mb * 4, 4).Clear();
                _absMvd[list][0].AsSpan(mb * 16, 16).Clear();
                _absMvd[list][1].AsSpan(mb * 16, 16).Clear();
            }
        }
    }

    /// <summary>Whether every macroblock of the picture (or of the field) is decoded.</summary>
    public bool IsComplete => DecodedMbCount == (_fieldParity >= 0 ? _mbCount / 2 : _mbCount);

    // The distance between consecutive macroblock addresses: a field picture's take every other slot.
    private int MbStep => _fieldParity >= 0 ? 2 : 1;

    /// <summary>slice_data (7.3.4); with <see cref="ConcealErrors"/>, what a failing slice leaves undecoded is filled in later (<see cref="Conceal"/>).</summary>
    public void DecodeSlice(SliceHeader header, PictureParameterSet pps, BitReader r, IReadOnlyList<Picture> refList0, IReadOnlyList<Picture> refList1, MarkSnapshot marks)
    {
        if (ConcealErrors)
        {
            DecodeSliceConcealed(header, pps, r, refList0, refList1, marks);
            return;
        }

        DecodeSliceData(header, pps, r, refList0, refList1, marks);
    }

    private void DecodeSliceData(SliceHeader header, PictureParameterSet pps, BitReader r, IReadOnlyList<Picture> refList0, IReadOnlyList<Picture> refList1, MarkSnapshot marks)
    {
        int sliceNum = _slices.Count;
        _mbaff = header.Mbaff;
        _pairSlots = header.Mbaff || header.FieldPic;
        Picture.Mbaff = _pairSlots;
        _slices.Add(new SliceInfo(header.DisableDeblockingFilterIdc, header.SliceAlphaC0Offset, header.SliceBetaOffset, pps.ChromaQpIndexOffset, pps.SecondChromaQpIndexOffset));

        if (_scaling.Sps != Sps || _scaling.Pps != pps)
        {
            _scaling = (Sps, pps, ScalingMatrix.For(Sps, pps));
        }

        var slice = new SliceState(header, pps, sliceNum, refList0, refList1, marks)
        {
            Scaling = _scaling.Matrix,
            Qp = pps.PicInitQp + header.SliceQpDelta,
            Cabac = pps.EntropyCodingModeCabac ? _cabac : null,
        };

        if (slice.Qp < -_qpBdOffset || slice.Qp > 51)
        {
            throw new InvalidDataException($"Slice QP {slice.Qp} is out of range.");
        }

        if (slice.Cabac is not null)
        {
            DecodeSliceDataCabac(slice, r);
            return;
        }

        int mbAddr = header.FirstMbAddr;
        bool moreData = true;
        do
        {
            if (header.Type != SliceType.I)
            {
                int skipRun = (int)r.ReadUe();
                if (skipRun > 0)
                {
                    moreData = r.MoreRbspData();
                }

                for (int i = 0; i < skipRun; i++)
                {
                    // A skipped top macroblock takes its pair's kind from the bottom one's
                    // mb_field_decoding_flag, the next bit, when the run ends with it; else it is inferred.
                    CheckAddress(mbAddr);
                    if (_mbaff && (mbAddr & 1) == 0)
                    {
                        _sliceOfMb[mbAddr] = slice.Number; // for the neighbours' availability
                        SetPairField(mbAddr, i == skipRun - 1 && moreData ? r.Peek(1) != 0 : InferPairField(mbAddr));
                    }

                    DecodeSkip(slice, mbAddr);
                    PairDone(mbAddr);
                    MacroblockDone(mbAddr);
                    mbAddr += MbStep;
                }
            }

            if (moreData)
            {
                CheckAddress(mbAddr);
                if (_mbaff && ((mbAddr & 1) == 0 || _kind[mbAddr - 1] == MbKind.PSkip))
                {
                    SetPairField(mbAddr, ReadFieldDecodingFlag(slice, r, mbAddr));
                }

                DecodeMacroblock(slice, r, mbAddr);
                CheckNotPastEnd(r.PastEnd);
                PairDone(mbAddr);
                MacroblockDone(mbAddr);
                mbAddr += MbStep;
                moreData = r.MoreRbspData();
            }
        }
        while (moreData);
    }

    /// <summary>A macroblock decoded from the zeros after the slice data (cut short) is not one: it fails.</summary>
    private static void CheckNotPastEnd(bool pastEnd)
    {
        if (pastEnd)
        {
            throw new InvalidDataException("The slice data ends inside a macroblock (cut short or damaged).");
        }
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
    private sealed class SliceState(SliceHeader header, PictureParameterSet pps, int number, IReadOnlyList<Picture> refList0, IReadOnlyList<Picture> refList1, MarkSnapshot marks)
    {
        public SliceHeader Header { get; } = header;

        public PictureParameterSet Pps { get; } = pps;

        public int Number { get; } = number;

        public IReadOnlyList<Picture> RefList0 { get; } = refList0;

        public IReadOnlyList<Picture> RefList1 { get; } = refList1;

        /// <summary>The marking of the lists' pictures when the lists were built.</summary>
        public MarkSnapshot Marks { get; } = marks;

        /// <summary>QP_Y of the previous macroblock in the slice (QP_Y,PRED).</summary>
        public int Qp { get; set; }

        public IReadOnlyList<Picture> RefList(int list) => list == 0 ? RefList0 : RefList1;

        /// <summary>The scaling matrix of the slice's SPS and PPS.</summary>
        public ScalingMatrix Scaling { get; init; } = ScalingMatrix.Flat;

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
    private int NeighborMb(int mbAddr, int x, int y, int maxW = 16, int maxH = 16)
    {
        if (_pairSlots)
        {
            return AffNeighbor(mbAddr, x, y, maxW, maxH).Mb;
        }

        if (y >= maxH || (x >= maxW && y >= 0))
        {
            return -1;
        }

        int mbX = mbAddr % _widthInMbs;
        int mbY = mbAddr / _widthInMbs;
        int nx = mbX + (x < 0 ? -1 : x >= maxW ? 1 : 0);
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

    /// <summary>nC for a luma 4x4 block (9.2.1), or in 4:4:4 a Cb or Cr one (<paramref name="component"/> 1 or 2).</summary>
    private int LumaNc(int mbAddr, int x, int y, int component = 0) =>
        CombineNc(
            NeighborTotalCoeff(mbAddr, (x * 4) - 1, y * 4, chroma: false, component),
            NeighborTotalCoeff(mbAddr, x * 4, (y * 4) - 1, chroma: false, component));

    /// <summary>nC for a chroma AC 4x4 block of component <paramref name="c"/> (9.2.1).</summary>
    private int ChromaNc(int mbAddr, int c, int x, int y) =>
        CombineNc(
            NeighborTotalCoeff(mbAddr, (x * 4) - 1, y * 4, chroma: true, c),
            NeighborTotalCoeff(mbAddr, x * 4, (y * 4) - 1, chroma: true, c));

    private static int CombineNc(int a, int b) => (a, b) switch
    {
        ( >= 0, >= 0) => (a + b + 1) >> 1,
        ( >= 0, _) => a,
        (_, >= 0) => b,
        _ => 0,
    };

    /// <summary>TotalCoeff of the 4x4 block covering a location, or -1 when unavailable (luma-shaped blocks: of luma or of a 4:4:4 chroma component).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int NeighborTotalCoeff(int mbAddr, int x, int y, bool chroma, int component)
    {
        var (n, xW, yW) = chroma ? Neighbor(mbAddr, x, y, _mbWidthC, _mbHeightC) : Neighbor(mbAddr, x, y);
        if (n < 0)
        {
            return -1;
        }

        int bx = xW / 4;
        int by = yW / 4;
        return chroma
            ? _totalCoeffChroma[(n * 16) + (component * 8) + (by * 2) + bx]
            : _totalCoeff[component][(n * 16) + (by * 4) + bx];
    }

    // ---- Macroblock layer (7.3.5) ----
    private void DecodeMacroblock(SliceState slice, BitReader r, int mbAddr)
    {
        var type = slice.Header.Type;

        // Inter types come first in P (5, Table 7-13) and B slices (23, Table 7-14), then the 26 intra ones.
        int interTypes = type switch { SliceType.P => 5, SliceType.B => 23, _ => 0 };
        int mbType = slice.Cabac is not { } c ? r.ReadUe(interTypes + 25, "mb_type")
            : type == SliceType.P ? CabacPMbType(c, mbAddr)
            : type == SliceType.B ? CabacBMbType(c, mbAddr)
            : CabacIntraMbType(c, mbAddr);

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
            cbpChroma = _noChromaSyntax ? 0 : (t / 4) % 3; // 4:4:4 and 4:0:0 have no chroma part (7.4.5)
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

        // 4:4:4 predicts Cb and Cr with the luma modes: there is no intra_chroma_pred_mode.
        int chromaPredMode = _noChromaSyntax ? 0 : slice.Cabac is { } cabac ? CabacChromaPredMode(cabac, mbAddr) : r.ReadUe(3, "intra_chroma_pred_mode");
        _chromaPredMode[mbAddr] = (byte)chromaPredMode;

        if (!is16x16)
        {
            int cbp = slice.Cabac is { } cabacCbp
                ? CabacCbp(cabacCbp, mbAddr)
                : (_noChromaSyntax ? CavlcTables.CodedBlockPatternIntra444 : CavlcTables.CodedBlockPatternIntra)[r.ReadUe(_noChromaSyntax ? 15 : 47, "coded_block_pattern")];
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
        SetLeftRows(slice, mbAddr, neighbors.A);
        var target = _mbaff ? GatherMbaffNeighbors(mbAddr) : PictureTarget(mbAddr);
        ReconstructIntraLuma(slice, mbAddr, target, is16x16, predMode16x16, cbpLuma, neighbors, 0);
        if (_chroma444)
        {
            ReconstructIntraLuma(slice, mbAddr, target, is16x16, predMode16x16, cbpLuma, neighbors, 1);
            ReconstructIntraLuma(slice, mbAddr, target, is16x16, predMode16x16, cbpLuma, neighbors, 2);
        }
        else if (!_monochrome)
        {
            ReconstructChroma(slice, mbAddr, target, residual, cbpChroma, intraMode: chromaPredMode, neighbors);
        }

        if (_mbaff)
        {
            ScatterMbaff(mbAddr, target);
        }
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
        _totalCoeffChroma.AsSpan(mbAddr * 16, 16).Fill(16);
        if (_chroma444)
        {
            _totalCoeff[1].AsSpan(mbAddr * 16, 16).Fill(16);
            _totalCoeff[2].AsSpan(mbAddr * 16, 16).Fill(16);
        }

        // For the CABAC contexts of later macroblocks I_PCM counts as fully coded.
        _cbp[mbAddr] = 15 | (2 << 4);
        _dcCoded[mbAddr] = 7;
        _chromaPredMode[mbAddr] = 0;
        slice.LastQpDeltaNonZero = false;

        while (!r.IsByteAligned)
        {
            r.Skip(1); // pcm_alignment_zero_bit
        }

        var (lumaOrigin, lumaStride) = LumaPlace(mbAddr);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                PlaneY[lumaOrigin + (y * lumaStride) + x] = TPixel.CreateTruncating(r.ReadBits(_bitDepth));
            }
        }

        var (chromaOrigin, chromaStride) = ChromaPlace(mbAddr);
        foreach (var plane in new[] { PlaneCb, PlaneCr })
        {
            for (int y = 0; y < _mbHeightC; y++)
            {
                for (int x = 0; x < _mbWidthC; x++)
                {
                    plane[chromaOrigin + (y * chromaStride) + x] = TPixel.CreateTruncating(r.ReadBits(_bitDepth));
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
        if (delta < -(26 + (_qpBdOffset / 2)) || delta > 25 + (_qpBdOffset / 2))
        {
            throw new InvalidDataException($"mb_qp_delta {delta} is out of range.");
        }

        // 7-37: QP_Y wraps within -QpBdOffset .. 51.
        slice.Qp = ((slice.Qp + delta + 52 + (2 * _qpBdOffset)) % (52 + _qpBdOffset)) - _qpBdOffset;
    }

    private void ClearCounts(int mbAddr)
    {
        _totalCoeffLuma.AsSpan(mbAddr * 16, 16).Clear();
        _totalCoeffChroma.AsSpan(mbAddr * 16, 16).Clear();
        if (_chroma444)
        {
            _totalCoeff[1].AsSpan(mbAddr * 16, 16).Clear();
            _totalCoeff[2].AsSpan(mbAddr * 16, 16).Clear();
        }
    }

    /// <summary>Intra4x4PredMode prediction (8.3.1.1).</summary>
    private int PredictIntra4x4Mode(SliceState slice, int mbAddr, int x, int y)
    {
        var (a, xA, yA) = Neighbor(mbAddr, (x * 4) - 1, y * 4);
        var (b, xB, yB) = Neighbor(mbAddr, x * 4, (y * 4) - 1);
        bool constrained = slice.Pps.ConstrainedIntraPred;
        if (a < 0 || b < 0 || (constrained && (IsInter(a) || IsInter(b))))
        {
            return 2;
        }

        // For 8x8 blocks this reads the neighbouring 4x4 block at the same location, which is the
        // 4x4 block 8.3.2.1 picks (n = 1 or 3 for A, 2 for B), MBAFF field/frame mixtures included.
        int modeA = NeighborIntraMode(a, xA / 4, yA / 4);
        int modeB = NeighborIntraMode(b, xB / 4, yB / 4);
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

        /// <summary>Chroma DC levels per component, in the order they are coded (4 in 4:2:0, 8 in 4:2:2).</summary>
        public int[][] ChromaDc { get; } = [new int[8], new int[8]];

        /// <summary>Chroma levels per component and raster 4x4 block (component * 8 + y * 2 + x).</summary>
        public int[][] ChromaAc { get; } = [.. Enumerable.Range(0, 16).Select(_ => new int[16])];
    }

    /// <summary>
    /// One residual block by either entropy coder, into <paramref name="block"/> through
    /// <paramref name="scan"/> (cleared first); returns the number of nonzero levels.
    /// </summary>
    private int ReadBlock(SliceState slice, BitReader r, int mbAddr, BlockCategory category, int x, int y, int component, int maxNumCoeff, Span<int> block, ReadOnlySpan<byte> scan)
    {
        block.Clear();
        if (slice.Cabac is { } c)
        {
            // 8x8 blocks have a coded_block_flag of their own only in 4:4:4 (7.3.5.3.3).
            int inc = category == BlockCategory.Luma8x8 && !_chroma444 ? -1 : CodedBlockFlagInc(mbAddr, category, x, y, component);
            return CabacResidualBlock(c, category, component, inc, IsFieldMb(mbAddr), maxNumCoeff, block, scan);
        }

        int nC = category switch
        {
            BlockCategory.ChromaDc => _mbHeightC == 8 ? Cavlc.ChromaDcNc : Cavlc.ChromaDc422Nc,
            BlockCategory.ChromaAc => ChromaNc(mbAddr, component, x, y),
            _ => LumaNc(mbAddr, x, y, component),
        };
        return Cavlc.ReadResidualBlock(r, nC, maxNumCoeff, block, scan);
    }

    // Scans without the DC (AC blocks start at scan position 1), and the chroma DC levels in coded order.
    private static ReadOnlySpan<byte> ZigZag4x4Ac => [1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    private static ReadOnlySpan<byte> ChromaDcScan => [0, 1, 2, 3];

    // 4:2:2's 2x4 chroma DC: coded order down the first column first (8.5.11.1), as raster indices two to a row.
    private static ReadOnlySpan<byte> ChromaDc422Scan => [0, 2, 1, 4, 6, 3, 5, 7];

    // CAVLC's 8x8 blocks come as four interleaved 4x4 blocks: coefficient k of block i4 is at 8x8
    // scan position 4 * k + i4 (7.3.5.3), so row i4 here holds those positions' raster indices.
    private static readonly byte[] Cavlc8x8Scan = [.. Enumerable.Range(0, 64).Select(i => Transform.ZigZag8x8[(4 * (i % 16)) + (i / 16)])];

    private void ReadResidual(SliceState slice, BitReader r, int mbAddr, Residual residual, bool is16x16, int cbpLuma, int cbpChroma)
    {
        Span<byte> chromaCounts = _totalCoeffChroma.AsSpan(mbAddr * 16, 16);
        chromaCounts.Clear();
        ReadResidualLuma(slice, r, mbAddr, residual, is16x16, cbpLuma, 0);
        if (_chroma444)
        {
            // residual_luma again for Cb and Cr (7.3.5.3), under the luma coded_block_pattern.
            ReadResidualLuma(slice, r, mbAddr, _residuals[1], is16x16, cbpLuma, 1);
            ReadResidualLuma(slice, r, mbAddr, _residuals[2], is16x16, cbpLuma, 2);
            return;
        }

        if (cbpChroma == 0)
        {
            return;
        }

        // Field macroblocks use the field scans (8.5.6).
        var scanAc = IsFieldMb(mbAddr) ? Field4x4Ac : ZigZag4x4Ac;

        for (int c = 0; c < 2; c++)
        {
            int dcCount = _chromaBlocks; // 4 * NumC8x8
            if (ReadBlock(slice, r, mbAddr, BlockCategory.ChromaDc, 0, 0, c, dcCount, residual.ChromaDc[c].AsSpan(0, dcCount), dcCount == 4 ? ChromaDcScan : ChromaDc422Scan) != 0)
            {
                _dcCoded[mbAddr] |= (byte)(2 << c);
            }
        }

        if (cbpChroma < 2)
        {
            return;
        }

        for (int c = 0; c < 2; c++)
        {
            for (int b = 0; b < _chromaBlocks; b++)
            {
                // In 4:2:2 the blocks come by 8x8 quadrant (chroma4x4BlkIdx), which is raster order here too.
                chromaCounts[(c * 8) + b] = (byte)ReadBlock(slice, r, mbAddr, BlockCategory.ChromaAc, b & 1, b >> 1, c, 15, residual.ChromaAc[(c * 8) + b], scanAc);
            }
        }
    }

    /// <summary>
    /// residual_luma (7.3.5.3) of <paramref name="component"/>: luma, or in 4:4:4 Cb (1) or Cr (2),
    /// whose blocks are luma blocks of ctxBlockCat 6 to 9 and 10 to 13.
    /// </summary>
    private void ReadResidualLuma(SliceState slice, BitReader r, int mbAddr, Residual residual, bool is16x16, int cbpLuma, int component)
    {
        Span<byte> counts = _totalCoeff[component].AsSpan(mbAddr * 16, 16);
        counts.Clear();

        // Field macroblocks use the field scans (8.5.6).
        bool field = IsFieldMb(mbAddr);
        var scan4x4 = field ? Field4x4 : Transform.ZigZag4x4;
        if (is16x16 && ReadBlock(slice, r, mbAddr, BlockCategory.LumaDc, 0, 0, component, 16, residual.LumaDc, scan4x4) != 0)
        {
            _dcCoded[mbAddr] |= (byte)(1 << component);
        }

        if (_transform8x8[mbAddr])
        {
            ReadResidual8x8(slice, r, mbAddr, residual, cbpLuma, component);
            return;
        }

        var scan = is16x16 ? (field ? Field4x4Ac : ZigZag4x4Ac) : scan4x4;
        for (int idx = 0; idx < 16; idx++)
        {
            if ((cbpLuma & (1 << (idx >> 2))) == 0)
            {
                continue;
            }

            // Reconstruction reads the counts and leaves blocks without coefficients alone.
            var (x, y) = BlockPosition(idx);
            var category = is16x16 ? BlockCategory.LumaAc : BlockCategory.Luma4x4;
            counts[(y * 4) + x] = (byte)ReadBlock(slice, r, mbAddr, category, x, y, component, scan.Length, residual.Luma[(y * 4) + x], scan);
        }
    }

    /// <summary>
    /// The luma levels (or 4:4:4 Cb or Cr levels) of a macroblock with 8x8 transforms: with CABAC
    /// one block of 64 per coded 8x8 block, whose count stands for all four of its 4x4 blocks;
    /// with CAVLC four interleaved 4x4 blocks (7.3.5.3), each keeping its own TotalCoeff for the
    /// nC of later blocks.
    /// </summary>
    private void ReadResidual8x8(SliceState slice, BitReader r, int mbAddr, Residual residual, int cbpLuma, int component)
    {
        Span<byte> counts = _totalCoeff[component].AsSpan(mbAddr * 16, 16);
        bool field = IsFieldMb(mbAddr);
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
                byte count = (byte)ReadBlock(slice, r, mbAddr, BlockCategory.Luma8x8, x8, y8, component, 64, block, field ? Field8x8 : Transform.ZigZag8x8);
                counts[(y8 * 4) + x8] = count;
                counts[(y8 * 4) + x8 + 1] = count;
                counts[((y8 + 1) * 4) + x8] = count;
                counts[((y8 + 1) * 4) + x8 + 1] = count;
                continue;
            }

            block.AsSpan().Clear();
            for (int i4 = 0; i4 < 4; i4++)
            {
                // Each 4x4 read clears nothing: it fills its own quarter of the 8x8 positions.
                var (x, y) = BlockPosition((b8 * 4) + i4);
                int nC = LumaNc(mbAddr, x, y, component);
                counts[(y * 4) + x] = (byte)Cavlc.ReadResidualBlock(r, nC, 16, block, (field ? CavlcField8x8Scan : Cavlc8x8Scan).AsSpan(i4 * 16, 16));
            }
        }
    }

    /// <summary>Whether any of the four 4x4 blocks of 8x8 block <paramref name="b8"/> of a component has coefficients.</summary>
    private bool Has8x8Coefficients(int mbAddr, int b8, int component = 0)
    {
        var counts = _totalCoeff[component];
        int i = (mbAddr * 16) + ((b8 >> 1) * 8) + ((b8 & 1) * 2);
        return (counts[i] | counts[i + 1] | counts[i + 4] | counts[i + 5]) != 0;
    }

    /// <summary>
    /// Adds the residual of 8x8 block <paramref name="b8"/>, if coded, to the prediction of a
    /// component (luma, or Cb or Cr in 4:4:4); <paramref name="qp"/> is its QP'.
    /// </summary>
    private void AddLuma8x8Residual(SliceState slice, int mbAddr, MbTarget target, int cbpLuma, int b8, int qp, int component)
    {
        if ((cbpLuma & (1 << b8)) == 0 || !Has8x8Coefficients(mbAddr, b8, component))
        {
            return;
        }

        var (plane, origin, stride) = ComponentOf(target, component);
        var block = _residuals[component].Luma8x8[b8];
        if (IsLossless(mbAddr))
        {
            int mode = _intra4x4Mode[(mbAddr * 16) + ((b8 >> 1) * 8) + ((b8 & 1) * 2)];
            Transform.AddBypass(block, 8, 8, DpcmOf(mode), plane.AsSpan(origin + ((b8 >> 1) * 8 * stride) + ((b8 & 1) * 8)), stride, _bitDepth);
            return;
        }

        Transform.Dequantize8x8(block, qp, slice.Scaling.LevelScale8x8((2 * component) + (IsInter(mbAddr) ? 1 : 0)));
        Transform.InverseTransform8x8Add(block, plane.AsSpan(origin + ((b8 >> 1) * 8 * stride) + ((b8 & 1) * 8)), stride, _bitDepth);
    }

    /// <summary>
    /// Whether the macroblock is lossless: TransformBypassModeFlag, set when the SPS allows it and
    /// QP'Y is 0 (7.4.2.1.1), for all three components whatever their QP'C.
    /// </summary>
    private bool IsLossless(int mbAddr) => Sps.TransformBypass && _qp[mbAddr] + _qpBdOffset == 0;

    /// <summary>
    /// How a lossless intra block's residual is summed (8.5.15) for a luma prediction mode
    /// (Intra4x4, Intra8x8 or Intra16x16: 0 vertical, 1 horizontal); -1, inter, for none.
    /// </summary>
    private static BypassDpcm DpcmOf(int lumaMode) => lumaMode switch
    {
        0 => BypassDpcm.Vertical,
        1 => BypassDpcm.Horizontal,
        _ => BypassDpcm.None,
    };

    /// <summary>The plane, first sample and row stride of a component of a target.</summary>
    private static (TPixel[] Plane, int Origin, int Stride) ComponentOf(MbTarget target, int component) => component switch
    {
        0 => (target.Y, target.YOrigin, target.YStride),
        1 => (target.Cb, target.COrigin, target.CStride),
        _ => (target.Cr, target.COrigin, target.CStride),
    };

    /// <summary>QP' of a component (8.5.12.1): QP'Y, or QP'C of Cb or Cr (the same as luma's QpBdOffset: equal bit depths).</summary>
    private int QpPrime(SliceState slice, int mbAddr, int component) => component == 0
        ? _qp[mbAddr] + _qpBdOffset
        : ChromaQp(_qp[mbAddr], component == 1 ? slice.Pps.ChromaQpIndexOffset : slice.Pps.SecondChromaQpIndexOffset, _qpBdOffset) + _qpBdOffset;

    // ---- Reconstruction ----
    /// <summary>
    /// Intra prediction and residual of luma, or in 4:4:4 of Cb or Cr (<paramref name="component"/>
    /// 1 or 2), which take luma's prediction modes (8.3.4.5) and their own levels, QP and scaling lists.
    /// </summary>
    private void ReconstructIntraLuma(SliceState slice, int mbAddr, MbTarget target, bool is16x16, int predMode16x16, int cbpLuma, (bool A, bool B, bool C, bool D) mbs, int component)
    {
        var (plane, origin, stride) = ComponentOf(target, component);
        var residual = _residuals[component];
        var counts = _totalCoeff[component];
        int qp = QpPrime(slice, mbAddr, component);
        var levelScale = slice.Scaling.LevelScale4x4(component); // Intra Y, Cb or Cr

        if (is16x16)
        {
            IntraPrediction.Predict16x16(predMode16x16, plane, origin, stride, new IntraNeighbors(LeftRows(0, 16), mbs.B, mbs.C, mbs.D), _bitDepth);
            if (IsLossless(mbAddr))
            {
                AddBypass16x16(mbAddr, residual, counts, predMode16x16, plane.AsSpan(origin), stride);
                return;
            }

            Span<int> dc = stackalloc int[16];
            residual.LumaDc.CopyTo(dc);
            Transform.InverseLumaDc(dc, qp, levelScale);

            for (int i = 0; i < 16; i++)
            {
                var dest = plane.AsSpan(origin + ((i >> 2) * 4 * stride) + ((i & 3) * 4));
                if (counts[(mbAddr * 16) + i] == 0)
                {
                    Transform.AddDc(dc[i], dest, stride, _bitDepth);
                    continue;
                }

                var block = residual.Luma[i];
                block[0] = dc[i];
                Transform.Dequantize(block, qp, skipDc: true, levelScale);
                Transform.InverseTransformAdd(block, dest, stride, _bitDepth);
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
                    0 => new IntraNeighbors(LeftRows(0, 8), mbs.B, mbs.B, mbs.D),
                    1 => new IntraNeighbors(true, mbs.B, mbs.C, mbs.B),
                    2 => new IntraNeighbors(LeftRows(8, 8), true, true, LeftRows(7, 1)),
                    _ => new IntraNeighbors(true, true, false, true),
                };
                int offset8 = origin + (y * 4 * stride) + (x * 4);
                IntraPrediction.Predict8x8(_intra4x4Mode[(mbAddr * 16) + (y * 4) + x], plane, offset8, stride, n, _bitDepth);
                AddLuma8x8Residual(slice, mbAddr, target, cbpLuma, b8, qp, component);
            }

            return;
        }

        for (int idx = 0; idx < 16; idx++)
        {
            var (x, y) = BlockPosition(idx);
            int offset = origin + (y * 4 * stride) + (x * 4);

            bool left = x > 0 || LeftRows(y * 4, 4);
            bool top = y > 0 || mbs.B;
            bool topLeft = (x > 0, y > 0) switch
            {
                (true, true) => true,
                (false, true) => LeftRows((y * 4) - 1, 1),
                (true, false) => mbs.B,
                _ => mbs.D,
            };
            bool topRight = y == 0
                ? (x < 3 ? mbs.B : mbs.C)
                : x < 3 && BlockIndex(x + 1, y - 1) < idx;

            IntraPrediction.Predict4x4(_intra4x4Mode[(mbAddr * 16) + (y * 4) + x], plane, offset, stride, new IntraNeighbors(left, top, topRight, topLeft), _bitDepth);

            if ((cbpLuma & (1 << (idx >> 2))) != 0 && counts[(mbAddr * 16) + (y * 4) + x] != 0)
            {
                var block = residual.Luma[(y * 4) + x];
                if (IsLossless(mbAddr))
                {
                    Transform.AddBypass(block, 4, 4, DpcmOf(_intra4x4Mode[(mbAddr * 16) + (y * 4) + x]), plane.AsSpan(offset), stride, _bitDepth);
                    continue;
                }

                Transform.Dequantize(block, qp, skipDc: false, levelScale);
                Transform.InverseTransformAdd(block, plane.AsSpan(offset), stride, _bitDepth);
            }
        }
    }

    /// <summary>
    /// The residual of a lossless Intra_16x16 macroblock (8.5.10, 8.5.15): each 4x4 block's levels
    /// with its DC level in place, summed over the whole 16x16 block for vertical or horizontal prediction.
    /// </summary>
    [SkipLocalsInit]
    private void AddBypass16x16(int mbAddr, Residual residual, byte[] counts, int predMode16x16, Span<TPixel> dest, int stride)
    {
        Span<int> r = stackalloc int[256];
        for (int i = 0; i < 16; i++)
        {
            var block = residual.Luma[i];
            bool coded = counts[(mbAddr * 16) + i] != 0;
            for (int k = 0; k < 16; k++)
            {
                r[((((i >> 2) * 4) + (k >> 2)) * 16) + ((i & 3) * 4) + (k & 3)] = k == 0 ? residual.LumaDc[i] : coded ? block[k] : 0;
            }
        }

        Transform.AddBypass(r, 16, 16, DpcmOf(predMode16x16), dest, stride, _bitDepth);
    }

    /// <summary>
    /// The chroma residual of a lossless macroblock (4:2:0 or 4:2:2): the levels of each 4x4 block
    /// with its DC level in place, summed over the whole component for horizontal (1) or vertical
    /// (2) intra prediction (8.5.11, 8.5.15).
    /// </summary>
    [SkipLocalsInit]
    private void AddChromaBypass(int mbAddr, Residual residual, int c, int intraMode, Span<TPixel> dest, int stride)
    {
        Span<int> r = stackalloc int[8 * 16];
        for (int b = 0; b < _chromaBlocks; b++)
        {
            var block = residual.ChromaAc[(c * 8) + b];
            bool coded = _totalCoeffChroma[(mbAddr * 16) + (c * 8) + b] != 0;
            for (int k = 0; k < 16; k++)
            {
                r[((((b >> 1) * 4) + (k >> 2)) * 8) + ((b & 1) * 4) + (k & 3)] = k == 0 ? residual.ChromaDc[c][b] : coded ? block[k] : 0;
            }
        }

        var dpcm = intraMode switch
        {
            1 => BypassDpcm.Horizontal,
            2 => BypassDpcm.Vertical,
            _ => BypassDpcm.None,
        };
        Transform.AddBypass(r[..(8 * _mbHeightC)], 8, _mbHeightC, dpcm, dest, stride, _bitDepth);
    }

    /// <summary>
    /// Chroma prediction (intra only; inter prediction is done before) and residual for both
    /// components. <paramref name="intraMode"/> is -1 for inter macroblocks.
    /// </summary>
    private void ReconstructChroma(SliceState slice, int mbAddr, MbTarget target, Residual residual, int cbpChroma, int intraMode, (bool A, bool B, bool C, bool D) mbs)
    {
        int origin = target.COrigin;
        int stride = target.CStride;
        int blocks = _chromaBlocks;

        for (int c = 0; c < 2; c++)
        {
            // QP'C (8.5.11): QP_C plus QpBdOffsetC (the same as luma's here: equal bit depths).
            int qpc = ChromaQp(_qp[mbAddr], c == 0 ? slice.Pps.ChromaQpIndexOffset : slice.Pps.SecondChromaQpIndexOffset, _qpBdOffset) + _qpBdOffset;
            var levelScale = slice.Scaling.LevelScale4x4((intraMode >= 0 ? 1 : 4) + c); // Intra or Inter Cb, Cr
            TPixel[] plane = c == 0 ? target.Cb : target.Cr;
            if (intraMode >= 0)
            {
                IntraPrediction.PredictChroma(intraMode, plane, origin, stride, new IntraNeighbors(LeftChromaRows(0), mbs.B, mbs.C, mbs.D), _bitDepth, LeftChromaBlocks(), _mbHeightC);
            }

            if (cbpChroma == 0)
            {
                continue;
            }

            if (IsLossless(mbAddr))
            {
                AddChromaBypass(mbAddr, residual, c, intraMode, plane.AsSpan(origin), stride);
                continue;
            }

            // In place: the levels are read again for every macroblock with chroma coefficients.
            Span<int> dc = residual.ChromaDc[c];
            if (blocks == 4)
            {
                Transform.InverseChromaDc(dc, qpc, levelScale);
            }
            else
            {
                Transform.InverseChromaDc422(dc, qpc, levelScale);
            }

            for (int b = 0; b < blocks; b++)
            {
                var dest = plane.AsSpan(origin + ((b >> 1) * 4 * stride) + ((b & 1) * 4));
                if (_totalCoeffChroma[(mbAddr * 16) + (c * 8) + b] == 0)
                {
                    Transform.AddDc(dc[b], dest, stride, _bitDepth);
                    continue;
                }

                var block = residual.ChromaAc[(c * 8) + b];
                block[0] = dc[b];
                Transform.Dequantize(block, qpc, skipDc: true, levelScale);
                Transform.InverseTransformAdd(block, dest, stride, _bitDepth);
            }
        }
    }

    /// <summary>QP_C from QP_Y and the chroma offset (8-311, Table 8-15): below 30 (down to -QpBdOffsetC) it is qPI itself.</summary>
    public static int ChromaQp(int qpY, int offset, int qpBdOffset = 0)
    {
        int qpi = Math.Clamp(qpY + offset, -qpBdOffset, 51);
        return qpi < 30 ? qpi : ChromaQpTable[qpi - 30];
    }

    private static ReadOnlySpan<byte> ChromaQpTable => [29, 30, 31, 32, 32, 33, 34, 34, 35, 35, 36, 36, 37, 37, 37, 38, 38, 38, 39, 39, 39, 39];
}

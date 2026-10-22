using System.Runtime.CompilerServices;

namespace ManagedCodecs.H264;

/// <summary>
/// CABAC parsing of slice data (H.264 clause 9.3): binarizations and context index derivations of
/// the syntax elements of I and P slices. The macroblock layer itself is shared with CAVLC; it calls
/// these methods when the slice has a <see cref="Cabac"/> engine.
/// </summary>
internal sealed partial class PictureDecoder<TPixel>
{
    // ctxBlockCatOffset (Table 9-40) for ctxBlockCat 0..4: coded_block_flag,
    // significant_coeff_flag / last_significant_coeff_flag, coeff_abs_level_minus1.
    private static ReadOnlySpan<byte> CodedBlockFlagCatOffset => [0, 4, 8, 12, 16];

    private static ReadOnlySpan<byte> SignificantCatOffset => [0, 15, 29, 44, 47];

    private static ReadOnlySpan<byte> AbsLevelCatOffset => [0, 10, 20, 30, 39];

    // ctxIdxInc of significant_coeff_flag and last_significant_coeff_flag in 8x8 blocks of frames
    // by scan position (Table 9-43), for positions 0..62.
    private static ReadOnlySpan<byte> Significant8x8Inc =>
    [
        0, 1, 2, 3, 4, 5, 5, 4, 4, 3, 3, 4, 4, 4, 5, 5, 4, 4, 4, 4, 3, 3, 6, 7, 7, 7, 8, 9, 10, 9, 8, 7,
        7, 6, 11, 12, 13, 11, 6, 7, 8, 9, 14, 10, 9, 8, 6, 11, 12, 13, 11, 6, 9, 14, 10, 9, 11, 12, 13, 11, 14, 10, 12,
    ];

    // The same for field macroblocks (Table 9-43, field column); last_significant_coeff_flag uses Last8x8Inc for both.
    private static ReadOnlySpan<byte> Significant8x8FieldInc =>
    [
        0, 1, 1, 2, 2, 3, 3, 4, 5, 6, 7, 7, 7, 8, 4, 5, 6, 9, 10, 10, 8, 11, 12, 11, 9, 9, 10, 10, 8, 11, 12, 11,
        9, 9, 10, 10, 8, 11, 12, 11, 9, 9, 10, 10, 8, 13, 13, 9, 9, 10, 10, 8, 13, 13, 9, 9, 10, 10, 14, 14, 14, 14, 14,
    ];

    private static ReadOnlySpan<byte> Last8x8Inc =>
    [
        0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 8, 8, 8,
    ];

    // Per macroblock, for the context derivations: coded_block_pattern (luma | chroma << 4),
    // intra_chroma_pred_mode, and the coded_block_flag of the DC blocks (bit 0 luma, 1 Cb, 2 Cr).
    private byte[] _cbp = [];
    private byte[] _chromaPredMode = [];
    private byte[] _dcCoded = [];

    // Per macroblock: B_Skip or B_Direct_16x16 (for the contexts of mb_type in B slices).
    private bool[] _directMb = [];

    // Per list, component and luma 4x4 block: |mvd|, capped (only sums up to 33 matter, also
    // after the halving of 9.3.3.1.1.7).
    private byte[][][] _absMvd = [[[], []], [[], []]];

    /// <summary>ctxBlockCat (Table 9-42).</summary>
    private enum BlockCategory
    {
        LumaDc = 0,
        LumaAc = 1,
        Luma4x4 = 2,
        ChromaDc = 3,
        ChromaAc = 4,
        Luma8x8 = 5,
    }

    private void InitCabacState()
    {
        _cbp = new byte[_mbCount];
        _chromaPredMode = new byte[_mbCount];
        _dcCoded = new byte[_mbCount];
        _directMb = new bool[_mbCount];
        _absMvd = [[new byte[_mbCount * 16], new byte[_mbCount * 16]], [new byte[_mbCount * 16], new byte[_mbCount * 16]]];
    }

    private void ResetCabacState()
    {
        Array.Clear(_cbp);
        Array.Clear(_chromaPredMode);
        Array.Clear(_dcCoded);
        Array.Clear(_directMb);
        foreach (var list in _absMvd)
        {
            Array.Clear(list[0]);
            Array.Clear(list[1]);
        }
    }

    /// <summary>slice_data (7.3.4) for CABAC.</summary>
    private void DecodeSliceDataCabac(SliceState slice, BitReader r)
    {
        while (!r.IsByteAligned)
        {
            if (!r.ReadFlag())
            {
                throw new InvalidDataException("cabac_alignment_one_bit is 0.");
            }
        }

        var cabac = slice.Cabac!;
        cabac.InitContexts(slice.Header.Type == SliceType.I ? 0 : 1 + slice.Header.CabacInitIdc, slice.Qp);
        cabac.InitEngine(r);

        int mbAddr = slice.Header.FirstMbAddr;
        int skipCtx = slice.Header.Type == SliceType.B ? 24 : 11;
        do
        {
            CheckAddress(mbAddr);

            // The contexts of mb_skip_flag and mb_type already look at the neighbours, which
            // count as available only once this macroblock belongs to the slice.
            _sliceOfMb[mbAddr] = slice.Number;
            if (_mbaff)
            {
                mbAddr = DecodeMbaffCabac(slice, r, cabac, mbAddr, skipCtx);
                PairDone(mbAddr);
            }
            else if (slice.Header.Type != SliceType.I && cabac.Decision(skipCtx + SkipFlagInc(mbAddr)) == 1)
            {
                DecodeSkip(slice, mbAddr);
            }
            else
            {
                DecodeMacroblock(slice, r, mbAddr);
            }

            mbAddr += MbStep;
        }
        while ((_mbaff && (mbAddr & 1) != 0) || cabac.Terminate() == 0); // end_of_slice_flag, in MBAFF frames only after a pair
    }

    /// <summary>
    /// One macroblock of an MBAFF frame with CABAC, or both of its pair when the top one is skipped;
    /// returns the address of the last one decoded. The pair's mb_field_decoding_flag comes with
    /// the first macroblock that is not skipped (7.3.4). Until then the pair is taken to be of the
    /// kind inferred from its neighbours (7.4.4), which the mb_skip_flag contexts use; a skipped
    /// top macroblock's motion depends on the flag, so it is decoded after the bottom one's.
    /// </summary>
    private int DecodeMbaffCabac(SliceState slice, BitReader r, Cabac cabac, int mbAddr, int skipCtx)
    {
        bool isTop = (mbAddr & 1) == 0;
        if (slice.Header.Type == SliceType.I)
        {
            if (isTop)
            {
                SetPairField(mbAddr, ReadFieldDecodingFlag(slice, r, mbAddr));
            }

            DecodeMacroblock(slice, r, mbAddr);
            return mbAddr;
        }

        if (!isTop)
        {
            // The top macroblock was coded, with the flag.
            if (cabac.Decision(skipCtx + SkipFlagInc(mbAddr)) == 1)
            {
                DecodeSkip(slice, mbAddr);
            }
            else
            {
                DecodeMacroblock(slice, r, mbAddr);
            }

            return mbAddr;
        }

        SetPairField(mbAddr, InferPairField(mbAddr));
        if (cabac.Decision(skipCtx + SkipFlagInc(mbAddr)) == 0)
        {
            SetPairField(mbAddr, ReadFieldDecodingFlag(slice, r, mbAddr));
            DecodeMacroblock(slice, r, mbAddr);
            return mbAddr;
        }

        // A skipped top macroblock: the bottom one's mb_skip_flag (seeing the top one as skipped)
        // and, if it is coded, the pair's flag come first.
        int bottom = CheckAddress(mbAddr + 1);
        _sliceOfMb[bottom] = slice.Number;
        _kind[mbAddr] = MbKind.PSkip;
        bool bottomSkipped = cabac.Decision(skipCtx + SkipFlagInc(bottom)) == 1;
        if (!bottomSkipped)
        {
            SetPairField(mbAddr, ReadFieldDecodingFlag(slice, r, bottom));
        }

        DecodeSkip(slice, mbAddr);
        if (bottomSkipped)
        {
            DecodeSkip(slice, bottom);
        }
        else
        {
            DecodeMacroblock(slice, r, bottom);
        }

        return bottom;
    }

    // ---- mb_skip_flag, mb_type, sub_mb_type (9.3.3.1.1.1, 9.3.3.1.1.3, 9.3.3.1.2) ----
    private int SkipFlagInc(int mbAddr)
    {
        int a = NeighborMb(mbAddr, -1, 0);
        int b = NeighborMb(mbAddr, 0, -1);
        return (a >= 0 && _kind[a] != MbKind.PSkip ? 1 : 0) + (b >= 0 && _kind[b] != MbKind.PSkip ? 1 : 0);
    }

    /// <summary>mb_type of a P slice: 0..4 inter (Table 7-13), 5.. intra (5 + Table 7-11).</summary>
    private int CabacPMbType(Cabac c, int mbAddr)
    {
        if (c.Decision(14) == 1)
        {
            return 5 + CabacIntraMbType(c, mbAddr, suffixOffset: 17);
        }

        // Prefix bins 000 P_L0_16x16, 001 P_8x8, 011 P_L0_L0_16x8, 010 P_L0_L0_8x16.
        if (c.Decision(15) == 0)
        {
            return c.Decision(16) == 0 ? 0 : 3;
        }

        return c.Decision(17) == 1 ? 1 : 2;
    }

    /// <summary>
    /// mb_type of an intra macroblock (Table 7-11), as the I-slice element (ctxIdxOffset 3) or as
    /// the suffix in a P or B slice (<paramref name="suffixOffset"/> 17 or 32; 0 for I slices).
    /// Bins: I_NxN 0; otherwise 1, the terminate bin (1 for I_PCM), then for I_16x16 the luma CBP
    /// flag, chroma CBP (0, 10, 11) and two prediction mode bits.
    /// </summary>
    private int CabacIntraMbType(Cabac c, int mbAddr, int suffixOffset = 0)
    {
        bool inPSlice = suffixOffset != 0; // P or B: the same bins and increments
        int offset = inPSlice ? suffixOffset : 3;
        int first;
        if (inPSlice)
        {
            first = c.Decision(offset);
        }
        else
        {
            int a = NeighborMb(mbAddr, -1, 0);
            int b = NeighborMb(mbAddr, 0, -1);
            int inc = (a >= 0 && _kind[a] != MbKind.Intra4x4 ? 1 : 0) + (b >= 0 && _kind[b] != MbKind.Intra4x4 ? 1 : 0);
            first = c.Decision(3 + inc);
        }

        if (first == 0)
        {
            return 0; // I_NxN
        }

        if (c.Terminate() == 1)
        {
            return 25; // I_PCM
        }

        // ctxIdxInc of bins 2.. (Table 9-39): I slices 3, 4, then 5 / 6 by bin 3, then 6 / 7, 7;
        // P suffix 1, 2, then 2 / 3 by bin 3, then 3, 3.
        int luma = c.Decision(offset + (inPSlice ? 1 : 3));
        int chroma = c.Decision(offset + (inPSlice ? 2 : 4));
        if (chroma != 0)
        {
            chroma += c.Decision(offset + (inPSlice ? 2 : 5));
        }

        int predMode = c.Decision(offset + (inPSlice ? 3 : 6)) << 1;
        predMode |= c.Decision(offset + (inPSlice ? 3 : 7));
        return 1 + predMode + (4 * chroma) + (12 * luma);
    }

    /// <summary>
    /// mb_type of a B slice: 0..22 inter (Table 7-14), 23.. intra (23 + Table 7-11), ctxIdxOffset
    /// 27. Bin 0 B_Direct_16x16; 10x B_L0 / B_L1_16x16; 11 and four bins for the rest, with 1101 the
    /// intra prefix, 1110 and 1111 B_L1_L0_8x16 and B_8x8, and a fifth bin for types 12 to 21.
    /// </summary>
    private int CabacBMbType(Cabac c, int mbAddr)
    {
        int Cond(int n) => n >= 0 && _kind[n] != MbKind.PSkip && !_directMb[n] ? 1 : 0;
        if (c.Decision(27 + Cond(NeighborMb(mbAddr, -1, 0)) + Cond(NeighborMb(mbAddr, 0, -1))) == 0)
        {
            return 0;
        }

        if (c.Decision(27 + 3) == 0)
        {
            return 1 + c.Decision(27 + 5);
        }

        int bits = c.Decision(27 + 4) << 3;
        bits |= c.Decision(27 + 5) << 2;
        bits |= c.Decision(27 + 5) << 1;
        bits |= c.Decision(27 + 5);
        return bits switch
        {
            < 8 => bits + 3,
            13 => 23 + CabacIntraMbType(c, mbAddr, suffixOffset: 32),
            14 => 11,
            15 => 22,
            _ => ((bits << 1) | c.Decision(27 + 5)) - 4,
        };
    }

    /// <summary>
    /// sub_mb_type of a B slice (Table 7-18), ctxIdxOffset 36: 0 B_Direct_8x8; 10x B_L0 / B_L1_8x8;
    /// 111 and two bins for L1 / Bi 4x4, or 11 and three bins for types 3 to 10.
    /// </summary>
    private static int CabacBSubMbType(Cabac c)
    {
        if (c.Decision(36) == 0)
        {
            return 0;
        }

        if (c.Decision(37) == 0)
        {
            return 1 + c.Decision(39);
        }

        int type = 3;
        if (c.Decision(38) == 1)
        {
            if (c.Decision(39) == 1)
            {
                return 11 + c.Decision(39);
            }

            type += 4;
        }

        type += 2 * c.Decision(39);
        type += c.Decision(39);
        return type;
    }

    /// <summary>sub_mb_type of a P slice: 1 P_L0_8x8, 00 P_L0_8x4, 011 P_L0_4x8, 010 P_L0_4x4.</summary>
    private static int CabacSubMbType(Cabac c)
    {
        if (c.Decision(21) == 1)
        {
            return 0;
        }

        if (c.Decision(22) == 0)
        {
            return 1;
        }

        return c.Decision(23) == 1 ? 2 : 3;
    }

    // ---- ref_idx_l0 and mvd_l0 (9.3.3.1.1.6, 9.3.3.1.1.7) ----

    /// <summary>ref_idx_lX of the partition whose top-left luma sample is (x, y) in the macroblock: unary, ctxIdxOffset 54.</summary>
    private int CabacRefIdx(Cabac c, int list, int mbAddr, int x, int y, int maxRef)
    {
        int RefInc(int nx, int ny)
        {
            var (n, xW, yW) = Neighbor(mbAddr, nx, ny);
            if (n < 0 || _kind[n] == MbKind.PSkip)
            {
                return 0;
            }

            // Direct predicted partitions count as 0, as do intra blocks (refIdx -1). A frame
            // macroblock sees a field neighbour's index halved (9.3.3.1.1.6).
            int part = (n * 4) + ((yW >> 3) * 2) + (xW >> 3);
            int threshold = _mbaff && _fieldMb[n] && !_fieldMb[mbAddr] ? 1 : 0;
            return !_direct[part] && _refIdx[list][part] > threshold ? 1 : 0;
        }

        int inc = RefInc(x - 1, y) + (2 * RefInc(x, y - 1));
        if (c.Decision(54 + inc) == 0)
        {
            return 0;
        }

        int value = 1;
        int ctxIdx = 54 + 4;
        while (c.Decision(ctxIdx) == 1)
        {
            value++;
            ctxIdx = 54 + 5;
            if (value > maxRef)
            {
                throw new InvalidDataException($"ref_idx_l0 exceeds {maxRef}.");
            }
        }

        return value;
    }

    /// <summary>
    /// One component of mvd_l0 for the partition at (x, y): UEG3 with signedValFlag 1 and uCoff 9,
    /// ctxIdxOffset 40 (horizontal) or 47 (vertical).
    /// </summary>
    private int CabacMvd(Cabac c, int list, int mbAddr, int x, int y, int component)
    {
        byte[] absMvd = _absMvd[list][component];
        int AbsMvd(int nx, int ny)
        {
            var (n, xW, yW) = Neighbor(mbAddr, nx, ny);
            if (n < 0)
            {
                return 0;
            }

            int value = absMvd[(n * 16) + ((yW >> 2) * 4) + (xW >> 2)]; // 0 for skipped and intra macroblocks

            // In MBAFF frames a vertical mvd of the other kind of macroblock is scaled to the
            // current one's (9.3.3.1.1.7).
            if (component == 1 && _mbaff && _fieldMb[n] != _fieldMb[mbAddr])
            {
                value = _fieldMb[mbAddr] ? value / 2 : value * 2;
            }

            return value;
        }

        int sum = AbsMvd(x - 1, y) + AbsMvd(x, y - 1);
        int offset = component == 0 ? 40 : 47;
        if (c.Decision(offset + (sum < 3 ? 0 : sum <= 32 ? 1 : 2)) == 0)
        {
            return 0;
        }

        // Truncated unary prefix up to 9 with ctxIdxInc 3, 4, 5, 6, 6, ..., then an EG3 suffix.
        int value = 1;
        while (value < 9 && c.Decision(offset + Math.Min(value + 2, 6)) == 1)
        {
            value++;
        }

        if (value == 9)
        {
            value += ExpGolombBypass(c, 3);
        }

        return c.Bypass() == 1 ? -value : value;
    }

    /// <summary>Records |mvd| over a partition for the contexts of later partitions.</summary>
    private void StoreAbsMvd(int list, int mbAddr, int x, int y, int w, int h, int mvdX, int mvdY)
    {
        byte absX = (byte)Math.Min(Math.Abs(mvdX), 127);
        byte absY = (byte)Math.Min(Math.Abs(mvdY), 127);
        for (int by = y / 4; by < (y + h) / 4; by++)
        {
            for (int bx = x / 4; bx < (x + w) / 4; bx++)
            {
                _absMvd[list][0][(mbAddr * 16) + (by * 4) + bx] = absX;
                _absMvd[list][1][(mbAddr * 16) + (by * 4) + bx] = absY;
            }
        }
    }

    /// <summary>The k-th order Exp-Golomb suffix of a UEGk binarization, all bypass bins (9.3.2.3).</summary>
    private static int ExpGolombBypass(Cabac c, int k)
    {
        int value = 0;
        while (c.Bypass() == 1)
        {
            value += 1 << k;
            k++;
            if (k > 24)
            {
                throw new InvalidDataException("Exp-Golomb suffix too long.");
            }
        }

        while (k-- > 0)
        {
            value += c.Bypass() << k;
        }

        return value;
    }

    // ---- mb_qp_delta, intra prediction modes, coded_block_pattern (9.3.3.1.1.4, .5, .8) ----

    /// <summary>mb_qp_delta: the mapped value of Table 9-3 in unary, ctxIdxOffset 60.</summary>
    private static int CabacQpDelta(SliceState slice, Cabac c)
    {
        if (c.Decision(60 + (slice.LastQpDeltaNonZero ? 1 : 0)) == 0)
        {
            return 0;
        }

        int mapped = 1;
        int ctxIdx = 62;
        while (c.Decision(ctxIdx) == 1)
        {
            mapped++;
            ctxIdx = 63;
            if (mapped > 52)
            {
                throw new InvalidDataException("mb_qp_delta is out of range.");
            }
        }

        return (mapped & 1) != 0 ? (mapped + 1) >> 1 : -(mapped >> 1);
    }

    /// <summary>prev_intra4x4_pred_mode_flag and rem_intra4x4_pred_mode (3 bins, least significant first): -1 for "use the predicted mode".</summary>
    private static int CabacIntra4x4Mode(Cabac c)
    {
        if (c.Decision(68) == 1)
        {
            return -1;
        }

        return c.Decision(69) | (c.Decision(69) << 1) | (c.Decision(69) << 2);
    }

    /// <summary>intra_chroma_pred_mode: truncated unary up to 3, ctxIdxOffset 64.</summary>
    private int CabacChromaPredMode(Cabac c, int mbAddr)
    {
        int Cond(int n) => n >= 0 && !IsInter(n) && _kind[n] != MbKind.IPcm && _chromaPredMode[n] != 0 ? 1 : 0;
        int inc = Cond(NeighborMb(mbAddr, -1, 0)) + Cond(NeighborMb(mbAddr, 0, -1));
        if (c.Decision(64 + inc) == 0)
        {
            return 0;
        }

        if (c.Decision(67) == 0)
        {
            return 1;
        }

        return c.Decision(67) == 0 ? 2 : 3;
    }

    /// <summary>coded_block_pattern: four luma bins (one per 8x8 block, ctxIdxOffset 73), then chroma as truncated unary up to 2 (77).</summary>
    private int CabacCbp(Cabac c, int mbAddr)
    {
        int luma = 0;
        for (int b8 = 0; b8 < 4; b8++)
        {
            int x = (b8 & 1) * 8;
            int y = (b8 >> 1) * 8;
            int LumaCond(int nx, int ny)
            {
                var (n, xW, yW) = Neighbor(mbAddr, nx, ny);
                if (n < 0 || _kind[n] == MbKind.IPcm)
                {
                    return 0;
                }

                int b8N = ((yW >> 3) * 2) + (xW >> 3);
                int coded = n == mbAddr ? luma : _kind[n] == MbKind.PSkip ? 0 : _cbp[n] & 15;
                return ((coded >> b8N) & 1) != 0 ? 0 : 1;
            }

            luma |= c.Decision(73 + LumaCond(x - 1, y) + (2 * LumaCond(x, y - 1))) << b8;
        }

        int a = NeighborMb(mbAddr, -1, 0);
        int b = NeighborMb(mbAddr, 0, -1);
        int ChromaCond(int n, int least)
        {
            if (n < 0 || _kind[n] == MbKind.PSkip)
            {
                return 0;
            }

            return _kind[n] == MbKind.IPcm || (_cbp[n] >> 4) >= least ? 1 : 0;
        }

        int chroma = 0;
        if (c.Decision(77 + ChromaCond(a, 1) + (2 * ChromaCond(b, 1))) == 1)
        {
            chroma = 1 + c.Decision(77 + 4 + ChromaCond(a, 2) + (2 * ChromaCond(b, 2)));
        }

        return luma | (chroma << 4);
    }

    // ---- Residual blocks (7.3.5.3.3, 9.3.3.1.1.9, 9.3.3.1.3) ----

    /// <summary>
    /// ctxIdxInc of coded_block_flag for a block of <paramref name="category"/> at 4x4 position
    /// (<paramref name="x"/>, <paramref name="y"/>) of the macroblock (chroma: of component <paramref name="component"/>).
    /// </summary>
    private int CodedBlockFlagInc(int mbAddr, BlockCategory category, int x, int y, int component)
    {
        bool chroma = category is BlockCategory.ChromaDc or BlockCategory.ChromaAc;
        int maxH = chroma ? _mbHeightC : 16;
        int maxW = chroma ? 8 : 16;
        bool currentIntra = !IsInter(mbAddr);

        int Cond(int px, int py)
        {
            var (n, xW, yW) = Neighbor(mbAddr, px, py, maxW, maxH);
            if (n < 0)
            {
                return currentIntra ? 1 : 0;
            }

            if (_kind[n] == MbKind.IPcm)
            {
                return 1;
            }

            int bx = xW >> 2;
            int by = yW >> 2;
            return category switch
            {
                BlockCategory.LumaDc => _kind[n] == MbKind.Intra16x16 ? _dcCoded[n] & 1 : 0,
                BlockCategory.ChromaDc => _kind[n] != MbKind.PSkip && (_cbp[n] >> 4) != 0 ? (_dcCoded[n] >> (1 + component)) & 1 : 0,
                BlockCategory.ChromaAc => _totalCoeffChroma[(n * 16) + (component * 8) + (by * 2) + bx] != 0 ? 1 : 0,
                _ => _totalCoeffLuma[(n * 16) + (by * 4) + bx] != 0 ? 1 : 0,
            };
        }

        int px = x * 4;
        int py = y * 4;
        return Cond(px - 1, py) + (2 * Cond(px, py - 1));
    }

    /// <summary>
    /// residual_block_cabac: coded_block_flag, the significance map and the levels, into
    /// <paramref name="block"/> through <paramref name="scan"/> (the caller clears it). Returns the
    /// number of nonzero coefficients. 8x8 blocks have no coded_block_flag in 4:2:0: it is
    /// inferred to be 1. Field macroblocks code the significance map with contexts of their own.
    /// </summary>
    private static int CabacResidualBlock(Cabac c, BlockCategory category, int codedBlockFlagInc, bool field, int maxNumCoeff, Span<int> block, ReadOnlySpan<byte> scan)
    {
        if (category == BlockCategory.Luma8x8)
        {
            return field
                ? c.ResidualBlock(-1, 436, 451, Significant8x8FieldInc, Last8x8Inc, 426, 4, maxNumCoeff, block, scan)
                : c.ResidualBlock(-1, 402, 417, Significant8x8Inc, Last8x8Inc, 426, 4, maxNumCoeff, block, scan);
        }

        int cat = (int)category;
        bool chromaDc = category == BlockCategory.ChromaDc;
        var inc = !chromaDc ? ScanPositionInc : maxNumCoeff == 4 ? ChromaDcInc : ChromaDc422Inc;
        return c.ResidualBlock(
            85 + CodedBlockFlagCatOffset[cat] + codedBlockFlagInc,
            (field ? 277 : 105) + SignificantCatOffset[cat],
            (field ? 338 : 166) + SignificantCatOffset[cat],
            inc,
            inc,
            227 + AbsLevelCatOffset[cat],
            chromaDc ? 3 : 4,
            maxNumCoeff,
            block,
            scan);
    }

    // ctxIdxInc of significant_coeff_flag and last_significant_coeff_flag in 4x4 blocks: the scan
    // position, for chroma DC Min(position / NumC8x8, 2) (NumC8x8 1 in 4:2:0, 2 in 4:2:2).
    private static ReadOnlySpan<byte> ScanPositionInc => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private static ReadOnlySpan<byte> ChromaDcInc => [0, 1, 2, 2];

    private static ReadOnlySpan<byte> ChromaDc422Inc => [0, 0, 1, 1, 2, 2, 2, 2];
}

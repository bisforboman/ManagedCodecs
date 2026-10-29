namespace ManagedCodecs.H264;

/// <summary>
/// Error concealment (<see cref="H264Decoder.ConcealErrors"/>): a slice whose data fails keeps the macroblocks decoded
/// before the failure, the picture's other slices go on, and every macroblock left missing is predicted from a reference
/// with motion guessed from its neighbours (<see cref="GuessMotion"/>), with no residual.
/// </summary>
internal sealed partial class PictureDecoder<TPixel>
{
    // The last macroblock of the current slice that was decoded to the end (MacroblockDone), -1 before the first.
    private int _lastDoneMb = -1;

    // What missing macroblocks are copied from: list 0 entry 0 of the picture's first slice that has one.
    private Picture? _concealSource;

    // QP_Y given to concealed macroblocks (deblocking reads it): the last slice's.
    private int _concealQp;

    // GuessMotion's candidates: zero motion and up to 16 neighbouring blocks'.
    private readonly ConcealMotion[] _concealCandidates = new ConcealMotion[17];

    /// <summary>Whether a slice whose data fails is concealed instead of failing the picture.</summary>
    public bool ConcealErrors { get; set; }

    /// <summary>The macroblocks of the current picture (or field) that <see cref="Conceal"/> filled in.</summary>
    public int ConcealedMbCount { get; private set; }

    private void BeginConcealment()
    {
        _lastDoneMb = -1;
        _concealSource = null;
        _concealQp = 0;
        ConcealedMbCount = 0;
    }

    /// <summary>slice_data, concealing its failure: what the slice decoded stays, the rest of it counts as missing.</summary>
    private void DecodeSliceConcealed(SliceHeader header, PictureParameterSet pps, BitReader r, IReadOnlyList<Picture> refList0, IReadOnlyList<Picture> refList1, MarkSnapshot marks)
    {
        int sliceNum = _slices.Count;
        _lastDoneMb = -1;
        _concealQp = Math.Clamp(pps.PicInitQp + header.SliceQpDelta, -_qpBdOffset, 51);
        if (_concealSource is null && refList0.Count > 0 && !refList0[0].IsNonExisting)
        {
            _concealSource = refList0[0];
        }

        try
        {
            DecodeSliceData(header, pps, r, refList0, refList1, marks);
        }
        catch (InvalidDataException)
        {
            // Macroblocks are marked as in the slice when they start: the one that failed, and any marked ahead of it
            // (the bottom of an MBAFF pair), are missing. Unmarked at once, so that the rows below them are neither
            // deblocked nor reported to other pictures until they are filled in.
            for (int mb = 0; mb < _mbCount; mb++)
            {
                if (_sliceOfMb[mb] == sliceNum && mb > _lastDoneMb)
                {
                    _sliceOfMb[mb] = -1;
                }
            }

            // Macroblocks count as decoded when they start: count the ones that are.
            int decoded = 0;
            for (int mb = Math.Max(_fieldParity, 0); mb < _mbCount; mb += MbStep)
            {
                decoded += _sliceOfMb[mb] >= 0 ? 1 : 0;
            }

            DecodedMbCount = decoded;
        }
    }

    /// <summary>
    /// Fills in every macroblock no slice decoded, in address order: from list 0 entry 0 of the picture's first P or B
    /// slice, else from <paramref name="fallback"/> (the latest reference), else mid-grey. Each becomes an inter
    /// macroblock with no coefficients (or an intra one, for grey), with motion guessed from its neighbours (in MBAFF
    /// frames zero motion from that picture), in a slice of its own, so that deblocking and later pictures' direct
    /// prediction read it as any other.
    /// </summary>
    public void Conceal(Picture? fallback)
    {
        var source = _concealSource ?? fallback;
        int sliceNum = _slices.Count;
        _slices.Add(_slices.Count > 0 ? _slices[^1] : new SliceInfo(0, 0, 0, 0, 0));
        for (int mb = Math.Max(_fieldParity, 0); mb < _mbCount; mb += MbStep)
        {
            if (_sliceOfMb[mb] >= 0)
            {
                continue;
            }

            ConcealMacroblock(mb, sliceNum, source);
            ConcealedMbCount++;
            MacroblockDone(mb);
        }

        DecodedMbCount = _fieldParity >= 0 ? _mbCount / 2 : _mbCount;
    }

    private void ConcealMacroblock(int mbAddr, int sliceNum, Picture? source)
    {
        _sliceOfMb[mbAddr] = sliceNum;
        _transform8x8[mbAddr] = false;
        _intra4x4Mode.AsSpan(mbAddr * 16, 16).Fill(-1);
        _cbp[mbAddr] = 0;
        _dcCoded[mbAddr] = 0;
        _chromaPredMode[mbAddr] = 0;
        _directMb[mbAddr] = false;
        _direct.AsSpan(mbAddr * 4, 4).Clear();
        ClearCounts(mbAddr);
        _qp[mbAddr] = _concealQp;
        for (int list = 0; list < 2; list++)
        {
            _mvX[list].AsSpan(mbAddr * 16, 16).Clear();
            _mvY[list].AsSpan(mbAddr * 16, 16).Clear();
            _refIdx[list].AsSpan(mbAddr * 4, 4).Fill(-1);
            _refId[list].AsSpan(mbAddr * 4, 4).Clear();
            _refPic[list].AsSpan(mbAddr * 4, 4).Clear();
            _absMvd[list][0].AsSpan(mbAddr * 16, 16).Clear();
            _absMvd[list][1].AsSpan(mbAddr * 16, 16).Clear();
        }

        var (lumaX, lumaY, lumaOut, lumaStride, chromaOut, chromaStride) = PartitionPlace(mbAddr, 0, 0);
        if (source is null)
        {
            _kind[mbAddr] = MbKind.Intra16x16;
            FillGrey(lumaOut, lumaStride, chromaOut, chromaStride);
            return;
        }

        // Outside MBAFF frames the motion is guessed from the neighbours (GuessMotion); in them, zero motion from
        // reference index 0, of a field macroblock the field of its own parity (8.4.2.1).
        _kind[mbAddr] = MbKind.Inter;
        var guess = _mbaff
            ? new ConcealMotion(0, 0, source.Id, (sbyte)(IsMbaffFieldMb(mbAddr) ? mbAddr & 1 : -1), IsMbaffFieldMb(mbAddr) ? source.Field(mbAddr & 1) : source, 0, 0)
            : GuessMotion(mbAddr, source, lumaX, lumaY);
        guess = guess with { RefParity = (sbyte)guess.Reference.Parity };
        int guessList = guess.List;
        _refIdx[guessList].AsSpan(mbAddr * 4, 4).Fill(guess.RefIdx);
        _refId[guessList].AsSpan(mbAddr * 4, 4).Fill(guess.RefId);
        _refParity[guessList].AsSpan(mbAddr * 4, 4).Fill(guess.RefParity);
        _refPic[guessList].AsSpan(mbAddr * 4, 4).Fill(guess.Reference);
        _mvX[guessList].AsSpan(mbAddr * 16, 16).Fill(guess.MvX);
        _mvY[guessList].AsSpan(mbAddr * 16, 16).Fill(guess.MvY);
        for (int c = 0; c < (_monochrome ? 1 : 3); c++)
        {
            var output = Picture.Plane<TPixel>(c).AsSpan(c == 0 ? lumaOut : chromaOut);
            PredictComponent(guess.Reference, c, mbAddr, lumaX, lumaY, 16, 16, guess.MvX, guess.MvY, output, c == 0 ? lumaStride : chromaStride);
        }
    }

    /// <summary>One motion a concealed macroblock may take: a list's reference (its index, id, parity and picture) and a vector.</summary>
    private readonly record struct ConcealMotion(int List, sbyte RefIdx, long RefId, sbyte RefParity, Picture Reference, short MvX, short MvY);

    /// <summary>
    /// The motion of a missing macroblock (in a frame or a field picture), by boundary matching: the candidates are zero
    /// motion from <paramref name="source"/> and the motion of every 4x4 block of an available inter neighbour (left,
    /// above, right, below: decoded, or concealed before it) along the edge it shares (list 0's, or list 1's where list 0
    /// is unused); each predicts the macroblock's luma, and the one whose outer samples differ least from the
    /// neighbours' samples next to them (the sum of absolute differences over the available edges) is taken, the
    /// earlier on a tie. Missing macroblocks are filled in address order, so the choice is the same however the
    /// picture's slices and the pictures' threads ran.
    /// </summary>
    private ConcealMotion GuessMotion(int mbAddr, Picture source, int lumaX, int lumaY)
    {
        Span<ConcealMotion> candidates = _concealCandidates;
        int count = 0;
        candidates[count++] = new ConcealMotion(0, 0, source.Id, 0, source, 0, 0);

        int step = MbStep;
        int column = _pairSlots ? _mbColumn[mbAddr >> 1] : _mbColumn[mbAddr];
        int row = _pairSlots ? _mbRow[mbAddr >> 1] : _mbRow[mbAddr];
        int rows = _mbCount / (step * _widthInMbs);
        int left = column > 0 ? mbAddr - step : -1;
        int above = row > 0 ? mbAddr - (step * _widthInMbs) : -1;
        int right = column < _widthInMbs - 1 ? mbAddr + step : -1;
        int below = row < rows - 1 ? mbAddr + (step * _widthInMbs) : -1;

        // The 4x4 blocks of each neighbour along the shared edge: its right column, bottom row, left column, top row.
        AddEdge(left, 3, 4, ref count);
        AddEdge(above, 12, 1, ref count);
        AddEdge(right, 0, 4, ref count);
        AddEdge(below, 0, 1, ref count);
        if (count == 1)
        {
            return candidates[0];
        }

        var (origin, stride) = LumaPlace(mbAddr);
        var plane = PlaneY;
        int best = 0;
        long bestCost = long.MaxValue;
        for (int i = 0; i < count; i++)
        {
            var candidate = candidates[i];
            PredictComponent(candidate.Reference, 0, mbAddr, lumaX, lumaY, 16, 16, candidate.MvX, candidate.MvY, _predictionL0, 16);
            long cost = 0;
            for (int k = 0; k < 16; k++)
            {
                cost += Available(left) ? Math.Abs(Sample(_predictionL0[k * 16]) - Sample(plane[origin + (k * stride) - 1])) : 0;
                cost += Available(right) ? Math.Abs(Sample(_predictionL0[(k * 16) + 15]) - Sample(plane[origin + (k * stride) + 16])) : 0;
                cost += Available(above) ? Math.Abs(Sample(_predictionL0[k]) - Sample(plane[origin - stride + k])) : 0;
                cost += Available(below) ? Math.Abs(Sample(_predictionL0[(15 * 16) + k]) - Sample(plane[origin + (16 * stride) + k])) : 0;
            }

            if (cost < bestCost)
            {
                (best, bestCost) = (i, cost);
            }
        }

        return candidates[best];

        void AddEdge(int neighbour, int first, int stepBlocks, ref int count)
        {
            if (!Available(neighbour) || IsIntra(neighbour))
            {
                return;
            }

            for (int k = 0; k < 4; k++)
            {
                int block = first + (k * stepBlocks);
                int part = (neighbour * 4) + ((block >> 3) * 2) + ((block & 3) >> 1);
                int list = _refIdx[0][part] >= 0 ? 0 : _refIdx[1][part] >= 0 ? 1 : -1;
                var reference = list >= 0 ? _refPic[list][part] : null;
                if (reference is null)
                {
                    continue;
                }

                var motion = new ConcealMotion(list, _refIdx[list][part], _refId[list][part], _refParity[list][part], reference, _mvX[list][(neighbour * 16) + block], _mvY[list][(neighbour * 16) + block]);
                bool seen = false;
                for (int j = 0; j < count && !seen; j++)
                {
                    seen = _concealCandidates[j].Reference == reference && _concealCandidates[j].MvX == motion.MvX && _concealCandidates[j].MvY == motion.MvY;
                }

                if (!seen)
                {
                    _concealCandidates[count++] = motion;
                }
            }
        }
    }

    private bool Available(int mbAddr) => mbAddr >= 0 && _sliceOfMb[mbAddr] >= 0;

    private static int Sample(TPixel value) => int.CreateTruncating(value);

    private void FillGrey(int lumaOut, int lumaStride, int chromaOut, int chromaStride)
    {
        var grey = TPixel.CreateTruncating(1 << (_bitDepth - 1));
        for (int y = 0; y < 16; y++)
        {
            PlaneY.AsSpan(lumaOut + (y * lumaStride), 16).Fill(grey);
        }

        if (_monochrome)
        {
            return;
        }

        int width = _chroma444 ? 16 : _mbWidthC;
        int height = _chroma444 ? 16 : _mbHeightC;
        for (int y = 0; y < height; y++)
        {
            PlaneCb.AsSpan(chromaOut + (y * chromaStride), width).Fill(grey);
            PlaneCr.AsSpan(chromaOut + (y * chromaStride), width).Fill(grey);
        }
    }
}

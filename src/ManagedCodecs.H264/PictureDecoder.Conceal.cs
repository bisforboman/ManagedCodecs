namespace ManagedCodecs.H264;

/// <summary>
/// Error concealment (<see cref="H264Decoder.ConcealErrors"/>): a slice whose data fails keeps the macroblocks decoded
/// before the failure, the picture's other slices go on, and every macroblock left missing becomes a copy of the
/// same place in one reference, with zero motion and no residual.
/// </summary>
internal sealed partial class PictureDecoder<TPixel>
{
    // The last macroblock of the current slice that was decoded to the end (MacroblockDone), -1 before the first.
    private int _lastDoneMb = -1;

    // What missing macroblocks are copied from: list 0 entry 0 of the picture's first slice that has one.
    private Picture? _concealSource;

    // QP_Y given to concealed macroblocks (deblocking reads it): the last slice's.
    private int _concealQp;

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
    /// Fills in every macroblock no slice decoded: from list 0 entry 0 of the picture's first P or B slice, else from
    /// <paramref name="fallback"/> (the latest reference), else mid-grey. Each becomes an inter macroblock with zero
    /// motion from that picture and no coefficients (or an intra one, for grey), in a slice of its own, so that
    /// deblocking and later pictures' direct prediction read it as any other.
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

        // Reference index 0: of a field macroblock in an MBAFF frame, the field of its own parity (8.4.2.1).
        _kind[mbAddr] = MbKind.Inter;
        var reference = IsMbaffFieldMb(mbAddr) ? source.Frame.Field(mbAddr & 1) : source;
        _refIdx[0].AsSpan(mbAddr * 4, 4).Fill(0);
        _refId[0].AsSpan(mbAddr * 4, 4).Fill(source.Id);
        _refParity[0].AsSpan(mbAddr * 4, 4).Fill((sbyte)reference.Parity);
        _refPic[0].AsSpan(mbAddr * 4, 4).Fill(reference);
        for (int c = 0; c < (_monochrome ? 1 : 3); c++)
        {
            var output = Picture.Plane<TPixel>(c).AsSpan(c == 0 ? lumaOut : chromaOut);
            PredictComponent(reference, c, mbAddr, lumaX, lumaY, 16, 16, 0, 0, output, c == 0 ? lumaStride : chromaStride);
        }
    }

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

    /// <summary>
    /// Whether the picture predicted from a damaged one (<see cref="Picture.Damaged"/>): any reference its
    /// macroblocks' motion names, once that reference is done.
    /// </summary>
    /// <remarks>
    /// A reference sets its flag before reporting its last rows final, so a picture that read those rows already waited
    /// long enough; the wait here is for pictures that read only upper rows (frame threading's tests do not tell).
    /// </remarks>
    public bool ReadsDamagedReference()
    {
        var seen = new HashSet<Picture>();
        for (int mb = Math.Max(_fieldParity, 0); mb < _mbCount; mb += MbStep)
        {
            for (int list = 0; list < 2; list++)
            {
                for (int part = 0; part < 4; part++)
                {
                    if (_refPic[list][(mb * 4) + part] is { } reference && seen.Add(reference))
                    {
                        reference.WaitForRow(int.MaxValue);
                        if (reference.Damaged)
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }
}

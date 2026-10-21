namespace ManagedCodecs.H264;

internal enum SliceType
{
    P = 0,
    B = 1,
    I = 2,
    SP = 3,
    SI = 4,
}

/// <summary>One ref_pic_list_modification operation (7.3.3.1).</summary>
internal readonly record struct RefPicListModification(int Idc, int Value);

/// <summary>One memory_management_control_operation (7.3.3.3).</summary>
internal readonly record struct MemoryManagementOperation(int Op, int DifferenceOfPicNums, int LongTermPicNum, int LongTermFrameIdx, int MaxLongTermFrameIdxPlus1);

/// <summary>slice_header (7.3.3): of frames (MBAFF frames included) and of field pictures.</summary>
internal sealed class SliceHeader
{
    /// <summary>first_mb_in_slice: in MBAFF frames it counts macroblock pairs (see <see cref="FirstMbAddr"/>).</summary>
    public int FirstMbInSlice { get; internal init; }

    /// <summary>MbaffFrameFlag (7-25): the frame codes macroblock pairs, each as a frame or a field pair.</summary>
    public bool Mbaff { get; internal init; }

    /// <summary>field_pic_flag: the picture is one field, the bottom one with bottom_field_flag.</summary>
    public bool FieldPic { get; internal init; }

    public bool BottomField { get; internal init; }

    /// <summary>The field's parity (0 top, 1 bottom) for a field picture, -1 for a frame.</summary>
    public int Parity => FieldPic ? (BottomField ? 1 : 0) : -1;

    /// <summary>
    /// The address of the slice's first macroblock: first_mb_in_slice * (1 + MbaffFrameFlag). A
    /// field picture's macroblocks are stored where the same macroblocks of an all-field MBAFF
    /// frame would be: field macroblock n at 2 * n + parity.
    /// </summary>
    public int FirstMbAddr => Mbaff ? FirstMbInSlice * 2 : FieldPic ? (FirstMbInSlice * 2) + Parity : FirstMbInSlice;

    public SliceType Type { get; internal init; }

    public int PpsId { get; internal init; }

    public int FrameNum { get; internal init; }

    public bool IsIdr { get; internal init; }

    public int NalRefIdc { get; internal init; }

    public int IdrPicId { get; internal init; }

    public int PicOrderCntLsb { get; internal init; }

    public int DeltaPicOrderCntBottom { get; internal init; }

    public int DeltaPicOrderCnt0 { get; internal init; }

    public int DeltaPicOrderCnt1 { get; internal init; }

    public int NumRefIdxL0Active { get; internal init; }

    public IReadOnlyList<RefPicListModification> RefPicListModificationL0 { get; internal init; } = [];

    /// <summary>pred_weight_table, for explicit weighted prediction; null otherwise.</summary>
    public PredWeightTable? Weights { get; internal init; }

    /// <summary>direct_spatial_mv_pred_flag of a B slice: spatial (true) or temporal direct prediction.</summary>
    public bool DirectSpatialMvPred { get; internal init; }

    public int NumRefIdxL1Active { get; internal init; }

    public IReadOnlyList<RefPicListModification> RefPicListModificationL1 { get; internal init; } = [];

    public bool NoOutputOfPriorPics { get; internal init; }

    public bool LongTermReference { get; internal init; }

    public bool AdaptiveRefPicMarking { get; internal init; }

    public IReadOnlyList<MemoryManagementOperation> MemoryManagement { get; internal init; } = [];

    public int CabacInitIdc { get; internal init; }

    public int SliceQpDelta { get; internal init; }

    public int DisableDeblockingFilterIdc { get; internal init; }

    public int SliceAlphaC0Offset { get; internal init; }

    public int SliceBetaOffset { get; internal init; }

    public static SliceHeader Parse(
        BitReader r,
        NalUnit nal,
        IReadOnlyDictionary<int, SequenceParameterSet> spss,
        IReadOnlyDictionary<int, PictureParameterSet> ppss,
        out SequenceParameterSet sps,
        out PictureParameterSet pps)
    {
        int firstMb = (int)r.ReadUe();
        int sliceTypeRaw = r.ReadUe(9, "slice_type");
        var type = (SliceType)(sliceTypeRaw % 5);
        int ppsId = r.ReadUe(255, "pic_parameter_set_id");
        if (!ppss.TryGetValue(ppsId, out pps!))
        {
            throw new InvalidDataException($"Slice refers to missing PPS {ppsId}.");
        }

        if (!spss.TryGetValue(pps.SpsId, out sps!))
        {
            throw new InvalidDataException($"PPS {ppsId} refers to missing SPS {pps.SpsId}.");
        }

        if (type is SliceType.SP or SliceType.SI)
        {
            throw new NotSupportedException($"{type} slices are not supported (Extended profile).");
        }

        bool isIdr = nal.Type == NalUnitType.IdrSlice;
        int frameNum = (int)r.ReadBits(sps.Log2MaxFrameNum);
        bool fieldPic = !sps.FrameMbsOnly && r.ReadFlag();
        bool bottomField = fieldPic && r.ReadFlag();
        if (fieldPic && type == SliceType.B)
        {
            throw new NotSupportedException("B slices in field pictures are not supported.");
        }

        bool mbaff = sps.MbAdaptiveFrameField && !fieldPic;

        int idrPicId = isIdr ? (int)r.ReadUe() : 0;

        int pocLsb = 0;
        int deltaBottom = 0;
        int delta0 = 0;
        int delta1 = 0;
        if (sps.PicOrderCntType == 0)
        {
            pocLsb = (int)r.ReadBits(sps.Log2MaxPicOrderCntLsb);
            if (pps.BottomFieldPicOrderInFramePresent && !fieldPic)
            {
                deltaBottom = r.ReadSe();
            }
        }
        else if (sps.PicOrderCntType == 1 && !sps.DeltaPicOrderAlwaysZero)
        {
            delta0 = r.ReadSe();
            if (pps.BottomFieldPicOrderInFramePresent && !fieldPic)
            {
                delta1 = r.ReadSe();
            }
        }

        if (pps.RedundantPicCntPresent && r.ReadUe() != 0)
        {
            throw new NotSupportedException("Redundant pictures are not supported.");
        }

        bool directSpatial = type == SliceType.B && r.ReadFlag(); // direct_spatial_mv_pred_flag
        PredWeightTable? weights = null;
        int numRefIdxL0 = pps.NumRefIdxL0DefaultActive;
        int numRefIdxL1 = pps.NumRefIdxL1DefaultActive;
        IReadOnlyList<RefPicListModification> modificationsL0 = [];
        IReadOnlyList<RefPicListModification> modificationsL1 = [];
        if (type is SliceType.P or SliceType.B)
        {
            if (r.ReadFlag())
            {
                // num_ref_idx_active_override_flag
                numRefIdxL0 = 1 + r.ReadUe(31, "num_ref_idx_l0_active_minus1");
                if (type == SliceType.B)
                {
                    numRefIdxL1 = 1 + r.ReadUe(31, "num_ref_idx_l1_active_minus1");
                }
            }

            modificationsL0 = ReadModifications(r); // ref_pic_list_modification_flag_l0 and its loop
            if (type == SliceType.B)
            {
                modificationsL1 = ReadModifications(r);
            }

            if ((type == SliceType.P && pps.WeightedPred) || (type == SliceType.B && pps.WeightedBipredIdc == 1))
            {
                weights = PredWeightTable.Parse(r, type == SliceType.B ? 2 : 1, numRefIdxL0, numRefIdxL1);
            }
        }

        bool noOutputOfPriorPics = false;
        bool longTermReference = false;
        bool adaptive = false;
        var operations = new List<MemoryManagementOperation>();
        if (nal.RefIdc != 0)
        {
            if (isIdr)
            {
                noOutputOfPriorPics = r.ReadFlag();
                longTermReference = r.ReadFlag();
            }
            else
            {
                adaptive = r.ReadFlag();
                while (adaptive)
                {
                    int op = r.ReadUe(6, "memory_management_control_operation");
                    if (op == 0)
                    {
                        break;
                    }

                    int difference = op is 1 or 3 ? (int)r.ReadUe() + 1 : 0;
                    int longTermPicNum = op == 2 ? (int)r.ReadUe() : 0;
                    int longTermFrameIdx = op is 3 or 6 ? (int)r.ReadUe() : 0;
                    int maxPlus1 = op == 4 ? (int)r.ReadUe() : 0;
                    operations.Add(new MemoryManagementOperation(op, difference, longTermPicNum, longTermFrameIdx, maxPlus1));
                }
            }
        }

        int cabacInitIdc = pps.EntropyCodingModeCabac && type != SliceType.I ? r.ReadUe(2, "cabac_init_idc") : 0;
        int qpDelta = r.ReadSe();
        int disableDeblocking = 0;
        int alpha = 0;
        int beta = 0;
        if (pps.DeblockingFilterControlPresent)
        {
            disableDeblocking = r.ReadUe(2, "disable_deblocking_filter_idc");
            if (disableDeblocking != 1)
            {
                alpha = r.ReadSe() * 2;
                beta = r.ReadSe() * 2;
            }
        }

        return new SliceHeader
        {
            FirstMbInSlice = firstMb,
            Mbaff = mbaff,
            FieldPic = fieldPic,
            BottomField = bottomField,
            Type = type,
            PpsId = ppsId,
            FrameNum = frameNum,
            IsIdr = isIdr,
            NalRefIdc = nal.RefIdc,
            IdrPicId = idrPicId,
            PicOrderCntLsb = pocLsb,
            DeltaPicOrderCntBottom = deltaBottom,
            DeltaPicOrderCnt0 = delta0,
            DeltaPicOrderCnt1 = delta1,
            DirectSpatialMvPred = directSpatial,
            Weights = weights,
            NumRefIdxL0Active = numRefIdxL0,
            NumRefIdxL1Active = numRefIdxL1,
            RefPicListModificationL0 = modificationsL0,
            RefPicListModificationL1 = modificationsL1,
            NoOutputOfPriorPics = noOutputOfPriorPics,
            LongTermReference = longTermReference,
            AdaptiveRefPicMarking = adaptive,
            MemoryManagement = operations,
            CabacInitIdc = cabacInitIdc,
            SliceQpDelta = qpDelta,
            DisableDeblockingFilterIdc = disableDeblocking,
            SliceAlphaC0Offset = alpha,
            SliceBetaOffset = beta,
        };
    }

    /// <summary>ref_pic_list_modification for one list (7.3.3.1): the flag, then operations up to idc 3.</summary>
    private static List<RefPicListModification> ReadModifications(BitReader r)
    {
        var modifications = new List<RefPicListModification>();
        if (!r.ReadFlag())
        {
            return modifications;
        }

        while (true)
        {
            int idc = r.ReadUe(5, "modification_of_pic_nums_idc");
            if (idc == 3)
            {
                return modifications;
            }

            if (idc > 2)
            {
                throw new InvalidDataException($"Invalid modification_of_pic_nums_idc {idc}.");
            }

            // abs_diff_pic_num_minus1 for 0 and 1, long_term_pic_num for 2.
            modifications.Add(new RefPicListModification(idc, (int)r.ReadUe()));
        }
    }
}

/// <summary>
/// pred_weight_table (7.3.3.2): weight and offset per list, reference index and component
/// (0 luma, 1 Cb, 2 Cr), with the defaults (2^denominator, 0) where a flag leaves them out.
/// </summary>
internal sealed class PredWeightTable
{
    private readonly int[][] _weights;
    private readonly int[][] _offsets;

    private PredWeightTable(int lumaLog2Denom, int chromaLog2Denom, int[][] weights, int[][] offsets)
    {
        LumaLog2Denom = lumaLog2Denom;
        ChromaLog2Denom = chromaLog2Denom;
        _weights = weights;
        _offsets = offsets;
    }

    public int LumaLog2Denom { get; }

    public int ChromaLog2Denom { get; }

    /// <summary>Weight for list <paramref name="list"/>, reference <paramref name="refIdx"/>, component <paramref name="component"/>.</summary>
    public int Weight(int list, int refIdx, int component) => _weights[list][(refIdx * 3) + component];

    public int Offset(int list, int refIdx, int component) => _offsets[list][(refIdx * 3) + component];

    public static PredWeightTable Parse(BitReader r, int lists, int numRefIdxL0, int numRefIdxL1)
    {
        int lumaDenom = r.ReadUe(7, "luma_log2_weight_denom");
        int chromaDenom = r.ReadUe(7, "chroma_log2_weight_denom");
        var weights = new int[2][];
        var offsets = new int[2][];
        for (int list = 0; list < 2; list++)
        {
            int count = list == 0 ? numRefIdxL0 : numRefIdxL1;
            weights[list] = new int[count * 3];
            offsets[list] = new int[count * 3];
            for (int i = 0; i < count; i++)
            {
                weights[list][i * 3] = 1 << lumaDenom;
                weights[list][(i * 3) + 1] = 1 << chromaDenom;
                weights[list][(i * 3) + 2] = 1 << chromaDenom;
                if (list >= lists)
                {
                    continue;
                }

                if (r.ReadFlag())
                {
                    // luma_weight_lX_flag
                    weights[list][i * 3] = ReadWeight(r, "luma_weight");
                    offsets[list][i * 3] = ReadWeight(r, "luma_offset");
                }

                if (r.ReadFlag())
                {
                    // chroma_weight_lX_flag
                    for (int j = 1; j <= 2; j++)
                    {
                        weights[list][(i * 3) + j] = ReadWeight(r, "chroma_weight");
                        offsets[list][(i * 3) + j] = ReadWeight(r, "chroma_offset");
                    }
                }
            }
        }

        return new PredWeightTable(lumaDenom, chromaDenom, weights, offsets);
    }

    /// <summary>Weights and offsets are se(v) in -128 .. 127 (8-bit video).</summary>
    private static int ReadWeight(BitReader r, string name)
    {
        int value = r.ReadSe();
        if (value is < -128 or > 127)
        {
            throw new InvalidDataException($"{name} {value} is out of range.");
        }

        return value;
    }
}

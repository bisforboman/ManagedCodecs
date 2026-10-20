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

/// <summary>slice_header (7.3.3) for frame (non-field) slices.</summary>
internal sealed class SliceHeader
{
    public int FirstMbInSlice { get; private init; }

    public SliceType Type { get; private init; }

    public int PpsId { get; private init; }

    public int FrameNum { get; private init; }

    public bool IsIdr { get; private init; }

    public int NalRefIdc { get; private init; }

    public int IdrPicId { get; private init; }

    public int PicOrderCntLsb { get; private init; }

    public int DeltaPicOrderCntBottom { get; private init; }

    public int DeltaPicOrderCnt0 { get; private init; }

    public int DeltaPicOrderCnt1 { get; private init; }

    public int NumRefIdxL0Active { get; private init; }

    public IReadOnlyList<RefPicListModification> RefPicListModificationL0 { get; private init; } = [];

    public bool NoOutputOfPriorPics { get; private init; }

    public bool LongTermReference { get; private init; }

    public bool AdaptiveRefPicMarking { get; private init; }

    public IReadOnlyList<MemoryManagementOperation> MemoryManagement { get; private init; } = [];

    public int SliceQpDelta { get; private init; }

    public int DisableDeblockingFilterIdc { get; private init; }

    public int SliceAlphaC0Offset { get; private init; }

    public int SliceBetaOffset { get; private init; }

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

        if (type is SliceType.B or SliceType.SP or SliceType.SI)
        {
            throw new NotSupportedException($"{type} slices are not supported (Constrained Baseline only).");
        }

        if (pps.EntropyCodingModeCabac)
        {
            throw new NotSupportedException("CABAC is not supported (Constrained Baseline only).");
        }

        if (!sps.FrameMbsOnly)
        {
            throw new NotSupportedException("Interlaced (field / MBAFF) coding is not supported.");
        }

        bool isIdr = nal.Type == NalUnitType.IdrSlice;
        int frameNum = (int)r.ReadBits(sps.Log2MaxFrameNum);
        int idrPicId = isIdr ? (int)r.ReadUe() : 0;

        int pocLsb = 0;
        int deltaBottom = 0;
        int delta0 = 0;
        int delta1 = 0;
        if (sps.PicOrderCntType == 0)
        {
            pocLsb = (int)r.ReadBits(sps.Log2MaxPicOrderCntLsb);
            if (pps.BottomFieldPicOrderInFramePresent)
            {
                deltaBottom = r.ReadSe();
            }
        }
        else if (sps.PicOrderCntType == 1 && !sps.DeltaPicOrderAlwaysZero)
        {
            delta0 = r.ReadSe();
            if (pps.BottomFieldPicOrderInFramePresent)
            {
                delta1 = r.ReadSe();
            }
        }

        if (pps.RedundantPicCntPresent && r.ReadUe() != 0)
        {
            throw new NotSupportedException("Redundant pictures are not supported.");
        }

        int numRefIdxL0 = pps.NumRefIdxL0DefaultActive;
        var modifications = new List<RefPicListModification>();
        if (type == SliceType.P)
        {
            if (r.ReadFlag()) // num_ref_idx_active_override_flag
            {
                numRefIdxL0 = 1 + r.ReadUe(31, "num_ref_idx_l0_active_minus1");
            }

            if (r.ReadFlag()) // ref_pic_list_modification_flag_l0
            {
                while (true)
                {
                    int idc = r.ReadUe(5, "modification_of_pic_nums_idc");
                    if (idc == 3)
                    {
                        break;
                    }

                    if (idc > 2)
                    {
                        throw new InvalidDataException($"Invalid modification_of_pic_nums_idc {idc}.");
                    }

                    // abs_diff_pic_num_minus1 for 0 and 1, long_term_pic_num for 2.
                    modifications.Add(new RefPicListModification(idc, (int)r.ReadUe()));
                }
            }

            if (pps.WeightedPred)
            {
                throw new NotSupportedException("Weighted prediction is not supported.");
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
            NumRefIdxL0Active = numRefIdxL0,
            RefPicListModificationL0 = modifications,
            NoOutputOfPriorPics = noOutputOfPriorPics,
            LongTermReference = longTermReference,
            AdaptiveRefPicMarking = adaptive,
            MemoryManagement = operations,
            SliceQpDelta = qpDelta,
            DisableDeblockingFilterIdc = disableDeblocking,
            SliceAlphaC0Offset = alpha,
            SliceBetaOffset = beta,
        };
    }
}

namespace ManagedCodecs.H264;

/// <summary>seq_parameter_set_rbsp (7.3.2.1.1), with the fields a frame decoder needs.</summary>
internal sealed class SequenceParameterSet
{
    public int ProfileIdc { get; private init; }

    public int ConstraintFlags { get; private init; }

    public int LevelIdc { get; private init; }

    public int Id { get; private init; }

    public int ChromaFormatIdc { get; private init; } = 1;

    public int BitDepthLuma { get; private init; } = 8;

    public int BitDepthChroma { get; private init; } = 8;

    public bool HasScalingMatrix { get; private init; }

    public int Log2MaxFrameNum { get; private init; }

    public int PicOrderCntType { get; private init; }

    public int Log2MaxPicOrderCntLsb { get; private init; }

    public bool DeltaPicOrderAlwaysZero { get; private init; }

    public int OffsetForNonRefPic { get; private init; }

    public int OffsetForTopToBottomField { get; private init; }

    public int[] OffsetForRefFrame { get; private init; } = [];

    public int MaxNumRefFrames { get; private init; }

    public bool GapsInFrameNumAllowed { get; private init; }

    public int WidthInMbs { get; private init; }

    public int HeightInMapUnits { get; private init; }

    public bool FrameMbsOnly { get; private init; }

    public bool Direct8x8Inference { get; private init; }

    /// <summary>Crop offsets in luma samples (already scaled by CropUnitX/Y).</summary>
    public (int Left, int Right, int Top, int Bottom) Crop { get; private init; }

    public int HeightInMbs => FrameMbsOnly ? HeightInMapUnits : HeightInMapUnits * 2;

    public int Width => (WidthInMbs * 16) - Crop.Left - Crop.Right;

    public int Height => (HeightInMbs * 16) - Crop.Top - Crop.Bottom;

    public static SequenceParameterSet Parse(BitReader r)
    {
        int profile = (int)r.ReadBits(8);
        int constraints = (int)r.ReadBits(8);
        int level = (int)r.ReadBits(8);
        int id = r.ReadUe(31, "seq_parameter_set_id");

        int chromaFormat = 1;
        int bitDepthLuma = 8;
        int bitDepthChroma = 8;
        bool scaling = false;
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            chromaFormat = r.ReadUe(3, "chroma_format_idc");
            if (chromaFormat == 3)
            {
                r.ReadFlag(); // separate_colour_plane_flag
            }

            bitDepthLuma = 8 + r.ReadUe(6, "bit_depth_luma_minus8");
            bitDepthChroma = 8 + r.ReadUe(6, "bit_depth_chroma_minus8");
            r.ReadFlag(); // qpprime_y_zero_transform_bypass_flag
            scaling = r.ReadFlag();
            if (scaling)
            {
                for (int i = 0; i < (chromaFormat != 3 ? 8 : 12); i++)
                {
                    if (r.ReadFlag())
                    {
                        SkipScalingList(r, i < 6 ? 16 : 64);
                    }
                }
            }
        }

        int log2MaxFrameNum = 4 + r.ReadUe(12, "log2_max_frame_num_minus4");
        int pocType = r.ReadUe(2, "pic_order_cnt_type");
        int log2MaxPocLsb = 0;
        bool deltaAlwaysZero = false;
        int offsetNonRef = 0;
        int offsetTopBottom = 0;
        int[] offsets = [];
        if (pocType == 0)
        {
            log2MaxPocLsb = 4 + r.ReadUe(12, "log2_max_pic_order_cnt_lsb_minus4");
        }
        else if (pocType == 1)
        {
            deltaAlwaysZero = r.ReadFlag();
            offsetNonRef = r.ReadSe();
            offsetTopBottom = r.ReadSe();
            offsets = new int[r.ReadUe(255, "num_ref_frames_in_pic_order_cnt_cycle")];
            for (int i = 0; i < offsets.Length; i++)
            {
                offsets[i] = r.ReadSe();
            }
        }

        int maxRefFrames = r.ReadUe(16, "max_num_ref_frames");
        bool gaps = r.ReadFlag();
        int widthInMbs = 1 + r.ReadUe(1023, "pic_width_in_mbs_minus1");
        int heightInMapUnits = 1 + r.ReadUe(1023, "pic_height_in_map_units_minus1");
        bool frameMbsOnly = r.ReadFlag();
        if (!frameMbsOnly)
        {
            r.ReadFlag(); // mb_adaptive_frame_field_flag
        }

        bool direct8x8 = r.ReadFlag();
        var crop = (0, 0, 0, 0);
        if (r.ReadFlag())
        {
            // Crop units for 4:2:0 frames (Table 6-1, equations 7-19 to 7-22).
            int unitX = chromaFormat is 1 or 2 ? 2 : 1;
            int unitY = (chromaFormat == 1 ? 2 : 1) * (frameMbsOnly ? 1 : 2);
            crop = ((int)r.ReadUe() * unitX, (int)r.ReadUe() * unitX, (int)r.ReadUe() * unitY, (int)r.ReadUe() * unitY);
        }

        // vui_parameters are not needed for decoding and are left unread.
        var sps = new SequenceParameterSet
        {
            ProfileIdc = profile,
            ConstraintFlags = constraints,
            LevelIdc = level,
            Id = id,
            ChromaFormatIdc = chromaFormat,
            BitDepthLuma = bitDepthLuma,
            BitDepthChroma = bitDepthChroma,
            HasScalingMatrix = scaling,
            Log2MaxFrameNum = log2MaxFrameNum,
            PicOrderCntType = pocType,
            Log2MaxPicOrderCntLsb = log2MaxPocLsb,
            DeltaPicOrderAlwaysZero = deltaAlwaysZero,
            OffsetForNonRefPic = offsetNonRef,
            OffsetForTopToBottomField = offsetTopBottom,
            OffsetForRefFrame = offsets,
            MaxNumRefFrames = maxRefFrames,
            GapsInFrameNumAllowed = gaps,
            WidthInMbs = widthInMbs,
            HeightInMapUnits = heightInMapUnits,
            FrameMbsOnly = frameMbsOnly,
            Direct8x8Inference = direct8x8,
            Crop = crop,
        };

        if (sps.Width <= 0 || sps.Height <= 0)
        {
            throw new InvalidDataException("Frame cropping removes the whole picture.");
        }

        return sps;
    }

    private static void SkipScalingList(BitReader r, int size)
    {
        int last = 8;
        int next = 8;
        for (int j = 0; j < size && next != 0; j++)
        {
            next = (last + r.ReadSe() + 256) % 256;
            last = next == 0 ? last : next;
        }
    }
}

/// <summary>pic_parameter_set_rbsp (7.3.2.2).</summary>
internal sealed class PictureParameterSet
{
    public int Id { get; private init; }

    public int SpsId { get; private init; }

    public bool EntropyCodingModeCabac { get; private init; }

    public bool BottomFieldPicOrderInFramePresent { get; private init; }

    public int NumSliceGroups { get; private init; }

    public int NumRefIdxL0DefaultActive { get; private init; }

    public int NumRefIdxL1DefaultActive { get; private init; }

    public bool WeightedPred { get; private init; }

    public int WeightedBipredIdc { get; private init; }

    public int PicInitQp { get; private init; }

    public int ChromaQpIndexOffset { get; private init; }

    public bool DeblockingFilterControlPresent { get; private init; }

    public bool ConstrainedIntraPred { get; private init; }

    public bool RedundantPicCntPresent { get; private init; }

    public bool Transform8x8Mode { get; private init; }

    public bool HasScalingMatrix { get; private init; }

    public int SecondChromaQpIndexOffset { get; private init; }

    public static PictureParameterSet Parse(BitReader r)
    {
        int id = r.ReadUe(255, "pic_parameter_set_id");
        int spsId = r.ReadUe(31, "seq_parameter_set_id");
        bool cabac = r.ReadFlag();
        bool bottomFieldPoc = r.ReadFlag();
        int sliceGroups = 1 + r.ReadUe(7, "num_slice_groups_minus1");
        if (sliceGroups > 1)
        {
            throw new NotSupportedException("Slice groups (FMO) are not supported.");
        }

        int refL0 = 1 + r.ReadUe(31, "num_ref_idx_l0_default_active_minus1");
        int refL1 = 1 + r.ReadUe(31, "num_ref_idx_l1_default_active_minus1");
        bool weighted = r.ReadFlag();
        int weightedBipred = (int)r.ReadBits(2);
        int qp = 26 + r.ReadSe();
        r.ReadSe(); // pic_init_qs_minus26 (SP/SI slices only)
        int chromaOffset = r.ReadSe();
        bool deblockingControl = r.ReadFlag();
        bool constrainedIntra = r.ReadFlag();
        bool redundant = r.ReadFlag();

        bool transform8x8 = false;
        bool scaling = false;
        int secondChromaOffset = chromaOffset;
        if (r.MoreRbspData())
        {
            transform8x8 = r.ReadFlag();
            scaling = r.ReadFlag(); // the lists themselves are not parsed; such streams are rejected
            if (!scaling)
            {
                secondChromaOffset = r.ReadSe();
            }
        }

        return new PictureParameterSet
        {
            Id = id,
            SpsId = spsId,
            EntropyCodingModeCabac = cabac,
            BottomFieldPicOrderInFramePresent = bottomFieldPoc,
            NumSliceGroups = sliceGroups,
            NumRefIdxL0DefaultActive = refL0,
            NumRefIdxL1DefaultActive = refL1,
            WeightedPred = weighted,
            WeightedBipredIdc = weightedBipred,
            PicInitQp = qp,
            ChromaQpIndexOffset = chromaOffset,
            DeblockingFilterControlPresent = deblockingControl,
            ConstrainedIntraPred = constrainedIntra,
            RedundantPicCntPresent = redundant,
            Transform8x8Mode = transform8x8,
            HasScalingMatrix = scaling,
            SecondChromaQpIndexOffset = secondChromaOffset,
        };
    }
}

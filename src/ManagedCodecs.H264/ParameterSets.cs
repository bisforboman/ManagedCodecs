namespace ManagedCodecs.H264;

/// <summary>seq_parameter_set_rbsp (7.3.2.1.1), with the fields a frame decoder needs.</summary>
internal sealed class SequenceParameterSet
{
    public int ProfileIdc { get; internal init; }

    public int ConstraintFlags { get; internal init; }

    public int LevelIdc { get; internal init; }

    public int Id { get; internal init; }

    public int ChromaFormatIdc { get; internal init; } = 1;

    public int BitDepthLuma { get; internal init; } = 8;

    public int BitDepthChroma { get; internal init; } = 8;

    /// <summary>The sequence's scaling matrix, or null for flat scaling (no seq_scaling_matrix_present_flag).</summary>
    public ScalingMatrix? Scaling { get; internal init; }

    public int Log2MaxFrameNum { get; internal init; }

    public int PicOrderCntType { get; internal init; }

    public int Log2MaxPicOrderCntLsb { get; internal init; }

    public bool DeltaPicOrderAlwaysZero { get; internal init; }

    public int OffsetForNonRefPic { get; internal init; }

    public int OffsetForTopToBottomField { get; internal init; }

    public int[] OffsetForRefFrame { get; internal init; } = [];

    public int MaxNumRefFrames { get; internal init; }

    public bool GapsInFrameNumAllowed { get; internal init; }

    public int WidthInMbs { get; internal init; }

    public int HeightInMapUnits { get; internal init; }

    public bool FrameMbsOnly { get; internal init; }

    /// <summary>mb_adaptive_frame_field_flag: frames may code each macroblock pair as two fields (MBAFF).</summary>
    public bool MbAdaptiveFrameField { get; internal init; }

    public bool Direct8x8Inference { get; internal init; }

    /// <summary>max_num_reorder_frames from the VUI's bitstream restriction, if it has one.</summary>
    public int? MaxNumReorderFrames { get; internal init; }

    /// <summary>max_dec_frame_buffering from the VUI's bitstream restriction, if it has one.</summary>
    public int? MaxDecFrameBuffering { get; internal init; }

    /// <summary>Crop offsets in luma samples (already scaled by CropUnitX/Y).</summary>
    public (int Left, int Right, int Top, int Bottom) Crop { get; internal init; }

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
        ScalingMatrix? scaling = null;
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
            if (r.ReadFlag())
            {
                // Lists the SPS leaves out fall back by rule A (Table 7-2).
                scaling = ScalingMatrix.Resolve(ScalingMatrix.ParseLists(r, chromaFormat != 3 ? 8 : 12), sequence: null);
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
        bool mbAdaptive = !frameMbsOnly && r.ReadFlag(); // mb_adaptive_frame_field_flag

        bool direct8x8 = r.ReadFlag();
        var crop = (0, 0, 0, 0);
        if (r.ReadFlag())
        {
            // Crop units for 4:2:0 frames (Table 6-1, equations 7-19 to 7-22).
            int unitX = chromaFormat is 1 or 2 ? 2 : 1;
            int unitY = (chromaFormat == 1 ? 2 : 1) * (frameMbsOnly ? 1 : 2);
            crop = ((int)r.ReadUe() * unitX, (int)r.ReadUe() * unitX, (int)r.ReadUe() * unitY, (int)r.ReadUe() * unitY);
        }

        // Of the VUI, only the bitstream restriction matters: it says how far output is reordered.
        (int Reorder, int Buffering)? restriction = r.ReadFlag() ? ReadVuiBitstreamRestriction(r) : null;

        var sps = new SequenceParameterSet
        {
            ProfileIdc = profile,
            ConstraintFlags = constraints,
            LevelIdc = level,
            Id = id,
            ChromaFormatIdc = chromaFormat,
            BitDepthLuma = bitDepthLuma,
            BitDepthChroma = bitDepthChroma,
            Scaling = scaling,
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
            MbAdaptiveFrameField = mbAdaptive,
            Direct8x8Inference = direct8x8,
            Crop = crop,
            MaxNumReorderFrames = restriction?.Reorder,
            MaxDecFrameBuffering = restriction?.Buffering,
        };

        if (sps.Width <= 0 || sps.Height <= 0)
        {
            throw new InvalidDataException("Frame cropping removes the whole picture.");
        }

        return sps;
    }

    /// <summary>vui_parameters (E.1.1), read up to and including bitstream_restriction; null when absent.</summary>
    private static (int Reorder, int Buffering)? ReadVuiBitstreamRestriction(BitReader r)
    {
        if (r.ReadFlag() && r.ReadBits(8) == 255)
        {
            r.ReadBits(32); // aspect_ratio_idc Extended_SAR: sar_width, sar_height
        }

        if (r.ReadFlag())
        {
            r.ReadFlag(); // overscan_appropriate_flag
        }

        if (r.ReadFlag())
        {
            r.ReadBits(4); // video_format, video_full_range_flag
            if (r.ReadFlag())
            {
                r.ReadBits(24); // colour_primaries, transfer_characteristics, matrix_coefficients
            }
        }

        if (r.ReadFlag())
        {
            r.ReadUe(); // chroma_sample_loc_type_top_field
            r.ReadUe(); // chroma_sample_loc_type_bottom_field
        }

        if (r.ReadFlag())
        {
            r.ReadBits(32); // num_units_in_tick
            r.ReadBits(32); // time_scale
            r.ReadFlag(); // fixed_frame_rate_flag
        }

        bool nalHrd = r.ReadFlag();
        if (nalHrd)
        {
            SkipHrdParameters(r);
        }

        bool vclHrd = r.ReadFlag();
        if (vclHrd)
        {
            SkipHrdParameters(r);
        }

        if (nalHrd || vclHrd)
        {
            r.ReadFlag(); // low_delay_hrd_flag
        }

        r.ReadFlag(); // pic_struct_present_flag
        if (!r.ReadFlag())
        {
            return null;
        }

        r.ReadFlag(); // motion_vectors_over_pic_boundaries_flag
        r.ReadUe(); // max_bytes_per_pic_denom
        r.ReadUe(); // max_bits_per_mb_denom
        r.ReadUe(); // log2_max_mv_length_horizontal
        r.ReadUe(); // log2_max_mv_length_vertical
        int reorder = r.ReadUe(16, "max_num_reorder_frames");
        int buffering = r.ReadUe(16, "max_dec_frame_buffering");
        return (reorder, buffering);
    }

    /// <summary>hrd_parameters (E.1.2).</summary>
    private static void SkipHrdParameters(BitReader r)
    {
        int cpbCount = 1 + r.ReadUe(31, "cpb_cnt_minus1");
        r.ReadBits(8); // bit_rate_scale, cpb_size_scale
        for (int i = 0; i < cpbCount; i++)
        {
            r.ReadUe(); // bit_rate_value_minus1
            r.ReadUe(); // cpb_size_value_minus1
            r.ReadFlag(); // cbr_flag
        }

        r.ReadBits(20); // initial_cpb_removal_delay_length_minus1 .. time_offset_length
    }
}

/// <summary>pic_parameter_set_rbsp (7.3.2.2).</summary>
internal sealed class PictureParameterSet
{
    public int Id { get; internal init; }

    public int SpsId { get; internal init; }

    public bool EntropyCodingModeCabac { get; internal init; }

    public bool BottomFieldPicOrderInFramePresent { get; internal init; }

    public int NumSliceGroups { get; internal init; }

    public int NumRefIdxL0DefaultActive { get; internal init; }

    public int NumRefIdxL1DefaultActive { get; internal init; }

    public bool WeightedPred { get; internal init; }

    public int WeightedBipredIdc { get; internal init; }

    public int PicInitQp { get; internal init; }

    public int ChromaQpIndexOffset { get; internal init; }

    public bool DeblockingFilterControlPresent { get; internal init; }

    public bool ConstrainedIntraPred { get; internal init; }

    public bool RedundantPicCntPresent { get; internal init; }

    public bool Transform8x8Mode { get; internal init; }

    /// <summary>
    /// The picture-level scaling lists (null entries absent, filled in by <see cref="ScalingMatrix.For"/>),
    /// or null without pic_scaling_matrix_present_flag.
    /// </summary>
    public byte[]?[]? ScalingLists { get; internal init; }

    public int SecondChromaQpIndexOffset { get; internal init; }

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
        byte[]?[]? scaling = null;
        int secondChromaOffset = chromaOffset;
        if (r.MoreRbspData())
        {
            transform8x8 = r.ReadFlag();
            if (r.ReadFlag())
            {
                // 6 + 2 * transform_8x8_mode_flag lists for 4:2:0; 4:4:4 would have 6 8x8 lists, but
                // it is rejected before any slice is decoded.
                scaling = ScalingMatrix.ParseLists(r, 6 + (transform8x8 ? 2 : 0));
            }

            secondChromaOffset = r.ReadSe();
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
            ScalingLists = scaling,
            SecondChromaQpIndexOffset = secondChromaOffset,
        };
    }
}

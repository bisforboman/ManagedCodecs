namespace ManagedCodecs.Containers;

/// <summary>
/// What a demuxer needs of an H.264 sequence parameter set (ITU-T H.264 7.3.2.1.1, E.1.1): the picture's size after
/// cropping, and the VUI's timing. The decoder reads the SPS whole (ManagedCodecs.H264's SequenceParameterSet); a test
/// checks that the two agree on every sample's SPS.
/// </summary>
/// <param name="Width">Width in luma samples, the cropping taken off.</param>
/// <param name="Height">Height in luma samples (a frame's), the cropping taken off.</param>
/// <param name="NumUnitsInTick">The VUI's num_units_in_tick; 0 without timing.</param>
/// <param name="TimeScale">The VUI's time_scale; 0 without timing.</param>
internal readonly record struct SpsInfo(int Width, int Height, uint NumUnitsInTick, uint TimeScale)
{
    /// <summary>
    /// Frames a second from the VUI's timing: time_scale / (2 num_units_in_tick), a frame being two fields' ticks (E.2.1),
    /// as FFmpeg's raw H.264 frames are timed; null without timing.
    /// </summary>
    public Rational? FrameRate => NumUnitsInTick > 0 && TimeScale > 0 ? Rational.Reduce(TimeScale, 2L * NumUnitsInTick) : null;

    /// <summary>
    /// Parses an SPS NAL unit (its header byte included, emulation prevention bytes still in); null for one it can't read
    /// (another NAL unit type, cut short, out of range).
    /// </summary>
    public static SpsInfo? Parse(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 4 || (nal[0] & 0x1F) != 7)
        {
            return null;
        }

        var reader = new RbspReader(nal[1..]);
        try
        {
            return Read(ref reader);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static SpsInfo Read(ref RbspReader r)
    {
        int profile = (int)r.Bits(8);
        r.Bits(8); // constraint_set flags, reserved_zero_2bits
        r.Bits(8); // level_idc
        r.Ue(31); // seq_parameter_set_id
        int chromaFormat = 1;
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            chromaFormat = r.Ue(3);
            if (chromaFormat == 3)
            {
                r.Bits(1); // separate_colour_plane_flag
            }

            r.Ue(6); // bit_depth_luma_minus8
            r.Ue(6); // bit_depth_chroma_minus8
            r.Bits(1); // qpprime_y_zero_transform_bypass_flag
            if (r.Bits(1) == 1)
            {
                // seq_scaling_list_present_flag for each list, and the list's delta_scale values (7.3.2.1.1.1).
                for (int i = 0; i < (chromaFormat != 3 ? 8 : 12); i++)
                {
                    if (r.Bits(1) == 1)
                    {
                        SkipScalingList(ref r, i < 6 ? 16 : 64);
                    }
                }
            }
        }

        r.Ue(12); // log2_max_frame_num_minus4
        int pocType = r.Ue(2);
        if (pocType == 0)
        {
            r.Ue(12); // log2_max_pic_order_cnt_lsb_minus4
        }
        else if (pocType == 1)
        {
            r.Bits(1); // delta_pic_order_always_zero_flag
            r.Se(); // offset_for_non_ref_pic
            r.Se(); // offset_for_top_to_bottom_field
            int cycle = r.Ue(255);
            for (int i = 0; i < cycle; i++)
            {
                r.Se(); // offset_for_ref_frame
            }
        }

        r.Ue(16); // max_num_ref_frames
        r.Bits(1); // gaps_in_frame_num_value_allowed_flag
        int widthInMbs = 1 + r.Ue(1023);
        int heightInMapUnits = 1 + r.Ue(1023);
        bool frameMbsOnly = r.Bits(1) == 1;
        if (!frameMbsOnly)
        {
            r.Bits(1); // mb_adaptive_frame_field_flag
        }

        r.Bits(1); // direct_8x8_inference_flag
        int width = widthInMbs * 16;
        int height = heightInMapUnits * 16 * (frameMbsOnly ? 1 : 2);
        if (r.Bits(1) == 1)
        {
            // Crop units (Table 6-1, 7-19 to 7-22): chroma samples, of 4:2:0 or 4:2:2; field pairs' rows twice.
            int unitX = chromaFormat is 1 or 2 ? 2 : 1;
            int unitY = (chromaFormat == 1 ? 2 : 1) * (frameMbsOnly ? 1 : 2);
            const int MaxCrop = 1024 * 16;
            width -= (r.Ue(MaxCrop) + r.Ue(MaxCrop)) * unitX;
            height -= (r.Ue(MaxCrop) + r.Ue(MaxCrop)) * unitY;
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("The SPS crops away the whole picture.");
        }

        uint units = 0;
        uint scale = 0;
        if (r.Bits(1) == 1)
        {
            // vui_parameters (E.1.1) up to the timing: aspect ratio, overscan, video signal type, chroma location.
            if (r.Bits(1) == 1 && r.Bits(8) == 255)
            {
                r.Bits(16); // sar_width
                r.Bits(16); // sar_height
            }

            if (r.Bits(1) == 1)
            {
                r.Bits(1); // overscan_appropriate_flag
            }

            if (r.Bits(1) == 1)
            {
                r.Bits(4); // video_format, video_full_range_flag
                if (r.Bits(1) == 1)
                {
                    r.Bits(24); // colour_primaries, transfer_characteristics, matrix_coefficients
                }
            }

            if (r.Bits(1) == 1)
            {
                r.Ue(5); // chroma_sample_loc_type_top_field
                r.Ue(5); // chroma_sample_loc_type_bottom_field
            }

            if (r.Bits(1) == 1)
            {
                units = r.Bits(32);
                scale = r.Bits(32);
            }
        }

        return new SpsInfo(width, height, units, scale);
    }

    // A scaling list's delta_scale values (7.3.2.1.1.1): read until the next scale is 0 (the rest repeat the last).
    private static void SkipScalingList(ref RbspReader r, int size)
    {
        int last = 8;
        int next = 8;
        for (int j = 0; j < size && next != 0; j++)
        {
            int delta = r.Se();
            next = (last + delta + 256) % 256;
            last = next == 0 ? last : next;
        }
    }

    /// <summary>Reads an RBSP's bits, most significant first, skipping emulation prevention bytes (0x000003).</summary>
    private ref struct RbspReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _byte;
        private int _bit;
        private int _zeros;

        public uint Bits(int count)
        {
            uint value = 0;
            for (int i = 0; i < count; i++)
            {
                value = (value << 1) | Bit();
            }

            return value;
        }

        public int Ue(int max)
        {
            int leadingZeros = 0;
            while (Bit() == 0)
            {
                if (++leadingZeros > 31)
                {
                    throw new InvalidDataException("An Exp-Golomb code too long.");
                }
            }

            long value = (1L << leadingZeros) - 1 + Bits(leadingZeros);
            return value <= max ? (int)value : throw new InvalidDataException("An SPS value out of range.");
        }

        public int Se()
        {
            int k = Ue(int.MaxValue);
            return (k & 1) != 0 ? (k + 1) / 2 : -(k / 2);
        }

        private uint Bit()
        {
            if (_bit == 0)
            {
                // An emulation prevention byte: 0x03 after two zero bytes.
                if (_zeros >= 2 && _byte < _data.Length && _data[_byte] == 3)
                {
                    _byte++;
                    _zeros = 0;
                }

                if (_byte >= _data.Length)
                {
                    throw new InvalidDataException("The SPS is cut short.");
                }
            }

            uint bit = (uint)(_data[_byte] >> (7 - _bit)) & 1;
            if (++_bit == 8)
            {
                _zeros = _data[_byte] == 0 ? _zeros + 1 : 0;
                _bit = 0;
                _byte++;
            }

            return bit;
        }
    }
}

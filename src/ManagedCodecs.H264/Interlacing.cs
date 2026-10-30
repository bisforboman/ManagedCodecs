namespace ManagedCodecs.H264;

/// <summary>
/// What the picture timing SEI (D.1.3) takes from the SPS's VUI: whether it starts with the HRD delays, their lengths,
/// and the clock timestamps' time_offset length. An SPS has one only with pic_struct_present_flag.
/// </summary>
internal sealed record PictureTimingLayout(bool HasDelays, int CpbRemovalDelayLength, int DpbOutputDelayLength, int TimeOffsetLength);

/// <summary>
/// The picture timing SEI's pic_struct (Table D-1), and its clock timestamps' ct_type values as FFmpeg keeps them: a set
/// of bits, 1 &lt;&lt; ct_type for each timestamp (0 progressive, 1 interlaced, 2 unknown).
/// </summary>
internal readonly record struct PictureTiming(int PicStruct, int CtTypes)
{
    // The largest pic_timing: two 32-bit delays and three full clock timestamps with 31-bit time offsets.
    private const int MaxPayload = 40;

    /// <summary>
    /// pic_timing (D.1.3) read with an SPS's layout, as FFmpeg reads it (<c>ff_h264_sei_process_picture_timing</c>): past
    /// the payload's end come zero bits. Null for a pic_struct above 8, which FFmpeg drops.
    /// </summary>
    public static PictureTiming? Read(byte[] payload, PictureTimingLayout layout)
    {
        var buffer = new byte[Math.Max(payload.Length, MaxPayload) + 8];
        payload.CopyTo(buffer, 0);
        var r = new BitReader(buffer, buffer.Length - 8);
        if (layout.HasDelays)
        {
            r.ReadBits(layout.CpbRemovalDelayLength);
            r.ReadBits(layout.DpbOutputDelayLength);
        }

        int picStruct = (int)r.ReadBits(4);
        if (picStruct > 8)
        {
            return null;
        }

        int ctTypes = 0;
        int timestamps = picStruct switch { <= 2 => 1, 3 or 4 or 7 => 2, _ => 3 }; // NumClockTS (Table D-1)
        for (int i = 0; i < timestamps; i++)
        {
            if (!r.ReadFlag())
            {
                continue; // clock_timestamp_flag
            }

            ctTypes |= 1 << (int)r.ReadBits(2);
            r.ReadBits(1 + 5); // nuit_field_based_flag, counting_type
            bool full = r.ReadFlag();
            r.ReadBits(1 + 1 + 8); // discontinuity_flag, cnt_dropped_flag, n_frames
            if (full)
            {
                r.ReadBits(6 + 6 + 5); // seconds_value, minutes_value, hours_value
            }
            else if (r.ReadFlag())
            {
                r.ReadBits(6); // seconds_value
                if (r.ReadFlag())
                {
                    r.ReadBits(6); // minutes_value
                    if (r.ReadFlag())
                    {
                        r.ReadBits(5); // hours_value
                    }
                }
            }

            r.ReadBits(layout.TimeOffsetLength); // time_offset
        }

        return new PictureTiming(picStruct, ctTypes);
    }

    /// <summary>
    /// The last picture timing message of an SEI NAL unit (7.3.2.3, its payload bytes), or null if it has none. As in
    /// FFmpeg (<c>ff_h264_sei_decode</c>), messages are read while more than two bytes are left, not starting 00 00,
    /// up to a payload that overruns the NAL unit; the RBSP's last byte (the stop bit's) is not part of them.
    /// </summary>
    public static byte[]? FindPayload(NalUnit nal)
    {
        var data = nal.Rbsp.AsSpan(0, nal.RbspLength);
        data = data[..(data.LastIndexOfAnyExcept((byte)0) is int last and >= 0 ? last : 0)];
        byte[]? found = null;
        int pos = 0;
        while (data.Length - pos > 2 && (data[pos] != 0 || data[pos + 1] != 0))
        {
            int type = 0;
            int size = 0;
            if (!ReadValue(data, ref pos, ref type) || !ReadValue(data, ref pos, ref size) || size > data.Length - pos)
            {
                break;
            }

            if (type == 1)
            {
                found = data.Slice(pos, size).ToArray();
            }

            pos += size;
        }

        return found;

        // payloadType or payloadSize: bytes of 255 add up until the first other byte.
        static bool ReadValue(ReadOnlySpan<byte> data, ref int pos, ref int value)
        {
            while (pos < data.Length)
            {
                byte b = data[pos++];
                value += b;
                if (b != 255)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>
/// A frame's interlacing as FFmpeg's H.264 decoder exports it (<c>h264_export_frame_props</c>, h264_slice.c), and the
/// field order its parser gives the picture (<c>parse_nal_units</c>, h264_parser.c). The spec leaves both to the
/// application: they are FFmpeg's rules, checked against <c>ffprobe</c>.
/// </summary>
internal readonly record struct Interlacing(bool Interlaced, bool TopFieldFirst, FieldOrder Order, bool CodedAsFields)
{
    /// <summary>
    /// A frame's flags: <paramref name="fieldOrMbaff"/> says whether it is a field (two field pictures) or an MBAFF frame,
    /// <paramref name="previousInterlaced"/> the previous frame's flag (true at the start), which pic_struct 3 and 4 on
    /// a progressive frame keep (soft telecine).
    /// </summary>
    public static (bool Interlaced, bool TopFieldFirst) Flags(PictureTiming? timing, bool fieldOrMbaff, bool previousInterlaced, int topPoc, int bottomPoc)
    {
        bool interlaced;
        if (timing is { } t)
        {
            interlaced = t.PicStruct switch
            {
                1 or 2 => true, // a top or a bottom field
                3 or 4 => fieldOrMbaff || previousInterlaced,
                _ => false, // a frame, or one repeating a field or itself (5 to 8)
            };

            // ct_type 0 or 1 (progressive or interlaced) of a timestamp overrides it for pic_struct up to 4.
            if ((t.CtTypes & 3) != 0 && t.PicStruct <= 4)
            {
                interlaced = (t.CtTypes & 2) != 0;
            }
        }
        else
        {
            interlaced = fieldOrMbaff;
        }

        bool topFieldFirst = topPoc != bottomPoc ? topPoc < bottomPoc
            : timing is { } u ? u.PicStruct is 3 or 5
            : interlaced;
        return (interlaced, topFieldFirst);
    }

    /// <summary>
    /// The field order FFmpeg's parser gives a picture: by pic_struct where there is one, else by the field order counts
    /// for a frame; a field after the other field of the same frame_num is that one's order, any other field is unknown.
    /// <paramref name="lastField"/> is the last field the parser saw (0 top, 1 bottom, -1 none) and its frame_num,
    /// which a frame picture leaves as they are.
    /// </summary>
    public static FieldOrder ParserOrder(SliceHeader header, PictureTiming? timing, int topPoc, int bottomPoc, ref (int Parity, int FrameNum) lastField)
    {
        if (header.FieldPic)
        {
            var order = lastField.Parity >= 0 && lastField.FrameNum == header.FrameNum && lastField.Parity != header.Parity
                ? (lastField.Parity == 0 ? FieldOrder.TT : FieldOrder.BB)
                : FieldOrder.Unknown;
            lastField = (header.Parity, header.FrameNum);
            return order;
        }

        if (timing is { } t)
        {
            return t.PicStruct switch
            {
                3 or 5 => FieldOrder.TT,
                4 or 6 => FieldOrder.BB,
                _ => FieldOrder.Progressive,
            };
        }

        return topPoc < bottomPoc ? FieldOrder.TT : topPoc > bottomPoc ? FieldOrder.BB : FieldOrder.Progressive;
    }
}

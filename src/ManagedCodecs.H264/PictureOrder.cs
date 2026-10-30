namespace ManagedCodecs.H264;

/// <summary>
/// Picture order count decoding for frames (H.264 clause 8.2.1): the display position of each
/// picture, from its slice header and the state left by earlier pictures.
/// </summary>
internal sealed class PictureOrder
{
    // Type 0: from the previous reference picture.
    private int _prevPicOrderCntMsb;
    private int _prevPicOrderCntLsb;

    // Types 1 and 2: from the previous picture.
    private int _prevFrameNumOffset;
    private int _prevFrameNum;

    /// <summary>
    /// PicOrderCnt of the picture whose first slice header is <paramref name="header"/> (a frame,
    /// or a field: its own order count), and the state update for the pictures after it. Call
    /// once per picture, in decoding order.
    /// </summary>
    public int Next(SliceHeader header, SequenceParameterSet sps)
    {
        bool mmco5 = header.MemoryManagement.Any(op => op.Op == 5);

        int top;
        int bottom;

        if (sps.PicOrderCntType == 0)
        {
            if (header.IsIdr)
            {
                _prevPicOrderCntMsb = 0;
                _prevPicOrderCntLsb = 0;
            }

            // 8-3: the most significant part follows the lsb across wraparounds.
            int maxLsb = 1 << sps.Log2MaxPicOrderCntLsb;
            int lsb = header.PicOrderCntLsb;
            int msb = _prevPicOrderCntMsb;
            if (lsb < _prevPicOrderCntLsb && _prevPicOrderCntLsb - lsb >= maxLsb / 2)
            {
                msb += maxLsb;
            }
            else if (lsb > _prevPicOrderCntLsb && lsb - _prevPicOrderCntLsb > maxLsb / 2)
            {
                msb -= maxLsb;
            }

            // A field picture's one order count is msb + lsb either way (8-4, 8-5).
            top = msb + lsb;
            bottom = header.FieldPic ? top : top + header.DeltaPicOrderCntBottom;
            if (header.NalRefIdc != 0)
            {
                // After memory_management_control_operation 5 the picture's own order count is
                // rebased to 0 (8.2.1), and that is what the next picture continues from (a field's: 0).
                _prevPicOrderCntMsb = mmco5 ? 0 : msb;
                _prevPicOrderCntLsb = mmco5 ? top - Math.Min(top, bottom) : lsb;
            }
        }
        else
        {
            int maxFrameNum = 1 << sps.Log2MaxFrameNum;
            int frameNumOffset = header.IsIdr ? 0
                : _prevFrameNum > header.FrameNum ? _prevFrameNumOffset + maxFrameNum
                : _prevFrameNumOffset;

            if (sps.PicOrderCntType == 1)
            {
                (top, bottom) = Type1(header, sps, frameNumOffset);
            }
            else
            {
                // 8-12: twice the frame number, one less for non-reference pictures.
                int temp = header.IsIdr ? 0 : (2 * (frameNumOffset + header.FrameNum)) - (header.NalRefIdc == 0 ? 1 : 0);
                top = temp;
                bottom = temp;
            }

            // After memory_management_control_operation 5, frame_num counts as 0.
            _prevFrameNumOffset = mmco5 ? 0 : frameNumOffset;
            _prevFrameNum = mmco5 ? 0 : header.FrameNum;
        }

        if (header.FieldPic)
        {
            // Type 1 gives a bottom field its offset from the top (8-10); the others the same value. After
            // memory_management_control_operation 5 the field's order count is rebased to 0, as a frame's smaller one.
            int decoded = header.BottomField ? bottom : top;
            int field = mmco5 ? 0 : decoded;
            Fields = (field, field);
            DecodedFields = (decoded, decoded);
            return field;
        }

        int poc = Math.Min(top, bottom);
        Fields = mmco5 ? (top - poc, bottom - poc) : (top, bottom);
        DecodedFields = (top, bottom);
        return mmco5 ? poc - Math.Min(top, bottom) : poc;
    }

    /// <summary>TopFieldOrderCnt and BottomFieldOrderCnt of the frame <see cref="Next"/> last returned PicOrderCnt for.</summary>
    public (int Top, int Bottom) Fields { get; private set; }

    /// <summary>
    /// <see cref="Fields"/> before memory_management_control_operation 5 rebased them: FFmpeg keeps these in its pictures'
    /// <c>field_poc</c>, which its field order flags compare.
    /// </summary>
    public (int Top, int Bottom) DecodedFields { get; private set; }

    /// <summary>
    /// PicOrderCnt of a frame inferred for a gap in frame_num (8.2.5.2), a reference frame without
    /// order count deltas. Types 1 and 2 derive it from <paramref name="frameNum"/> and move their
    /// state on, as for a decoded frame; type 0 has nothing to derive it from, so the frame gets 0
    /// and the state stays.
    /// </summary>
    public int NextInferred(int frameNum, SequenceParameterSet sps)
    {
        if (sps.PicOrderCntType == 0)
        {
            return 0;
        }

        int frameNumOffset = _prevFrameNum > frameNum ? _prevFrameNumOffset + (1 << sps.Log2MaxFrameNum) : _prevFrameNumOffset;
        int poc = sps.PicOrderCntType == 1
            ? Type1(frameNum, isReference: true, 0, 0, sps, frameNumOffset).Top
            : 2 * (frameNumOffset + frameNum);
        _prevFrameNumOffset = frameNumOffset;
        _prevFrameNum = frameNum;
        return poc;
    }

    /// <summary>8-6 to 8-11.</summary>
    private static (int Top, int Bottom) Type1(SliceHeader header, SequenceParameterSet sps, int frameNumOffset) =>
        Type1(header.FrameNum, header.NalRefIdc != 0, header.DeltaPicOrderCnt0, header.DeltaPicOrderCnt1, sps, frameNumOffset);

    private static (int Top, int Bottom) Type1(int frameNum, bool isReference, int delta0, int delta1, SequenceParameterSet sps, int frameNumOffset)
    {
        var offsets = sps.OffsetForRefFrame;
        int absFrameNum = offsets.Length != 0 ? frameNumOffset + frameNum : 0;
        if (!isReference && absFrameNum > 0)
        {
            absFrameNum--;
        }

        int expected = 0;
        if (absFrameNum > 0)
        {
            int cycleCount = (absFrameNum - 1) / offsets.Length;
            int inCycle = (absFrameNum - 1) % offsets.Length;
            expected = cycleCount * offsets.Sum();
            for (int i = 0; i <= inCycle; i++)
            {
                expected += offsets[i];
            }
        }

        if (!isReference)
        {
            expected += sps.OffsetForNonRefPic;
        }

        int top = expected + delta0;
        int bottom = top + sps.OffsetForTopToBottomField + delta1;
        return (top, bottom);
    }
}

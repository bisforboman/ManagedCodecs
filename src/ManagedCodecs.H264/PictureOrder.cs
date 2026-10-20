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
    /// PicOrderCnt of the frame whose first slice header is <paramref name="header"/>, and the
    /// state update for the pictures after it. Call once per picture, in decoding order.
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

            top = msb + lsb;
            bottom = top + header.DeltaPicOrderCntBottom;
            if (header.NalRefIdc != 0)
            {
                // After memory_management_control_operation 5 the picture's own order count is
                // rebased to 0 (8.2.1), and that is what the next picture continues from.
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

        int poc = Math.Min(top, bottom);
        return mmco5 ? poc - Math.Min(top, bottom) : poc;
    }

    /// <summary>8-6 to 8-11.</summary>
    private static (int Top, int Bottom) Type1(SliceHeader header, SequenceParameterSet sps, int frameNumOffset)
    {
        var offsets = sps.OffsetForRefFrame;
        int absFrameNum = offsets.Length != 0 ? frameNumOffset + header.FrameNum : 0;
        if (header.NalRefIdc == 0 && absFrameNum > 0)
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

        if (header.NalRefIdc == 0)
        {
            expected += sps.OffsetForNonRefPic;
        }

        int top = expected + header.DeltaPicOrderCnt0;
        int bottom = top + sps.OffsetForTopToBottomField + header.DeltaPicOrderCnt1;
        return (top, bottom);
    }
}

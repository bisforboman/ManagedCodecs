namespace ManagedCodecs.H264;

/// <summary>Direct prediction of B_Skip, B_Direct_16x16 and B_Direct_8x8 (H.264 clause 8.4.1.2).</summary>
internal sealed partial class PictureDecoder<TPixel>
{
    /// <summary>
    /// Derives and stores the motion of 8x8 blocks <paramref name="first"/> .. first + count - 1
    /// of a macroblock in direct mode, spatial or temporal as the slice says.
    /// </summary>
    private void DirectMotion(SliceState slice, int mbAddr, int first, int count)
    {
        if (slice.RefList1[0].IsNonExisting)
        {
            throw new InvalidDataException("The co-located picture is a frame inferred for a gap in frame_num (8.2.5.2).");
        }

        for (int i = first; i < first + count; i++)
        {
            _direct[(mbAddr * 4) + i] = true;
        }

        if (IsFieldMb(mbAddr))
        {
            Statistics.DirectField++;
            Statistics.TemporalField += slice.Header.DirectSpatialMvPred ? 0 : 1;
            FieldStatistics.TemporalDirect += _fieldParity >= 0 && !slice.Header.DirectSpatialMvPred ? 1 : 0;
        }

        if (slice.Header.DirectSpatialMvPred)
        {
            SpatialDirect(slice, mbAddr, first, count);
        }
        else
        {
            TemporalDirect(slice, mbAddr, first, count);
        }
    }

    /// <summary>
    /// 8.4.1.2.2: reference indices and motion vectors predicted from the neighbouring macroblocks
    /// (as for a 16x16 partition), set to zero motion in blocks whose co-located block is still.
    /// </summary>
    private void SpatialDirect(SliceState slice, int mbAddr, int first, int count)
    {
        int ref0 = SpatialDirectRefIdx(0, mbAddr);
        int ref1 = SpatialDirectRefIdx(1, mbAddr);
        (int X, int Y) mv0 = (0, 0);
        (int X, int Y) mv1 = (0, 0);
        bool zero = ref0 < 0 && ref1 < 0;
        if (zero)
        {
            ref0 = 0;
            ref1 = 0;
        }
        else
        {
            if (ref0 >= 0)
            {
                mv0 = PredictMv(0, mbAddr, 0, 0, 16, 16, ref0, PartitionShape.Other);
            }

            if (ref1 >= 0)
            {
                mv1 = PredictMv(1, mbAddr, 0, 0, 16, 16, ref1, PartitionShape.Other);
            }
        }

        // colZeroFlag of each block: a short-term co-located picture whose block barely moves,
        // from its own reference 0 (its motion as stored, not rescaled between frame and field).
        var colPic = slice.RefList1[0];
        int size = Sps.Direct8x8Inference ? 8 : 4;
        bool checkStill = !zero && slice.Marks.IsShortTerm(colPic) && (ref0 == 0 || ref1 == 0);
        uint colZero = 0;
        int blocks = 0;
        foreach (var (x, y) in DirectBlocks(first, count, size))
        {
            if (checkStill)
            {
                var col = CoLocated(colPic, mbAddr, x / 4, y / 4);
                colZero |= col.RefIdx == 0 && col.MvX is >= -1 and <= 1 && col.MvY is >= -1 and <= 1 ? 1u << blocks : 0;
            }

            blocks++;
        }

        // A whole macroblock whose blocks are all still or all not takes one motion a list.
        if (count == 4 && (colZero == 0 || colZero == (1u << blocks) - 1))
        {
            StoreSpatialMotion(slice, mbAddr, 0, 0, 16, ref0, ref1, mv0, mv1, colZero != 0);
            return;
        }

        int block = 0;
        foreach (var (x, y) in DirectBlocks(first, count, size))
        {
            StoreSpatialMotion(slice, mbAddr, x, y, size, ref0, ref1, mv0, mv1, ((colZero >> block++) & 1) != 0);
        }
    }

    /// <summary>Spatial direct motion of a size x size area: zero in a list whose reference is 0 where the co-located block is still.</summary>
    private void StoreSpatialMotion(SliceState slice, int mbAddr, int x, int y, int size, int ref0, int ref1, (int X, int Y) mv0, (int X, int Y) mv1, bool colZero)
    {
        if (ref0 >= 0)
        {
            bool still = ref0 == 0 && colZero;
            StoreMotion(slice, 0, mbAddr, x, y, size, size, ref0, still ? 0 : mv0.X, still ? 0 : mv0.Y);
        }

        if (ref1 >= 0)
        {
            bool still = ref1 == 0 && colZero;
            StoreMotion(slice, 1, mbAddr, x, y, size, size, ref1, still ? 0 : mv1.X, still ? 0 : mv1.Y);
        }
    }

    /// <summary>refIdxLX of spatial direct prediction: MinPositive over the neighbours A, B and C (or D).</summary>
    private int SpatialDirectRefIdx(int list, int mbAddr)
    {
        var a = MotionNeighbor(list, mbAddr, -1, 0, 0);
        var b = MotionNeighbor(list, mbAddr, 0, -1, 0);
        var c = MotionNeighbor(list, mbAddr, 16, -1, 0);
        if (!c.Available)
        {
            c = MotionNeighbor(list, mbAddr, -1, -1, 0);
        }

        return MinPositive(a.RefIdx, MinPositive(b.RefIdx, c.RefIdx));
    }

    private static int MinPositive(int x, int y) => x >= 0 && y >= 0 ? Math.Min(x, y) : Math.Max(x, y);

    /// <summary>
    /// 8.4.1.2.3: the co-located block's motion vector, scaled by the order count distances, gives
    /// both lists; list 0 refers to the picture the co-located block referred to. In MBAFF frames
    /// a field macroblock works with fields (its reference indices, the order counts of its
    /// parity), and the co-located motion is rescaled where it is of the other kind.
    /// </summary>
    private void TemporalDirect(SliceState slice, int mbAddr, int first, int count)
    {
        var colFrame = slice.RefList1[0];
        var refList0 = slice.RefList0;
        int size = Sps.Direct8x8Inference ? 8 : 4;
        bool field = IsMbaffFieldMb(mbAddr);
        bool fieldPicture = _fieldParity >= 0;
        int parity = mbAddr & 1;
        int currentPoc = !field && !fieldPicture ? Picture.Poc : parity == 0 ? Picture.TopPoc : Picture.BottomPoc;
        foreach (var (x, y) in DirectBlocks(first, count, size))
        {
            var col = CoLocated(colFrame, mbAddr, x / 4, y / 4);
            int frameIdx = 0;
            if (fieldPicture)
            {
                // List 0 holds fields: the field the co-located block referred to, or of a frame
                // it referred to, the field of the current parity.
                if (col.RefIdx >= 0)
                {
                    int refParity = col.Scale == VerticalScale.FrameToField ? parity : col.RefParity;
                    frameIdx = -1;
                    for (int i = 0; i < refList0.Count && frameIdx < 0; i++)
                    {
                        frameIdx = refList0[i].Id == col.RefId && refList0[i].Parity == refParity ? i : -1;
                    }

                    if (frameIdx < 0)
                    {
                        throw new InvalidDataException("Temporal direct: the co-located block's reference is not in list 0.");
                    }
                }
            }
            else if (col.RefIdx >= 0)
            {
                // The lowest index in list 0 of the picture the co-located block referred to.
                frameIdx = -1;
                for (int i = 0; i < refList0.Count && frameIdx < 0; i++)
                {
                    if (refList0[i].Id == col.RefId)
                    {
                        frameIdx = i;
                    }
                }

                if (frameIdx < 0)
                {
                    throw new InvalidDataException("Temporal direct: the co-located block's reference is not in list 0.");
                }
            }

            // A field macroblock names the field: the co-located field reference itself, or for
            // a frame one its field of the macroblock's parity (the lowest index of the frame's).
            int ref0 = !field ? frameIdx : (2 * frameIdx) + (col.RefParity >= 0 && col.RefParity != parity ? 1 : 0);
            int colMvY = col.Scale switch
            {
                VerticalScale.FrameToField => col.MvY / 2,
                VerticalScale.FieldToFrame => col.MvY * 2,
                _ => col.MvY,
            };

            var pic0 = field ? refList0[frameIdx].Field(parity ^ (ref0 & 1)) : refList0[frameIdx];
            var pic1 = field ? colFrame.Field(parity) : colFrame;
            int mv0X;
            int mv0Y;
            int mv1X;
            int mv1Y;
            if (slice.Marks.IsLongTerm(pic0) || pic1.Poc - pic0.Poc == 0)
            {
                (mv0X, mv0Y, mv1X, mv1Y) = (col.MvX, colMvY, 0, 0);
            }
            else
            {
                int tb = Math.Clamp(currentPoc - pic0.Poc, -128, 127);
                int td = Math.Clamp(pic1.Poc - pic0.Poc, -128, 127);
                int tx = (16384 + Math.Abs(td / 2)) / td;
                int distScaleFactor = Math.Clamp(((tb * tx) + 32) >> 6, -1024, 1023);
                mv0X = ((distScaleFactor * col.MvX) + 128) >> 8;
                mv0Y = ((distScaleFactor * colMvY) + 128) >> 8;
                mv1X = mv0X - col.MvX;
                mv1Y = mv0Y - colMvY;
            }

            StoreMotion(slice, 0, mbAddr, x, y, size, size, ref0, mv0X, mv0Y);
            StoreMotion(slice, 1, mbAddr, x, y, size, size, 0, mv1X, mv1Y);
        }
    }

    /// <summary>vertMvScale (Table 8-8): how the co-located vertical motion relates to the current macroblock's.</summary>
    private enum VerticalScale
    {
        OneToOne,
        FrameToField,
        FieldToFrame,
    }

    /// <summary>
    /// The co-located block's list 0 motion (or list 1 where it uses only that): reference index,
    /// vector, the referenced frame's Id and, for a field macroblock's reference, the field's parity.
    /// </summary>
    private readonly record struct CoLocatedMotion(int RefIdx, int MvX, int MvY, long RefId, int RefParity, VerticalScale Scale);

    /// <summary>
    /// The co-located block of 4x4 block (bx, by) in the first list 1 picture (8.4.1.2.1): its
    /// list 0 motion, or list 1 where it does not use list 0; refIdx -1 in intra macroblocks. With
    /// direct_8x8_inference_flag each 8x8 block takes the motion of its corner 4x4 block.
    /// </summary>
    /// <remarks>
    /// In MBAFF frames (Tables 8-6 and 8-8) a field macroblock looks at the co-located pair's
    /// macroblock of its parity if that pair is a field pair, else at the frame macroblock holding
    /// the corner's rows (Frm_To_Fld); a frame macroblock over a field pair looks at the field
    /// nearer in order count (Fld_To_Frm), at the rows of its own half.
    /// </remarks>
    private CoLocatedMotion CoLocated(Picture colPic, int mbAddr, int bx, int by)
    {
        if (Sps.Direct8x8Inference)
        {
            bx = (bx >> 1) * 3;
            by = (by >> 1) * 3;
        }

        // The co-located motion is at most a macroblock row below this one (in frame rows), and so are the co-located
        // picture's layout (CodedAsFields, Mbaff, FieldMb): with frame threading it may still be being decoded.
        colPic.WaitForMotion(FrameMbRow(mbAddr) + 1);

        int colMb = mbAddr;
        var scale = VerticalScale.OneToOne;
        var colFrame = colPic.Frame;
        if (_fieldParity >= 0)
        {
            // A field picture: the field RefPicList1[0] where it was decoded as a field (a field
            // picture, or a field pair of an MBAFF frame), else the frame macroblocks over the
            // field macroblock's rows (Frm_To_Fld).
            int n = mbAddr >> 1;
            if (colFrame.CodedAsFields || (colFrame.Mbaff && colFrame.FieldMb[2 * n]))
            {
                colMb = (2 * n) + colPic.Parity;
            }
            else
            {
                int yCol = by * 4;
                colMb = colFrame.Mbaff ? (2 * n) + (yCol >> 3) : (2 * _widthInMbs * (n / _widthInMbs)) + (n % _widthInMbs) + (_widthInMbs * (yCol >> 3));
                by = ((2 * yCol) % 16) >> 2;
                scale = VerticalScale.FrameToField;
                FieldStatistics.FieldOverFrame++;
            }
        }
        else if (!_mbaff && colFrame.CodedAsFields)
        {
            // A frame over a frame coded as two field pictures: the field nearer in order count,
            // at the rows of the frame macroblock's half of its pair (Fld_To_Frm).
            int parity = Math.Abs(Picture.Poc - colFrame.BottomPoc) > Math.Abs(Picture.Poc - colFrame.TopPoc) ? 0 : 1;
            int yCol = by * 4;
            colMb = (2 * ((_widthInMbs * (mbAddr / (2 * _widthInMbs))) + (mbAddr % _widthInMbs))) + parity;
            by = (2 * ((mbAddr / _widthInMbs) % 2)) + (yCol >> 3);
            scale = VerticalScale.FieldToFrame;
            FieldStatistics.FrameOverFields++;
        }
        else if (_mbaff && colPic.Mbaff)
        {
            int top = mbAddr & ~1;
            bool colField = colPic.FieldMb[top];
            if (colField != _fieldMb[mbAddr])
            {
                int yCol = by * 4;
                if (colField)
                {
                    // The field nearer in order count to the current frame; ties go to the bottom one.
                    int parity = Math.Abs(Picture.Poc - colPic.BottomPoc) > Math.Abs(Picture.Poc - colPic.TopPoc) ? 0 : 1;
                    colMb = top + parity;
                    by = (2 * (mbAddr & 1)) + (yCol >> 3);
                    scale = VerticalScale.FieldToFrame;
                }
                else
                {
                    colMb = top + (yCol >> 3);
                    by = ((2 * yCol) % 16) >> 2;
                    scale = VerticalScale.FrameToField;
                }

                Statistics.MixedCoLocated++;
            }
        }

        int part = (colMb * 4) + ((by >> 1) * 2) + (bx >> 1);
        int block = (colMb * 16) + (by * 4) + bx;
        int list = colPic.RefIdx[0][part] >= 0 ? 0 : 1;
        int refIdx = colPic.RefIdx[list][part];
        return refIdx < 0
            ? new CoLocatedMotion(-1, 0, 0, -1, -1, scale)
            : new CoLocatedMotion(refIdx, colPic.MvX[list][block], colPic.MvY[list][block], colPic.RefId[list][part], colPic.RefParity[list][part], scale);
    }

    /// <summary>Top-left luma positions in the macroblock of the size x size blocks of 8x8 blocks first .. first + count - 1.</summary>
    private static DirectBlockList DirectBlocks(int first, int count, int size) => new(first, count, size);

    /// <summary>The positions of <see cref="DirectBlocks"/>, enumerated without allocating.</summary>
    private struct DirectBlockList(int first, int count, int size)
    {
        private readonly int _perBlock = size == 8 ? 1 : 4;
        private int _index = -1;

        public (int X, int Y) Current { get; private set; }

        public readonly DirectBlockList GetEnumerator() => this;

        public bool MoveNext()
        {
            if (++_index >= count * _perBlock)
            {
                return false;
            }

            int block = first + (_index / _perBlock);
            int k = _index % _perBlock;
            Current = (((block & 1) * 8) + ((k & 1) * size), ((block >> 1) * 8) + ((k >> 1) * size));
            return true;
        }
    }

    /// <summary>Motion compensation of direct predicted 8x8 blocks, in the units their motion was derived in.</summary>
    private void PredictDirect(SliceState slice, int mbAddr, int first, int count)
    {
        // A whole macroblock whose blocks came out with one motion (as spatial direct's usually
        // do) predicts as one 16x16 partition: the same samples, for a quarter of the calls.
        if (count == 4 && HasUniformMotion(mbAddr))
        {
            PredictPartition(slice, mbAddr, 0, 0, 16, 16);
            return;
        }

        int size = Sps.Direct8x8Inference ? 8 : 4;
        foreach (var (x, y) in DirectBlocks(first, count, size))
        {
            PredictPartition(slice, mbAddr, x, y, size, size);
        }
    }
}

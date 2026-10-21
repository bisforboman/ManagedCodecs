namespace ManagedCodecs.H264;

/// <summary>
/// Reference picture management: list initialization and modification (8.2.4) and reference
/// marking (8.2.5), for frames and for field pictures. The list holds frames with any field
/// marked as used for reference; a field picture's lists are made of field views.
/// </summary>
internal sealed class ReferenceList
{
    private readonly List<Picture> _pictures = [];

    // MaxLongTermFrameIdx; -1 means "no long-term frame indices".
    private int _maxLongTermFrameIdx = -1;

    public void Clear()
    {
        foreach (var picture in _pictures)
        {
            picture.IsShortTermReference = false;
            picture.IsLongTermReference = false;
        }

        _pictures.Clear();
        _maxLongTermFrameIdx = -1;
    }

    /// <summary>True while <paramref name="picture"/> is marked as a reference, short or long term.</summary>
    public bool Contains(Picture picture) => _pictures.Contains(picture);

    /// <summary>RefPicList0 for a P slice of the current picture.</summary>
    public List<Picture> BuildList(SliceHeader header, SequenceParameterSet sps)
    {
        if (header.FieldPic)
        {
            return BuildFieldList(header, sps);
        }

        int maxFrameNum = 1 << sps.Log2MaxFrameNum;
        UpdateFrameNumWrap(header.FrameNum, maxFrameNum);

        // 8.2.4.2.1: short-term by descending PicNum, then long-term by ascending LongTermPicNum.
        var list = _pictures.Where(p => p.IsShortTermReference).OrderByDescending(p => p.FrameNumWrap)
            .Concat(_pictures.Where(p => p.IsLongTermReference).OrderBy(p => p.LongTermFrameIdx))
            .ToList();

        return Finish(list, header.NumRefIdxL0Active, header.RefPicListModificationL0, header, maxFrameNum);
    }

    /// <summary>RefPicList0 and RefPicList1 for a B slice of the current picture, whose order count is <paramref name="currentPoc"/>.</summary>
    public (List<Picture> L0, List<Picture> L1) BuildLists(SliceHeader header, SequenceParameterSet sps, int currentPoc)
    {
        int maxFrameNum = 1 << sps.Log2MaxFrameNum;
        UpdateFrameNumWrap(header.FrameNum, maxFrameNum);

        // 8.2.4.2.3: short-term pictures before the current one (latest first) and after it
        // (earliest first), in that order for list 0 and the other way round for list 1, then the
        // long-term pictures by ascending LongTermPicNum.
        var before = _pictures.Where(p => p.IsShortTermReference && p.Poc < currentPoc).OrderByDescending(p => p.Poc).ToList();
        var after = _pictures.Where(p => p.IsShortTermReference && p.Poc > currentPoc).OrderBy(p => p.Poc).ToList();
        var longTerm = _pictures.Where(p => p.IsLongTermReference).OrderBy(p => p.LongTermFrameIdx).ToList();
        var l0 = before.Concat(after).Concat(longTerm).ToList();
        var l1 = after.Concat(before).Concat(longTerm).ToList();
        if (l1.Count > 1 && l1.SequenceEqual(l0))
        {
            (l1[0], l1[1]) = (l1[1], l1[0]);
        }

        return (
            Finish(l0, header.NumRefIdxL0Active, header.RefPicListModificationL0, header, maxFrameNum),
            Finish(l1, header.NumRefIdxL1Active, header.RefPicListModificationL1, header, maxFrameNum));
    }

    /// <summary>
    /// RefPicList0 for a P slice of a field picture (8.2.4.2.2, 8.2.4.2.5): the frames with a
    /// field used for short-term reference by descending FrameNumWrap (the current frame's first
    /// field included), then those with a long-term field by LongTermFrameIdx, each set turned
    /// into fields alternately from the current field's parity and the other one.
    /// </summary>
    private List<Picture> BuildFieldList(SliceHeader header, SequenceParameterSet sps)
    {
        int maxFrameNum = 1 << sps.Log2MaxFrameNum;
        UpdateFrameNumWrap(header.FrameNum, maxFrameNum);
        int parity = header.Parity;
        var shortTerm = _pictures.Where(p => p.Field(0).IsShortTermReference || p.Field(1).IsShortTermReference).OrderByDescending(p => p.FrameNumWrap).ToList();
        var longTerm = _pictures.Where(p => p.Field(0).IsLongTermReference || p.Field(1).IsLongTermReference).OrderBy(p => p.LongTermFrameIdx).ToList();
        var list = AlternateFields(shortTerm, parity, f => f.IsShortTermReference)
            .Concat(AlternateFields(longTerm, parity, f => f.IsLongTermReference))
            .ToList();
        return Finish(list, header.NumRefIdxL0Active, header.RefPicListModificationL0, header, maxFrameNum);
    }

    /// <summary>
    /// 8.2.4.2.5: fields of the frames in order, starting with the current field's parity and
    /// alternating, each parity taking its next frame that has such a field marked; when one
    /// parity runs out, the other's remaining fields follow.
    /// </summary>
    private static IEnumerable<Picture> AlternateFields(List<Picture> frames, int parity, Func<Picture, bool> marked)
    {
        var same = frames.Select(f => f.Field(parity)).Where(marked).ToList();
        var other = frames.Select(f => f.Field(1 - parity)).Where(marked).ToList();
        int i = 0;
        int j = 0;
        while (i < same.Count || j < other.Count)
        {
            if (i < same.Count)
            {
                yield return same[i++];
            }

            if (j < other.Count)
            {
                yield return other[j++];
            }
        }
    }

    /// <summary>Truncates an initial list to the active count (8.2.4.2) and applies the modifications (8.2.4.3).</summary>
    private List<Picture> Finish(List<Picture> list, int count, IReadOnlyList<RefPicListModification> modifications, SliceHeader header, int maxFrameNum)
    {
        if (list.Count > count)
        {
            list.RemoveRange(count, list.Count - count);
        }

        if (modifications.Count > 0)
        {
            Modify(list, count, modifications, header, maxFrameNum);
        }

        if (list.Count == 0)
        {
            throw new InvalidDataException("Inter slice without any reference picture.");
        }

        return list;
    }

    /// <summary>8.2.4.3: moves the named pictures to the front, in order.</summary>
    private void Modify(List<Picture> list, int count, IReadOnlyList<RefPicListModification> modifications, SliceHeader header, int maxFrameNum)
    {
        // Field pictures number fields (8.2.4.1): CurrPicNum 2 * frame_num + 1, a field's PicNum
        // 2 * FrameNumWrap + 1 for the current parity and 2 * FrameNumWrap for the other; the
        // same for LongTermPicNum from LongTermFrameIdx.
        bool field = header.FieldPic;
        int maxPicNum = field ? 2 * maxFrameNum : maxFrameNum;
        int currPicNum = field ? (2 * header.FrameNum) + 1 : header.FrameNum;
        int picNumPred = currPicNum;
        var candidates = field
            ? _pictures.SelectMany(p => new[] { p.Field(0), p.Field(1) }).ToList()
            : _pictures;
        int PicNum(Picture p) => field ? (2 * p.Frame.FrameNumWrap) + (p.Parity == header.Parity ? 1 : 0) : p.FrameNumWrap;
        int LongTermPicNum(Picture p) => field ? (2 * p.Frame.LongTermFrameIdx) + (p.Parity == header.Parity ? 1 : 0) : p.LongTermFrameIdx;
        int refIdx = 0;

        // Work on a list one longer than the final one, as the spec does.
        var work = new List<Picture?>(list);
        while (work.Count < count + 1)
        {
            work.Add(null);
        }

        foreach (var modification in modifications)
        {
            Picture picture;
            Func<Picture?, bool> isSame;
            if (modification.Idc < 2)
            {
                int absDiff = modification.Value + 1;
                int picNumNoWrap = modification.Idc == 0 ? picNumPred - absDiff : picNumPred + absDiff;
                if (picNumNoWrap < 0)
                {
                    picNumNoWrap += maxPicNum;
                }
                else if (picNumNoWrap >= maxPicNum)
                {
                    picNumNoWrap -= maxPicNum;
                }

                picNumPred = picNumNoWrap;
                int picNum = picNumNoWrap > currPicNum ? picNumNoWrap - maxPicNum : picNumNoWrap;
                picture = candidates.FirstOrDefault(p => p.IsShortTermReference && PicNum(p) == picNum)
                    ?? throw new InvalidDataException($"Reference list modification names missing picture {picNum}.");
                isSame = p => p is { IsShortTermReference: true } && PicNum(p) == picNum;
            }
            else
            {
                int longTermPicNum = modification.Value;
                picture = candidates.FirstOrDefault(p => p.IsLongTermReference && LongTermPicNum(p) == longTermPicNum)
                    ?? throw new InvalidDataException($"Reference list modification names missing long-term picture {longTermPicNum}.");
                isSame = p => p is { IsLongTermReference: true } && LongTermPicNum(p) == longTermPicNum;
            }

            for (int c = count; c > refIdx; c--)
            {
                work[c] = work[c - 1];
            }

            work[refIdx++] = picture;
            int n = refIdx;
            for (int c = refIdx; c <= count; c++)
            {
                if (!isSame(work[c]))
                {
                    work[n++] = work[c];
                }
            }
        }

        list.Clear();
        foreach (var picture in work.Take(count))
        {
            if (picture is null)
            {
                break;
            }

            list.Add(picture);
        }
    }

    /// <summary>
    /// Marks a just-decoded reference field (8.2.5) of frame <paramref name="frame"/>. The second
    /// field of a pair whose first field is a short-term reference is marked the same, without the
    /// sliding window (8.2.5.3); otherwise as a picture of its own, the window counting frames.
    /// Of the adaptive operations only 1 (unmark a field) is supported in field pictures.
    /// </summary>
    public void MarkField(SliceHeader header, Picture frame, SequenceParameterSet sps, bool secondField)
    {
        int maxFrameNum = 1 << sps.Log2MaxFrameNum;
        UpdateFrameNumWrap(header.FrameNum, maxFrameNum);
        var field = frame.Field(header.Parity);
        if (header.IsIdr && header.LongTermReference)
        {
            throw new NotSupportedException("Long-term IDR field pictures are not supported.");
        }

        if (header.AdaptiveRefPicMarking)
        {
            int currPicNum = (2 * header.FrameNum) + 1;
            foreach (var op in header.MemoryManagement)
            {
                if (op.Op != 1)
                {
                    throw new NotSupportedException($"memory_management_control_operation {op.Op} in field pictures is not supported.");
                }

                int picNum = currPicNum - op.DifferenceOfPicNums;
                var target = _pictures.SelectMany(p => new[] { p.Field(0), p.Field(1) })
                    .FirstOrDefault(f => f.IsShortTermReference && (2 * f.Frame.FrameNumWrap) + (f.Parity == header.Parity ? 1 : 0) == picNum);
                if (target is not null)
                {
                    target.IsShortTermReference = false;
                    if (!target.HasReferenceField)
                    {
                        _pictures.Remove(target.Frame);
                    }
                }
            }
        }
        else if (!header.IsIdr && !(secondField && frame.Field(1 - header.Parity).IsShortTermReference))
        {
            SlidingWindow(sps);
        }

        if (header.IsIdr)
        {
            _maxLongTermFrameIdx = -1;
        }

        field.IsShortTermReference = true;
        if (!_pictures.Contains(frame))
        {
            _pictures.Add(frame);
        }
    }

    /// <summary>Marks the just-decoded reference picture (8.2.5).</summary>
    public void Mark(SliceHeader header, Picture current, SequenceParameterSet sps)
    {
        int maxFrameNum = 1 << sps.Log2MaxFrameNum;
        UpdateFrameNumWrap(header.FrameNum, maxFrameNum);

        if (header.IsIdr)
        {
            // All earlier references were dropped when the IDR picture started.
            if (header.LongTermReference)
            {
                MarkLongTerm(current, 0);
                _maxLongTermFrameIdx = 0;
            }
            else
            {
                current.IsShortTermReference = true;
                _maxLongTermFrameIdx = -1;
            }

            _pictures.Add(current);
            return;
        }

        bool currentIsLongTerm = false;
        if (header.AdaptiveRefPicMarking)
        {
            foreach (var op in header.MemoryManagement)
            {
                switch (op.Op)
                {
                    case 1:
                        Unmark(ShortTerm(header.FrameNum - op.DifferenceOfPicNums));
                        break;
                    case 2:
                        Unmark(_pictures.FirstOrDefault(p => p.IsLongTermReference && p.LongTermFrameIdx == op.LongTermPicNum));
                        break;
                    case 3:
                        var picture = ShortTerm(header.FrameNum - op.DifferenceOfPicNums);
                        if (picture is not null)
                        {
                            Unmark(_pictures.FirstOrDefault(p => p.IsLongTermReference && p.LongTermFrameIdx == op.LongTermFrameIdx && p != picture));
                            MarkLongTerm(picture, op.LongTermFrameIdx);
                        }

                        break;
                    case 4:
                        _maxLongTermFrameIdx = op.MaxLongTermFrameIdxPlus1 - 1;
                        foreach (var p in _pictures.Where(p => p.IsLongTermReference && p.LongTermFrameIdx > _maxLongTermFrameIdx).ToList())
                        {
                            Unmark(p);
                        }

                        break;
                    case 5:
                        foreach (var p in _pictures.ToList())
                        {
                            Unmark(p);
                        }

                        _maxLongTermFrameIdx = -1;
                        current.FrameNum = 0; // the picture counts as frame_num 0 from now on (8.2.1)
                        break;
                    case 6:
                        Unmark(_pictures.FirstOrDefault(p => p.IsLongTermReference && p.LongTermFrameIdx == op.LongTermFrameIdx));
                        MarkLongTerm(current, op.LongTermFrameIdx);
                        currentIsLongTerm = true;
                        break;
                }
            }
        }
        else
        {
            SlidingWindow(sps);
        }

        if (!currentIsLongTerm)
        {
            current.IsShortTermReference = true;
        }

        _pictures.Add(current);
    }

    /// <summary>
    /// Marks a frame inferred for a gap in frame_num (8.2.5.2) as a short-term reference, after
    /// the sliding window made room for it.
    /// </summary>
    public void MarkNonExisting(Picture picture, SequenceParameterSet sps)
    {
        UpdateFrameNumWrap(picture.FrameNum, 1 << sps.Log2MaxFrameNum);
        SlidingWindow(sps);
        picture.IsShortTermReference = true;
        _pictures.Add(picture);
    }

    /// <summary>
    /// Sliding window (8.2.5.3): when the frames (pairs, unpaired fields) marked as references
    /// fill the DPB, drops the short-term one with the smallest FrameNumWrap, both its fields.
    /// </summary>
    private void SlidingWindow(SequenceParameterSet sps)
    {
        if (_pictures.Count >= Math.Max(sps.MaxNumRefFrames, 1))
        {
            var oldest = _pictures.Where(p => p.Field(0).IsShortTermReference || p.Field(1).IsShortTermReference).MinBy(p => p.FrameNumWrap);
            Unmark(oldest);
        }
    }

    private Picture? ShortTerm(int picNum) =>
        _pictures.FirstOrDefault(p => p.IsShortTermReference && p.FrameNumWrap == picNum);

    private static void MarkLongTerm(Picture picture, int longTermFrameIdx)
    {
        picture.IsShortTermReference = false;
        picture.IsLongTermReference = true;
        picture.LongTermFrameIdx = longTermFrameIdx;
    }

    private void Unmark(Picture? picture)
    {
        if (picture is null)
        {
            return;
        }

        picture.IsShortTermReference = false;
        picture.IsLongTermReference = false;
        _pictures.Remove(picture);
    }

    /// <summary>FrameNumWrap (8.2.4.1); for frames PicNum equals FrameNumWrap.</summary>
    private void UpdateFrameNumWrap(int currentFrameNum, int maxFrameNum)
    {
        foreach (var picture in _pictures)
        {
            picture.FrameNumWrap = picture.FrameNum > currentFrameNum ? picture.FrameNum - maxFrameNum : picture.FrameNum;
        }
    }
}

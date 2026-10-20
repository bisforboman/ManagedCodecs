namespace ManagedCodecs.H264;

/// <summary>
/// Reference picture management for frames: list initialization and modification for P slices
/// (8.2.4) and reference marking (8.2.5).
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

    /// <summary>RefPicList0 for a P slice of the current picture.</summary>
    public List<Picture> BuildList(SliceHeader header, SequenceParameterSet sps)
    {
        int maxFrameNum = 1 << sps.Log2MaxFrameNum;
        UpdateFrameNumWrap(header.FrameNum, maxFrameNum);

        // 8.2.4.2.1: short-term by descending PicNum, then long-term by ascending LongTermPicNum.
        var list = _pictures.Where(p => p.IsShortTermReference).OrderByDescending(p => p.FrameNumWrap)
            .Concat(_pictures.Where(p => p.IsLongTermReference).OrderBy(p => p.LongTermFrameIdx))
            .ToList();

        int count = header.NumRefIdxL0Active;
        if (list.Count > count)
        {
            list.RemoveRange(count, list.Count - count);
        }

        if (header.RefPicListModificationL0.Count > 0)
        {
            Modify(list, header, maxFrameNum);
        }

        if (list.Count == 0)
        {
            throw new InvalidDataException("P slice without any reference picture.");
        }

        return list;
    }

    /// <summary>8.2.4.3: moves the named pictures to the front, in order.</summary>
    private void Modify(List<Picture> list, SliceHeader header, int maxFrameNum)
    {
        int count = header.NumRefIdxL0Active;
        int currPicNum = header.FrameNum;
        int picNumPred = currPicNum;
        int refIdx = 0;

        // Work on a list one longer than the final one, as the spec does.
        var work = new List<Picture?>(list);
        while (work.Count < count + 1)
        {
            work.Add(null);
        }

        foreach (var modification in header.RefPicListModificationL0)
        {
            Picture picture;
            Func<Picture?, bool> isSame;
            if (modification.Idc < 2)
            {
                int absDiff = modification.Value + 1;
                int picNumNoWrap = modification.Idc == 0 ? picNumPred - absDiff : picNumPred + absDiff;
                if (picNumNoWrap < 0)
                {
                    picNumNoWrap += maxFrameNum;
                }
                else if (picNumNoWrap >= maxFrameNum)
                {
                    picNumNoWrap -= maxFrameNum;
                }

                picNumPred = picNumNoWrap;
                int picNum = picNumNoWrap > currPicNum ? picNumNoWrap - maxFrameNum : picNumNoWrap;
                picture = _pictures.FirstOrDefault(p => p.IsShortTermReference && p.FrameNumWrap == picNum)
                    ?? throw new InvalidDataException($"Reference list modification names missing picture {picNum}.");
                isSame = p => p is { IsShortTermReference: true } && p.FrameNumWrap == picNum;
            }
            else
            {
                int longTermPicNum = modification.Value;
                picture = _pictures.FirstOrDefault(p => p.IsLongTermReference && p.LongTermFrameIdx == longTermPicNum)
                    ?? throw new InvalidDataException($"Reference list modification names missing long-term picture {longTermPicNum}.");
                isSame = p => p is { IsLongTermReference: true } && p.LongTermFrameIdx == longTermPicNum;
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
            // Sliding window (8.2.5.3): drop the oldest short-term picture when the DPB is full.
            if (_pictures.Count >= Math.Max(sps.MaxNumRefFrames, 1))
            {
                var oldest = _pictures.Where(p => p.IsShortTermReference).MinBy(p => p.FrameNumWrap);
                Unmark(oldest);
            }
        }

        if (!currentIsLongTerm)
        {
            current.IsShortTermReference = true;
        }

        _pictures.Add(current);
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

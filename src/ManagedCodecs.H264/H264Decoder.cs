using System.Runtime.InteropServices;

namespace ManagedCodecs.H264;

/// <summary>
/// H.264 decoder for Constrained Baseline, Main, High, High 10, High 4:2:2 and High 4:4:4 Predictive profile streams
/// (CAVLC or CABAC; I, P and B slices; 8, 9, 10, 12 or 14 bits; monochrome, 4:2:0, 4:2:2 or 4:4:4; lossless;
/// progressive or interlaced (MBAFF frames, field pictures)), taking MP4-style samples: length-prefixed NAL units, one
/// picture per sample. It can decode several pictures at once (<see cref="Threads"/>).
/// </summary>
/// <remarks>
/// Implemented from ITU-T H.264; output is bit-exact with any conforming decoder. Streams that use other features
/// (slice groups (FMO), data partitioning, separate colour planes, 11 or 13 bits, luma and chroma bit depths that
/// differ) are rejected with <see cref="NotSupportedException"/>. Frames come out in display order (picture order
/// count, 8.2.1), held back as far as the stream's max_num_reorder_frames says reordering can
/// reach; <see cref="Flush"/> returns the rest at the end.
/// </remarks>
public sealed class H264Decoder : IVideoDecoder
{
    private readonly Dictionary<int, SequenceParameterSet> _sps = [];
    private readonly Dictionary<int, PictureParameterSet> _pps = [];
    private readonly ReferenceList _references = new();
    private readonly int _lengthSize;

    // Pictures are reused once they are no longer references and no frame waiting for output
    // borrows them (output is a copy, see Crop, unless BorrowFrames), and so is the per-macroblock
    // state, as long as the picture size stays the same.
    private readonly List<Picture> _pictures = [];

    // The planes of copied frames, back from frames the caller disposed.
    private readonly VideoFramePool _framePool = new();
    private long _pictureCount;

    // One picture decoder per picture decoded at once (Threads), all for the current stream size and format; and
    // with threads, the pictures being decoded (or decoded and not yet output), in decoding order.
    private readonly List<IPictureDecoder> _decoders = [];
    private readonly Queue<PictureJob> _inFlight = new();
    private readonly int _threads = 1;

    // frame_num of the last reference picture (PrevRefFrameNum, 7.4.3); -1 before the first.
    private int _prevRefFrameNum = -1;

    private readonly PictureOrder _order = new();
    private readonly OutputQueue _output = new();

    // Whether the packet being parsed is to be decoded and not shown (Packet.Discard).
    private bool _discard;

    /// <param name="codecPrivate">The AVCDecoderConfigurationRecord ('avcC') from the container.</param>
    public H264Decoder(ReadOnlySpan<byte> codecPrivate)
    {
        var config = AvcConfiguration.Parse(codecPrivate);
        _lengthSize = config.LengthSize;
        foreach (var sps in config.Sps)
        {
            HandleNal(NalUnit.Parse(sps), current: null);
        }

        foreach (var pps in config.Pps)
        {
            HandleNal(NalUnit.Parse(pps), current: null);
        }
    }

    public H264Decoder(VideoStreamInfo stream)
        : this(stream.CodecPrivate.Span)
    {
    }

    /// <summary>
    /// Return frames that point into the decoder's own pictures instead of copies, which saves a
    /// copy of every frame (about 5% of the decoding time at 720p). A borrowed frame is valid only
    /// until the next call to <see cref="Decode"/> or <see cref="Flush"/>, which may decode a new
    /// picture into the same memory; copy what has to be kept longer. Its planes are the
    /// decoder's macroblock-aligned buffers (read rows through <see cref="VideoFrame.GetRow"/>, or
    /// with its offsets and strides), and they must not be written: later pictures predict from them.
    /// Frames of more than 8 bits are copies all the same (the decoder keeps their samples as 16-bit
    /// words, which a frame's byte planes cannot hold without one).
    /// </summary>
    public bool BorrowFrames { get; init; }

    /// <summary>
    /// How many pictures to decode at once, each on a thread of its own (frame threading): a picture is decoded while
    /// the ones before it are still being decoded, waiting only for the rows of theirs its motion vectors reach, so
    /// more threads decode faster on more cores, and give the same frames. 1, the default, decodes on the calling
    /// thread, as WebAssembly in a browser needs (it has no threads there). With more, <see cref="Decode"/> returns
    /// frames up to that many packets later (<see cref="Flush"/> returns the rest), and <see cref="BorrowFrames"/> is
    /// not supported.
    /// </summary>
    public int Threads
    {
        get => _threads;
        init => _threads = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "At least one thread is needed.");
    }

    /// <summary>For tests of frame threading: sleep up to this many milliseconds before each picture and after each macroblock row, at random.</summary>
    internal int RowDelay { get; init; }

    /// <summary>
    /// Decodes one packet, which holds one picture (MP4 sample), and returns the frames ready for
    /// display. A field picture is half a frame: the first of a pair gives no frame, the second
    /// the whole frame, with the first one's timestamp.
    /// </summary>
    /// <remarks>
    /// Frames that are copies own their samples; disposing one when done with it gives its
    /// planes back to the decoder for a later frame, which saves allocating (and collecting)
    /// them: about 4 to 7% of the decoding time at 720p.
    /// </remarks>
    public IReadOnlyList<VideoFrame> Decode(Packet packet) => Decode(packet.Data.Span, packet.Pts, packet.Discard);

    /// <summary>
    /// Decodes one picture's length-prefixed NAL units from <paramref name="data"/>, which need not outlive the call
    /// (the decoder copies what it keeps), as <see cref="Decode(Packet)"/> does; the frame that comes from it gets
    /// <paramref name="pts"/>.
    /// </summary>
    public IReadOnlyList<VideoFrame> Decode(ReadOnlySpan<byte> data, long pts) => Decode(data, pts, discard: false);

    /// <summary>A picture's NAL units; a <paramref name="discard"/> picture (<see cref="Packet.Discard"/>) is decoded and gives no frame.</summary>
    private List<VideoFrame> Decode(ReadOnlySpan<byte> data, long pts, bool discard)
    {
        if (BorrowFrames && Threads > 1)
        {
            throw new NotSupportedException("Borrowed frames with more than one thread are not supported.");
        }

        var frames = new List<VideoFrame>();
        _discard = discard;
        var job = ParsePicture(data, pts, frames);
        if (Threads == 1)
        {
            Run(job);
            Finish(job);
            Output(job, frames);
            return frames;
        }

        // A thread of its own, so that a picture always runs while the later ones that wait for it do: pictures
        // start in decoding order, and wait only for earlier ones. The front end goes on from the headers alone.
        job.Task = Task.Factory.StartNew(() => Run(job), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _inFlight.Enqueue(job);
        Finish(job);
        Drain(frames, all: false);
        return frames;
    }

    /// <summary>
    /// <see cref="Decode(Packet)"/> for asynchronous callers. With <see cref="Threads"/> above 1 it awaits what
    /// <see cref="Decode(Packet)"/> would block on: a free decoder (the oldest picture finishing, whose frames it then
    /// returns), and a first field finishing before its second starts. A change of picture size or format still waits
    /// for every picture being decoded, blocking. Cancelling stops the waiting, not pictures already started; the
    /// exceptions are those of <see cref="Decode(Packet)"/>, the decoding threads' as they were thrown.
    /// </summary>
    public async ValueTask<IReadOnlyList<VideoFrame>> DecodeAsync(Packet packet, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Threads == 1)
        {
            return Decode(packet);
        }

        var frames = new List<VideoFrame>();
        while (_inFlight.Count >= Threads)
        {
            await WaitForAsync(_inFlight.Peek().Task!, cancellationToken).ConfigureAwait(false);
            DrainOldest(frames);
        }

        frames.AddRange(Decode(packet));
        return frames;
    }

    /// <summary>
    /// <see cref="Flush"/> for asynchronous callers: awaits each picture still being decoded instead of blocking.
    /// Cancelled, it drops the frames of pictures it had already waited for (cancelling a flush abandons the stream).
    /// </summary>
    public async ValueTask<IReadOnlyList<VideoFrame>> FlushAsync(CancellationToken cancellationToken = default)
    {
        var frames = new List<VideoFrame>();
        while (_inFlight.TryPeek(out var job))
        {
            await WaitForAsync(job.Task!, cancellationToken).ConfigureAwait(false);
            DrainOldest(frames);
        }

        frames.AddRange(Flush());
        return frames;
    }

    /// <summary>
    /// Waits for a picture's thread to end, either way (what it threw comes out where its picture is output), or for
    /// cancellation.
    /// </summary>
    private static async Task WaitForAsync(Task task, CancellationToken cancellationToken)
    {
        await task.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!task.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public IReadOnlyList<VideoFrame> Flush()
    {
        var frames = new List<VideoFrame>();
        Drain(frames, all: true);
        if (_firstField is not null)
        {
            throw new NotSupportedException("A field without its second field (an unpaired field) is not supported.");
        }

        frames.AddRange(_output.Flush());
        return frames;
    }

    /// <summary>Outputs the decoded pictures at the front of the queue, in decoding order: those done, or all of them (waiting).</summary>
    private void Drain(List<VideoFrame> frames, bool all)
    {
        while (_inFlight.TryPeek(out var job) && (all || job.Task!.IsCompleted))
        {
            DrainOldest(frames);
        }
    }

    /// <summary>Waits for the oldest picture being decoded and outputs it; rethrows what decoding it threw.</summary>
    private void DrainOldest(List<VideoFrame> frames)
    {
        var job = _inFlight.Dequeue();
        job.Task!.GetAwaiter().GetResult();
        Output(job, frames);
    }

    /// <summary>Decodes the job's slices into its picture and deblocks it, then reports it done (or failed) to pictures that predict from it.</summary>
    private static void Run(PictureJob job)
    {
        var state = job.State;
        var view = state.Header.FieldPic ? state.Picture.Field(state.Header.Parity) : state.Picture;
        try
        {
            if (state.Decoder.RowDelay > 0)
            {
                Thread.Sleep(Random.Shared.Next(state.Decoder.RowDelay + 1)); // tests: pictures (and fields) start late
            }

            // A first field's Begin resets the whole frame's motion and macroblock state, both fields' slots: its second
            // field, which may run on a decoder of its own, starts after that.
            job.FirstField?.Begun.Task.GetAwaiter().GetResult();
            state.Decoder.Begin(state.Picture, state.Sps, state.Header.Parity, state.SecondField);
            job.Begun.SetResult();
            foreach (var slice in job.Slices)
            {
                state.Decoder.DecodeSlice(slice.Header, slice.Pps, slice.Reader, slice.List0, slice.List1, slice.Marks);
            }

            if (!state.Decoder.IsComplete)
            {
                throw new InvalidDataException("The picture is missing macroblocks (error concealment is not supported).");
            }

            state.Decoder.Deblock();
            view.ReportDone();
        }
        catch
        {
            job.Begun.TrySetResult(); // a second field waiting for it goes on, and fails on what it reads
            view.ReportFailed();
            throw;
        }
    }

    /// <summary>
    /// What follows a picture in decoding order, from its headers alone: reference marking, the frame order count of a
    /// frame whose second field this was, and the first field a second one is to pair with.
    /// </summary>
    private void Finish(PictureJob job)
    {
        var state = job.State;
        var picture = state.Picture;
        if (state.Header.NalRefIdc != 0)
        {
            if (state.Header.FieldPic)
            {
                _references.MarkField(state.Header, picture, state.Sps, state.SecondField);
            }
            else
            {
                _references.Mark(state.Header, picture, state.Sps);
            }

            // PrevRefFrameNum (7.4.3); after memory_management_control_operation 5 it is 0.
            _prevRefFrameNum = state.Header.MemoryManagement.Any(op => op.Op == 5) ? 0 : state.Header.FrameNum;
        }

        if (state.SecondField)
        {
            picture.Poc = Math.Min(picture.TopPoc, picture.BottomPoc);
        }

        _firstField = state.Header.FieldPic && !state.SecondField ? job : null;
    }

    /// <summary>The frame of a decoded picture into the output queue, and what comes out of it; a first field gives none (its frame comes with the second).</summary>
    private void Output(PictureJob job, List<VideoFrame> frames)
    {
        var state = job.State;
        if (state.Header.FieldPic && !state.SecondField)
        {
            return;
        }

        var picture = state.Picture;
        if (job.Discard)
        {
            // It still takes its place in the output order, so that the frames around it come out as they would with it.
            frames.AddRange(_output.Add(null, picture.Poc, job.Restart, ReorderDepth(state.Sps)));
            return;
        }

        bool borrow = BorrowFrames && picture.BitDepth == 8;
        var frame = borrow ? Borrow(picture, state.Sps) : Crop(picture, state.Sps);
        frame.Pts = job.Pts;
        frames.AddRange(_output.Add(frame, picture.Poc, job.Restart, ReorderDepth(state.Sps), borrow ? picture : null));
    }

    /// <summary>
    /// How many decoded frames can precede, in decoding order, a frame shown before them: the
    /// VUI's max_num_reorder_frames, else none for order count type 2 (which follows decoding
    /// order), else the whole DPB (A.3.1, the bound with no other information).
    /// </summary>
    private static int ReorderDepth(SequenceParameterSet sps)
    {
        if (sps.MaxNumReorderFrames is int reorder)
        {
            return reorder;
        }

        if (sps.PicOrderCntType == 2)
        {
            return 0;
        }

        if (sps.MaxDecFrameBuffering is int buffering)
        {
            return buffering;
        }

        // MaxDpbMbs of Table A-1 by level_idc; level 1b counts as 1.1, the larger.
        int maxDpbMbs = sps.LevelIdc switch
        {
            <= 10 => 396,
            11 => 900,
            <= 20 => 2376,
            21 => 4752,
            <= 30 => 8100,
            31 => 18000,
            32 => 20480,
            <= 41 => 32768,
            42 => 34816,
            50 => 110400,
            <= 52 => 184320,
            _ => 696320,
        };
        return Math.Clamp(maxDpbMbs / (sps.WidthInMbs * sps.HeightInMbs), 1, 16);
    }

    /// <summary>Parses a packet's NAL units: parameter sets, and the slices of its picture with their reference lists.</summary>
    private PictureJob ParsePicture(ReadOnlySpan<byte> packet, long pts, List<VideoFrame> frames)
    {
        PictureJob? current = null;
        int pos = 0;
        while (pos < packet.Length)
        {
            if (pos + _lengthSize > packet.Length)
            {
                throw new InvalidDataException("Truncated NAL length prefix.");
            }

            int length = 0;
            for (int i = 0; i < _lengthSize; i++)
            {
                length = (length << 8) | packet[pos + i];
            }

            pos += _lengthSize;
            if (length <= 0 || pos + length > packet.Length)
            {
                throw new InvalidDataException($"NAL unit length {length} overruns the packet.");
            }

            current = HandleNal(NalUnit.Parse(packet.Slice(pos, length)), current, pts, frames);
            pos += length;
        }

        return current ?? throw new InvalidDataException("The packet contains no picture.");
    }

    // frames: where pictures output while waiting for a free decoder go.
    private PictureJob? HandleNal(NalUnit nal, PictureJob? current, long pts = 0, List<VideoFrame>? frames = null)
    {
        switch (nal.Type)
        {
            case NalUnitType.Sps:
                {
                    var sps = SequenceParameterSet.Parse(nal.CreateReader());
                    _sps[sps.Id] = sps;
                    break;
                }

            case NalUnitType.Pps:
                {
                    var pps = PictureParameterSet.Parse(nal.CreateReader(), _sps);
                    _pps[pps.Id] = pps;
                    break;
                }

            case NalUnitType.Slice or NalUnitType.IdrSlice:
                {
                    var reader = nal.CreateReader();
                    var header = SliceHeader.Parse(reader, nal, _sps, _pps, out var sps, out var pps);
                    CheckSupported(sps);

                    if (current is null)
                    {
                        current = StartPicture(header, sps, pts, frames!);
                    }
                    else if (header.FrameNum != current.State.Header.FrameNum || current.State.Sps != sps)
                    {
                        throw new NotSupportedException("A packet holding more than one picture is not supported.");
                    }

                    var (list0, list1) = header.Type switch
                    {
                        SliceType.P => (_references.BuildList(header, sps), []),
                        SliceType.B => _references.BuildLists(header, sps, current.State.Poc),
                        _ => (new List<Picture>(), new List<Picture>()),
                    };
                    foreach (var picture in list0.Concat(list1))
                    {
                        current.Uses.Add(picture.Frame);
                    }

                    current.Slices.Add(new SliceWork(header, pps, reader, list0, list1, new MarkSnapshot(list0.Concat(list1))));
                    break;
                }

            case >= NalUnitType.SliceDataPartitionA and <= (NalUnitType)4:
                throw new NotSupportedException("Data partitioning is not supported.");

            default:
                break; // SEI, access unit delimiters, filler data and the like carry no picture data
        }

        return current;
    }

    /// <summary>How often MBAFF frames used field and frame pairs and mixed them, so tests can check samples exercise them.</summary>
    internal MbaffStatistics MbaffStatistics { get; } = new();

    private PictureJob StartPicture(SliceHeader header, SequenceParameterSet sps, long pts, List<VideoFrame> frames)
    {
        // The second field of a frame (7.4.1.2.4): the other parity right after the first, with its
        // frame_num, not IDR, and a reference if and only if the first one is.
        bool secondField = header.FieldPic && _firstField?.State is { } first
            && first.Header.Parity == 1 - header.Parity && first.Header.FrameNum == header.FrameNum && first.Sps == sps
            && !header.IsIdr && (first.Header.NalRefIdc != 0) == (header.NalRefIdc != 0);
        if (_firstField is not null && !secondField)
        {
            throw new NotSupportedException("A field without its second field (an unpaired field) is not supported.");
        }

        if (header.IsIdr)
        {
            _references.Clear();
        }
        else if (!secondField)
        {
            InferGapFrames(header, sps);
        }

        // A second field takes a decoder of its own, so it can run while its first is still being decoded: each writes
        // only its own parity's macroblock slots, and reads the first one's samples as any reference's, row by row.
        var decoder = TakeDecoder(sps, frames);

        int poc = _order.Next(header, sps);
        Picture picture;
        if (secondField)
        {
            picture = _firstField!.State.Picture;
            if (header.BottomField)
            {
                picture.BottomPoc = poc;
            }
            else
            {
                picture.TopPoc = poc;
            }

            // The frame keeps the first field's order count while its second field is decoded:
            // 8.2.4.2.4 orders the first field by PicOrderCnt(fldPrev).
            FieldStatistics.SecondFields++;
        }
        else
        {
            FieldStatistics.FramePictures += header.FieldPic || sps.FrameMbsOnly ? 0 : 1;
            picture = TakePicture(sps, header.FrameNum, poc);
            (picture.TopPoc, picture.BottomPoc) = _order.Fields;
        }

        // A second field's frame is the first one's: its timestamp, and whether it starts a new sequence.
        var state = new PictureState(header, sps, decoder, picture, poc, secondField);
        return secondField
            ? new PictureJob(state, _firstField!.Pts, _firstField.Restart, _firstField.Discard) { FirstField = _firstField }
            : new PictureJob(state, pts, header.IsIdr || header.MemoryManagement.Any(op => op.Op == 5), _discard);
    }

    /// <summary>
    /// A decoder for pictures of the stream's size and format that no picture being decoded uses: a free one, a new one
    /// while there are fewer than <see cref="Threads"/>, or the oldest one's once it is done (its picture output into
    /// <paramref name="frames"/>). A new size or format waits for every picture being decoded first.
    /// </summary>
    private IPictureDecoder TakeDecoder(SequenceParameterSet sps, List<VideoFrame> frames)
    {
        if (_decoders.Count > 0 && (_decoders[0].Sps.WidthInMbs != sps.WidthInMbs || _decoders[0].Sps.HeightInMbs != sps.HeightInMbs
            || _decoders[0].Sps.ChromaFormatIdc != sps.ChromaFormatIdc || _decoders[0].Sps.BitDepthLuma != sps.BitDepthLuma))
        {
            Drain(frames, all: true);
            _decoders.Clear();
        }

        while (true)
        {
            var free = _decoders.Find(d => !_inFlight.Any(job => job.State.Decoder == d));
            if (free is not null)
            {
                return free;
            }

            if (_decoders.Count < Threads)
            {
                // The statistics are shared, counted without locks: exact with one thread (which tests use).
                IPictureDecoder decoder = sps.BitDepthLuma > 8
                    ? new PictureDecoder<ushort>(sps) { Statistics = MbaffStatistics, FieldStatistics = FieldStatistics, RowDelay = RowDelay }
                    : new PictureDecoder<byte>(sps) { Statistics = MbaffStatistics, FieldStatistics = FieldStatistics, RowDelay = RowDelay };
                _decoders.Add(decoder);
                return decoder;
            }

            DrainOldest(frames);
        }
    }

    /// <summary>Whether a picture being decoded (or decoded and not yet output) is <paramref name="picture"/> or predicts from it.</summary>
    private bool InUse(Picture picture) => _inFlight.Any(job => job.State.Picture == picture || job.Uses.Contains(picture));

    // The first field of a frame being decoded as two field pictures.
    private PictureJob? _firstField;

    /// <summary>How often field pictures were decoded, so tests can check samples exercise them.</summary>
    internal FieldStatistics FieldStatistics { get; } = new();

    /// <summary>
    /// Decoding process for gaps in frame_num (8.2.5.2): a frame_num more than one past the
    /// previous reference picture's means reference frames are missing (dropped temporal layers,
    /// or lost). Each missing frame_num gets a "non-existing" frame, marked as a short-term
    /// reference by the sliding window, so that later list positions and the window come out as
    /// the encoder had them.
    /// </summary>
    private void InferGapFrames(SliceHeader header, SequenceParameterSet sps)
    {
        int maxFrameNum = 1 << sps.Log2MaxFrameNum;
        if (_prevRefFrameNum < 0 || header.FrameNum == _prevRefFrameNum || header.FrameNum == (_prevRefFrameNum + 1) % maxFrameNum)
        {
            return;
        }

        for (int frameNum = (_prevRefFrameNum + 1) % maxFrameNum; frameNum != header.FrameNum; frameNum = (frameNum + 1) % maxFrameNum)
        {
            var picture = TakePicture(sps, frameNum, _order.NextInferred(frameNum, sps));
            picture.IsNonExisting = true;
            picture.ReportDone();
            _references.MarkNonExisting(picture, sps);
        }

        _prevRefFrameNum = (header.FrameNum + maxFrameNum - 1) % maxFrameNum;
    }

    /// <summary>A picture of the stream's size that is not a reference, reused when one is free.</summary>
    private Picture TakePicture(SequenceParameterSet sps, int frameNum, int poc)
    {
        // A picture whose frame was returned by the previous call may be reused: borrowed frames
        // are valid until the next call. One waiting in the output queue may not.
        bool Fits(Picture p) => p.WidthInMbs == sps.WidthInMbs && p.HeightInMbs == sps.HeightInMbs && p.MbHeightC == sps.MbHeightC
            && p.MbWidthC == sps.MbWidthC && p.BitDepth == sps.BitDepthLuma;
        _pictures.RemoveAll(p => !Fits(p) && !_references.Contains(p) && !_output.Holds(p) && !InUse(p));
        var picture = _pictures.Find(p => Fits(p) && !_references.Contains(p) && !_output.Holds(p) && !InUse(p));
        if (picture is null)
        {
            picture = new Picture(sps.WidthInMbs, sps.HeightInMbs, sps.MbHeightC, sps.BitDepthLuma, sps.MbWidthC);
            _pictures.Add(picture);
        }

        // Every macroblock of a complete picture is written, so the old samples need no clearing.
        picture.Id = ++_pictureCount;
        picture.Poc = poc;
        picture.TopPoc = poc;
        picture.BottomPoc = poc;
        picture.Mbaff = false;
        picture.FrameNum = frameNum;
        picture.FrameNumWrap = 0;
        picture.IsShortTermReference = false;
        picture.IsLongTermReference = false;
        picture.LongTermFrameIdx = 0;
        picture.IsNonExisting = false;
        picture.ResetProgress();
        return picture;
    }

    /// <summary>Copies the visible area (frame cropping, 7.4.2.1.1) into a new frame.</summary>
    private VideoFrame Crop(Picture picture, SequenceParameterSet sps)
    {
        // Every sample is written below, so pooled planes need no clearing.
        var format = FormatOf(picture);
        var frame = _framePool.Rent(sps.Width, sps.Height, format);
        var (left, _, top, _) = sps.Crop;
        for (int plane = 0; plane < format.PlaneCount(); plane++)
        {
            int shiftX = plane == 0 || picture.MbWidthC == 16 ? 0 : 1;
            int shiftY = plane == 0 || picture.MbHeightC == 16 ? 0 : 1;
            int stride = plane == 0 ? picture.LumaStride : picture.ChromaStride;
            var (width, height) = format.PlaneSize(plane, sps.Width, sps.Height);
            for (int y = 0; y < height; y++)
            {
                int start = (((top >> shiftY) + y) * stride) + (left >> shiftX);
                if (picture.BitDepth == 8)
                {
                    picture.Plane<byte>(plane).AsSpan(start, width).CopyTo(frame.GetRow(plane, y));
                }
                else
                {
                    // 16-bit words in memory order, which is little-endian on every platform .NET runs on.
                    MemoryMarshal.AsBytes(picture.Plane<ushort>(plane).AsSpan(start, width)).CopyTo(frame.GetRow(plane, y));
                }
            }
        }

        return frame;
    }

    /// <summary>A frame of the cropped area of <paramref name="picture"/>, in its planes: no copy (<see cref="BorrowFrames"/>).</summary>
    private static VideoFrame Borrow(Picture picture, SequenceParameterSet sps)
    {
        var (left, _, top, _) = sps.Crop;
        int lumaOffset = (top * picture.LumaStride) + left;
        var format = FormatOf(picture);
        if (format.PlaneCount() == 1)
        {
            return new VideoFrame(sps.Width, sps.Height, format, [picture.Y], [picture.LumaStride], [lumaOffset]);
        }

        int chromaOffset = ((picture.MbHeightC == 16 ? top : top >> 1) * picture.ChromaStride) + (picture.MbWidthC == 16 ? left : left >> 1);
        return new VideoFrame(
            sps.Width,
            sps.Height,
            format,
            [picture.Y, picture.Cb, picture.Cr],
            [picture.LumaStride, picture.ChromaStride, picture.ChromaStride],
            [lumaOffset, chromaOffset, chromaOffset]);
    }

    private static PixelFormat FormatOf(Picture picture) =>
        PixelFormatExtensions.Of(picture.MbWidthC == 16 ? 3 : picture.MbHeightC switch { 0 => 0, 16 => 2, _ => 1 }, picture.BitDepth);

    /// <summary>The picture being decoded: its first slice header, its SPS, its decoder, its frame and its order count.</summary>
    private sealed record PictureState(SliceHeader Header, SequenceParameterSet Sps, IPictureDecoder Decoder, Picture Picture, int Poc, bool SecondField = false);

    /// <summary>A slice to decode, with its reference lists and their marking when they were built.</summary>
    private sealed record SliceWork(SliceHeader Header, PictureParameterSet Pps, BitReader Reader, List<Picture> List0, List<Picture> List1, MarkSnapshot Marks);

    /// <summary>
    /// A picture parsed from a packet, to be decoded: its state and slices, the frames it predicts from, and for its
    /// output frame the timestamp (a second field's frame takes the first field's), whether it restarts the order and
    /// whether it is shown at all (a second field's frame goes as the first field's packet says).
    /// </summary>
    private sealed class PictureJob(PictureState state, long pts, bool restart, bool discard)
    {
        public PictureState State { get; } = state;

        public long Pts { get; } = pts;

        public bool Restart { get; } = restart;

        public bool Discard { get; } = discard;

        /// <summary>A second field's first field, whose <see cref="Begun"/> it waits for.</summary>
        public PictureJob? FirstField { get; init; }

        /// <summary>Set once the picture's decoder has begun it (reset its state).</summary>
        public TaskCompletionSource Begun { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<SliceWork> Slices { get; } = [];

        public HashSet<Picture> Uses { get; } = [];

        /// <summary>With threads, the thread decoding it.</summary>
        public Task? Task { get; set; }
    }

    private static void CheckSupported(SequenceParameterSet sps)
    {
        if (sps.SeparateColourPlanes)
        {
            throw new NotSupportedException("Separate colour planes (separate_colour_plane_flag) are not supported.");
        }

        // 11 and 13 bits are left out: FFmpeg has no pixel format for them, so nothing to check them against.
        if (sps.BitDepthLuma is not (8 or 9 or 10 or 12 or 14) || (sps.ChromaFormatIdc != 0 && sps.BitDepthChroma != sps.BitDepthLuma))
        {
            throw new NotSupportedException($"Bit depths {sps.BitDepthLuma} (luma) and {sps.BitDepthChroma} (chroma) are not supported (8, 9, 10, 12 or 14, the same for both unless monochrome).");
        }
    }
}

/// <summary>Counts of field pictures, so tests can check samples exercise them.</summary>
internal sealed class FieldStatistics
{
    /// <summary>Second fields decoded (and so frames made of two field pictures).</summary>
    public int SecondFields { get; set; }

    /// <summary>Partitions of a second field predicted from the first field of its own frame.</summary>
    public int FromOwnFirstField { get; set; }

    /// <summary>Frame pictures in streams that may code frames as fields (frame_mbs_only_flag 0).</summary>
    public int FramePictures { get; set; }

    /// <summary>Direct blocks of field pictures whose co-located block is in a frame macroblock (Frm_To_Fld).</summary>
    public int FieldOverFrame { get; set; }

    /// <summary>Direct blocks of frame pictures whose co-located block is in a frame coded as fields (Fld_To_Frm).</summary>
    public int FrameOverFields { get; set; }

    /// <summary>Direct blocks of field pictures, by temporal direct prediction.</summary>
    public int TemporalDirect { get; set; }
}

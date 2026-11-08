namespace ManagedCodecs.Containers;

/// <summary>An AVI audio stream's headers: its 'strh' (ticks, length, dwSampleSize) and its 'strf' WAVEFORMATEX.</summary>
/// <param name="Length">dwLength, in ticks.</param>
/// <param name="SampleSize">dwSampleSize: 0 for one packet a chunk, else the bytes a tick.</param>
/// <param name="Extra">The WAVEFORMATEX's extra bytes (cbSize of them), as FFmpeg's extradata.</param>
internal sealed record AudioHeader(int StreamIndex, string Codec, int SampleRate, int Channels, Rational TimeBase, long Length, long SampleSize, byte[] Extra);

/// <summary>
/// One walk's audio packets, chunk by chunk in file order, as FFmpeg's AVI demuxer and (for MP3 cut anywhere in a
/// frame) its MPEG audio parser give them; <see cref="AviDemuxer"/>'s remarks say how.
/// </summary>
internal sealed class AviAudioWalk
{
    private readonly AudioHeader _header;
    private readonly long _first;
    private readonly bool _withData;

    // dwSampleSize 0: the ticks so far (a chunk each). Else the bytes so far.
    private long _ticks;
    private long _bytes;
    private long _sequence;

    // MP3 cut anywhere in a frame: the bytes not yet in a frame (from _pendingStart in _pending, the stream's byte
    // _pendingOffset first), and the packets they came in (where each starts and ends in the stream, its time, and
    // whether a frame has taken that time).
    private readonly bool _parse;
    private byte[] _pending = [];
    private int _pendingStart;
    private int _pendingEnd;
    private long _pendingOffset;
    private readonly List<(long Start, long End, long Dts, bool Taken)> _pieces = [];
    private long _nextPts;

    public AviAudioWalk(AudioHeader header, long first, bool withData)
    {
        _header = header;
        _first = first;
        _withData = withData;
        _parse = header.Codec == CodecIds.Mp3 && header.SampleSize > 0;
    }

    /// <summary>The reads and packets of the chunk whose <paramref name="size"/> bytes (not 0) start at <paramref name="position"/>.</summary>
    public IEnumerable<ReadStep> Chunk(long position, uint size)
    {
        if (_header.SampleSize == 0)
        {
            // One packet a chunk, a tick each.
            foreach (var step in Emit(position, checked((int)size), _ticks, 1))
            {
                yield return step;
            }

            _ticks++;
            yield break;
        }

        // The bytes count: pieces of 1024 blocks, one block from 32 bytes up, the whole chunk at 1 byte.
        long sampleSize = _header.SampleSize;
        long piece = sampleSize <= 1 ? size : sampleSize < 32 ? 1024 * sampleSize : sampleSize;
        for (long offset = 0; offset < size; offset += piece)
        {
            int length = (int)Math.Min(piece, size - offset);
            long dts = _bytes / sampleSize;
            if (_parse)
            {
                Reserve(length);
                yield return ReadStep.Read(position + offset, _pending.AsMemory(_pendingEnd, length));
                _pieces.Add((_bytes, _bytes + length, dts, false));
                _pendingEnd += length;
                _bytes += length;
                foreach (var step in Frames())
                {
                    yield return step;
                }
            }
            else
            {
                _bytes += length;
                foreach (var step in Emit(position + offset, length, dts, length / sampleSize))
                {
                    yield return step;
                }
            }
        }
    }

    // A packet of the file's bytes at position: read and handed out, described, or (before where a seek put the start)
    // walked over.
    private IEnumerable<ReadStep> Emit(long position, int length, long pts, long duration)
    {
        long sequence = _sequence++;
        if (sequence < _first)
        {
            yield break;
        }

        if (_withData)
        {
            var data = new byte[length];
            yield return ReadStep.Read(position, data);
            yield return ReadStep.Yield(new Packet(_header.StreamIndex, sequence, data, IsKeyframe: true, Dts: pts, Pts: pts, Duration: duration));
        }
        else
        {
            yield return ReadStep.Describe(new PacketInfo(_header.StreamIndex, sequence, length, IsKeyframe: true, Dts: pts, Pts: pts, Duration: duration));
        }
    }

    // The whole frames in the pending bytes, each as soon as its last byte is in (as FFmpeg's parser hands them out);
    // bytes that start no Layer III frame are skipped one at a time, and a frame cut off by the end never comes.
    private IEnumerable<ReadStep> Frames()
    {
        while (_pendingEnd - _pendingStart >= MpegAudioHeader.Size)
        {
            var bytes = _pending.AsSpan(_pendingStart, _pendingEnd - _pendingStart);
            if (!MpegAudioHeader.TryParse(bytes, out var frame))
            {
                Advance(1);
                continue;
            }

            int length = frame.FrameLength;
            if (bytes.Length < length)
            {
                yield break;
            }

            // The frame's length in ticks, rounded down (1152 samples at 44.1 kHz in a 1/8000 time base: 208).
            var timeBase = _header.TimeBase;
            long duration = (long)frame.SamplesPerFrame * timeBase.Denominator / ((long)frame.SampleRate * timeBase.Numerator);

            // The time of the packet the frame starts in, if no frame has taken it yet, moved on by the bytes before the
            // frame at the frame's rate (its duration over its size, rounded to nearest); else the frame before's end.
            long pts = _nextPts;
            for (int i = 0; i < _pieces.Count; i++)
            {
                var (start, end, dts, taken) = _pieces[i];
                if (start <= _pendingOffset && _pendingOffset < end)
                {
                    if (!taken)
                    {
                        pts = dts + ((((_pendingOffset - start) * duration) + (length / 2)) / length);
                        _pieces[i] = (start, end, dts, true);
                    }

                    break;
                }
            }

            _nextPts = pts + duration;
            long sequence = _sequence++;
            if (sequence >= _first)
            {
                yield return _withData
                    ? ReadStep.Yield(new Packet(_header.StreamIndex, sequence, bytes[..length].ToArray(), IsKeyframe: true, Dts: pts, Pts: pts, Duration: duration))
                    : ReadStep.Describe(new PacketInfo(_header.StreamIndex, sequence, length, IsKeyframe: true, Dts: pts, Pts: pts, Duration: duration));
            }

            Advance(length);
        }
    }

    // Drops count pending bytes, and the packets wholly before what is left.
    private void Advance(int count)
    {
        _pendingStart += count;
        _pendingOffset += count;
        int done = 0;
        while (done < _pieces.Count && _pieces[done].End <= _pendingOffset)
        {
            done++;
        }

        _pieces.RemoveRange(0, done);
    }

    // Room for count more pending bytes, what is pending moved to the front.
    private void Reserve(int count)
    {
        int pending = _pendingEnd - _pendingStart;
        if (_pendingEnd + count <= _pending.Length)
        {
            return;
        }

        var buffer = pending + count <= _pending.Length ? _pending : new byte[Math.Max(2 * _pending.Length, pending + count)];
        Array.Copy(_pending, _pendingStart, buffer, 0, pending);
        _pending = buffer;
        _pendingStart = 0;
        _pendingEnd = pending;
    }
}

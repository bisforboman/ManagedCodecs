namespace ManagedCodecs.Containers;

/// <summary>
/// A raw MP3 file (MPEG audio Layer III frames, the usual .mp3; ISO/IEC 11172-3, 13818-3 and MPEG-2.5): one audio
/// stream, a packet per frame, 1152 samples long (576 at 24 kHz and below), header included as in FFmpeg's packets.
/// </summary>
/// <remarks>
/// <para>
/// ID3v2 tags before the first frame are skipped, and anything after the last frame that does not start a frame (an
/// ID3v1 or APE tag) ends the stream. Bytes between frames that are not a frame are skipped up to the next header that
/// is followed by another one (or by the end of the file), as FFmpeg resynchronizes. A file that breaks off inside a
/// frame gives the packets before it, then <see cref="InvalidDataException"/>. A first frame whose header differs from
/// the next one's in what a stream keeps (mode, copyright, original, emphasis) is skipped too, as FFmpeg's demuxer skips
/// it. Only Layer III: a file of Layer I or II frames (.mp2), or of free-format frames, is not taken.
/// </para>
/// <para>
/// A first frame holding a Xing, Info or VBRI tag (the encoder's information, no audio) that counts the stream's frames
/// or bytes is not a packet, as in FFmpeg (one that counts neither is).
/// When its tag carries a LAME tag (LAME's, or FFmpeg's "Lavf"/"Lavc"), playback is gapless as FFmpeg's: the
/// encoder's delay plus the decoder's 529 samples are the first packet's <see cref="Packet.SkipSamples"/>, and its
/// padding less 529 are cut from the end (<see cref="Packet.SkipEndSamples"/>, by the frame count the tag gives, over
/// as many packets as it covers: a packet it covers whole skips all its samples).
/// Timestamps count samples (<see cref="AudioStreamInfo.TimeBase"/> is one over the sample rate; FFmpeg's is
/// 1/14112000, for the same times), from 0 at the first packet.
/// </para>
/// </remarks>
public sealed class Mp3Demuxer : IDemuxer
{
    // The decoder's own delay, which FFmpeg adds to the LAME tag's (528 + 1).
    private const int DecoderDelay = 529;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly int _samplesPerFrame;

    // The frames found when opening (position, length), and where one that runs past the end of the file starts (-1 if
    // none does).
    private readonly List<(long Position, int Length)> _frames = [];
    private readonly long _truncatedFrame = -1;

    // Gapless playback from the LAME tag: samples to skip at the start, and the first sample (from the first packet's
    // start) cut from the end, or long.MaxValue for none.
    private readonly int _startSkip;
    private readonly long _firstDiscarded = long.MaxValue;

    /// <summary>Reads the file's headers (and finds every frame); packets are read from the stream later, by <see cref="ReadPackets"/>.</summary>
    /// <param name="stream">A seekable stream holding the file.</param>
    /// <param name="leaveOpen">Whether <see cref="Dispose"/> leaves <paramref name="stream"/> open.</param>
    /// <exception cref="ArgumentException">The stream is not seekable.</exception>
    /// <exception cref="InvalidDataException">The file is not MP3, or is malformed.</exception>
    public Mp3Demuxer(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;

        try
        {
            long start = SkipId3v2(_stream);
            if (!TryReadHeader(_stream, start, out var first) || !FollowedByFrame(_stream, start, first))
            {
                throw new InvalidDataException("Not an MP3 file.");
            }

            _samplesPerFrame = first.SamplesPerFrame;
            long pos = start;
            long? taggedFrames = null;
            if (ReadInfoTag(first, start) is { } tag)
            {
                pos += first.FrameLength;
                taggedFrames = tag.Frames;
                if (tag.Delay is { } delay)
                {
                    _startSkip = delay + DecoderDelay;
                    if (tag.Frames is { } count)
                    {
                        _firstDiscarded = (count * _samplesPerFrame) - tag.Padding + DecoderDelay;
                    }
                }
            }

            // A first frame unlike the next one is junk to FFmpeg, and so is the next while it is unlike its own next.
            while (TryReadHeader(_stream, pos, out var frame) && pos + frame.FrameLength < _stream.Length
                && TryReadHeader(_stream, pos + frame.FrameLength, out var next) && !frame.SameStream(next))
            {
                pos += frame.FrameLength;
            }

            for (; FindFrame(ref pos, out var header); pos += header.FrameLength)
            {
                if (pos + header.FrameLength > _stream.Length)
                {
                    _truncatedFrame = pos;
                    break;
                }

                _frames.Add((pos, header.FrameLength));
            }

            Audio = new AudioStreamInfo(CodecIds.Mp3, first.SampleRate, first.Channels, _frames.Count, new Rational(1, first.SampleRate), ReadOnlyMemory<byte>.Empty, 0)
            {
                Duration = (taggedFrames ?? 0) * _samplesPerFrame,
            };
        }
        catch (Exception e)
        {
            if (!Malformed.IsMalformedInput(e))
            {
                throw;
            }

            throw Malformed.Wrap(e, "MP3");
        }
    }

    /// <summary>Always null: an MP3 file is audio only.</summary>
    public VideoStreamInfo? Video => null;

    /// <summary>
    /// The MP3 stream, its rate and channels from the first frame's header; its <see cref="AudioStreamInfo.Duration"/>
    /// is the info tag's frame count in samples (0 without one).
    /// </summary>
    public AudioStreamInfo Audio { get; }

    /// <summary>Opens the file at <paramref name="path"/>, which the demuxer disposes with itself.</summary>
    public static Mp3Demuxer Open(string path) => new(File.OpenRead(path));

    /// <inheritdoc cref="Mp3Demuxer"/>
    public IEnumerable<Packet> ReadPackets() => ReadSteps.Run(_stream, Steps(), "MP3");

    /// <inheritdoc cref="AviDemuxer.ReadPacketsAsync"/>
    public IAsyncEnumerable<Packet> ReadPacketsAsync(CancellationToken cancellationToken = default) => ReadSteps.RunAsync(_stream, Steps(), "MP3", cancellationToken);

    /// <summary>
    /// The packets <see cref="ReadPackets"/> gives, in its order and with its values, without their data: from the frames
    /// found when the file was opened, with no read of the file.
    /// </summary>
    /// <exception cref="InvalidDataException">A frame runs past the end of the file (after the packets before it).</exception>
    public IEnumerable<PacketInfo> ReadPacketInfo() => Frames().Select(f => f.Info);

    /// <summary>Disposes the stream, unless the demuxer was made to leave it open.</summary>
    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    /// <summary>
    /// Whether <paramref name="stream"/> starts (after any ID3v2 tags) with a Layer III frame followed by another one or by
    /// the end of the file. Leaves the position at 0.
    /// </summary>
    internal static bool Probe(Stream stream)
    {
        try
        {
            long start = SkipId3v2(stream);
            return TryReadHeader(stream, start, out var header) && FollowedByFrame(stream, start, header);
        }
        catch (Exception e)
        {
            if (!Malformed.IsMalformedInput(e))
            {
                throw;
            }

            return false;
        }
        finally
        {
            stream.Position = 0;
        }
    }

    // Each frame's read and packet.
    private IEnumerable<ReadStep> Steps()
    {
        foreach (var (position, info) in Frames())
        {
            var data = new byte[info.Size];
            yield return ReadStep.Read(position, data);
            yield return ReadStep.Yield(new Packet(info.StreamIndex, info.Sequence, data, info.IsKeyframe, info.Dts, info.Pts, info.Duration, info.SkipSamples, info.Discard)
            {
                SkipEndSamples = info.SkipEndSamples,
            });
        }
    }

    // Each frame's position and packet description, then the error for a frame that runs past the end of the file.
    private IEnumerable<(long Position, PacketInfo Info)> Frames()
    {
        long sequence = 0;
        long time = 0;
        foreach (var (pos, length) in _frames)
        {
            // The samples from the gapless end on: the end of the last packet, or every sample of packets past it (all of
            // them skipped from its end, not a discarded packet, as FFmpeg gives them).
            long cut = Math.Clamp(time + _samplesPerFrame - _firstDiscarded, 0, _samplesPerFrame);
            var info = new PacketInfo(0, sequence, length, IsKeyframe: true, time, time, _samplesPerFrame, sequence == 0 ? _startSkip : 0)
            {
                SkipEndSamples = (int)cut,
            };
            yield return (pos, info);
            sequence++;
            time += _samplesPerFrame;
        }

        if (_truncatedFrame >= 0)
        {
            throw new InvalidDataException($"The frame at byte {_truncatedFrame} runs past the end of the file.");
        }
    }

    // The first frame's information tag, if it has one: Xing or Info after its side information, or VBRI at byte 36
    // (Fraunhofer's), with the frame count; then, after a Xing or Info tag's fields, a LAME tag's encoder delay and
    // padding (bytes 21 to 23 from its start: 12 bits each), which FFmpeg takes from LAME's tags and its own.
    private (long? Frames, int? Delay, int Padding)? ReadInfoTag(MpegAudioHeader header, long start)
    {
        var frame = new byte[header.FrameLength];
        _stream.Position = start;
        if (_stream.ReadAtLeast(frame, frame.Length, throwOnEndOfStream: false) < frame.Length)
        {
            return null;
        }

        int at = MpegAudioHeader.Size + header.SideInfoLength;
        var span = frame.AsSpan();
        if (frame.Length >= 36 + 18 && span.Slice(36, 4).SequenceEqual("VBRI"u8))
        {
            // Its byte count, then its frame count; with neither, FFmpeg keeps the frame as a packet.
            uint vbriFrames = ReadUInt32(span[(36 + 14)..]);
            return vbriFrames != 0 || ReadUInt32(span[(36 + 10)..]) != 0 ? (vbriFrames, null, 0) : null;
        }

        if (frame.Length < at + 8 || !(span.Slice(at, 4).SequenceEqual("Xing"u8) || span.Slice(at, 4).SequenceEqual("Info"u8)))
        {
            return null;
        }

        uint flags = ReadUInt32(span[(at + 4)..]);
        int o = at + 8;
        long? frames = null;
        uint bytes = 0;
        if ((flags & 1) != 0 && o + 4 <= frame.Length)
        {
            frames = ReadUInt32(span[o..]);
            o += 4;
        }

        if ((flags & 2) != 0 && o + 4 <= frame.Length)
        {
            bytes = ReadUInt32(span[o..]);
            o += 4;
        }

        // A tag that counts neither frames nor bytes leaves its frame a packet, as FFmpeg does.
        if ((frames ?? 0) == 0 && bytes == 0)
        {
            return null;
        }

        o += ((flags & 4) != 0 ? 100 : 0) + ((flags & 8) != 0 ? 4 : 0);
        if (o + 24 > frame.Length || !(span.Slice(o, 4).SequenceEqual("LAME"u8) || span.Slice(o, 4).SequenceEqual("Lavf"u8) || span.Slice(o, 4).SequenceEqual("Lavc"u8)))
        {
            return (frames, null, 0);
        }

        int delayPadding = (span[o + 21] << 16) | (span[o + 22] << 8) | span[o + 23];
        return (frames, delayPadding >> 12, delayPadding & 0xFFF);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes) => (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);

    // The position after the ID3v2 tags at the start of the stream (ID3v2.4 3.1: "ID3", version, flags, a syncsafe
    // size, and a footer of 10 bytes more when flag 0x10 is set).
    private static long SkipId3v2(Stream stream)
    {
        Span<byte> tag = stackalloc byte[10];
        long pos = 0;
        while (true)
        {
            stream.Position = pos;
            if (stream.ReadAtLeast(tag, tag.Length, throwOnEndOfStream: false) < tag.Length || !tag[..3].SequenceEqual("ID3"u8)
                || ((tag[6] | tag[7] | tag[8] | tag[9]) & 0x80) != 0)
            {
                return pos;
            }

            long size = (tag[6] << 21) | (tag[7] << 14) | (tag[8] << 7) | tag[9];
            pos += 10 + size + ((tag[5] & 0x10) != 0 ? 10 : 0);
        }
    }

    /// <summary>
    /// The frame at <paramref name="pos"/>, or else the next one past bytes that are not a frame: a header followed by
    /// another one or by the end of the file. False at the end of the frames.
    /// </summary>
    private bool FindFrame(ref long pos, out MpegAudioHeader header)
    {
        if (TryReadHeader(_stream, pos, out header))
        {
            return true;
        }

        var buffer = new byte[4096];
        for (long chunk = pos + 1; chunk < _stream.Length; chunk += buffer.Length - 1)
        {
            _stream.Position = chunk;
            int read = _stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            for (int i = 0; i + 1 < read; i++)
            {
                if (buffer[i] == 0xFF && (buffer[i + 1] & 0xE0) == 0xE0 && TryReadHeader(_stream, chunk + i, out header) && FollowedByFrame(_stream, chunk + i, header))
                {
                    pos = chunk + i;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadHeader(Stream stream, long pos, out MpegAudioHeader header)
    {
        Span<byte> bytes = stackalloc byte[MpegAudioHeader.Size];
        stream.Position = pos;
        if (stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false) < bytes.Length)
        {
            header = default;
            return false;
        }

        return MpegAudioHeader.TryParse(bytes, out header);
    }

    /// <summary>Whether the frame at <paramref name="pos"/> ends at the end of the file or where another one starts.</summary>
    private static bool FollowedByFrame(Stream stream, long pos, MpegAudioHeader header)
    {
        long next = pos + header.FrameLength;
        return next == stream.Length || TryReadHeader(stream, next, out _);
    }
}

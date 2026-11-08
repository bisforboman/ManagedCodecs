using System.Buffers.Binary;

namespace ManagedCodecs.Containers;

/// <summary>
/// A raw MP3 file (MPEG-1, MPEG-2 or MPEG-2.5 audio Layer III, ISO/IEC 11172-3 and 13818-3): one audio stream, a packet
/// per frame, the header included as in FFmpeg's packets, each 1152 samples long (576 at the lower sampling
/// frequencies).
/// </summary>
/// <remarks>
/// <para>
/// ID3v2 tags before the first frame are skipped, and anything after the last frame that does not start a frame (an
/// ID3v1 or APE tag) ends the stream. Bytes between frames that are not a frame are skipped up to the next header that
/// is followed by another one (or by the end of the file), as FFmpeg resynchronizes; a first frame whose header differs
/// from the next one's in what a stream keeps (mode, copyright, original, emphasis) is skipped too, as FFmpeg's demuxer
/// skips it. A file that breaks off inside a frame gives the packets before it, then <see cref="InvalidDataException"/>.
/// Free-format streams (no bitrate in the header) are not read.
/// </para>
/// <para>
/// A first frame holding a Xing or Info tag (LAME's, at the end of the side information) or a VBRI tag (Fraunhofer's)
/// that counts the stream's frames or bytes is no packet, as in FFmpeg. With the LAME extension of a Xing or Info tag
/// (its encoder name starting with LAME, Lavf or Lavc), the encoder delay plus the decoder's 529 samples are skipped at
/// the start (<see cref="Packet.SkipSamples"/> of the first packet), and, where the tag counts the frames, the
/// padding less those 529 at the end (<see cref="Packet.SkipEndSamples"/> of the packets it falls in), so the stream
/// decodes to the samples encoded, as FFmpeg gives them.
/// </para>
/// <para>
/// Timestamps count samples (<see cref="AudioStreamInfo.TimeBase"/> is one over the sample rate; FFmpeg's is 1/14112000,
/// which fits every rate, for the same times). <see cref="AudioStreamInfo.CodecPrivate"/> is empty, as FFmpeg's: the
/// decoder reads the headers. <see cref="AudioStreamInfo.Duration"/> is the tag's frames times their length (before
/// any trimming), as FFmpeg's stream duration, or 0 without one.
/// </para>
/// </remarks>
public sealed class Mp3Demuxer : IDemuxer
{
    // The samples MPEG audio Layer III decoders output before the first encoded one (the synthesis and the IMDCT's
    // overlap): 528 + 1, which LAME's delay leaves out and FFmpeg adds to it.
    private const int DecoderDelay = 529;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    // The frames found when opening (position, length), and where one that runs past the end of the file starts (-1 if
    // none does).
    private readonly List<(long Position, int Length)> _frames = [];
    private readonly long _truncatedFrame = -1;

    private readonly int _samplesPerFrame;

    // From a LAME tag: the samples to skip at the start, and the range of samples (counted from the first packet's start)
    // to drop at the end, empty without one.
    private readonly int _startSkip;
    private readonly long _endSkipFrom;
    private readonly long _endSkipTo;

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

            long pos = start;
            long tagFrames = 0;
            if (TryReadTag(pos, first, out tagFrames, out int delay, out int padding))
            {
                pos += first.FrameLength;
                if (delay >= 0)
                {
                    _startSkip = delay + DecoderDelay;
                    if (tagFrames > 0)
                    {
                        _endSkipTo = tagFrames * first.SamplesPerFrame;
                        _endSkipFrom = _endSkipTo - padding + DecoderDelay;
                    }
                }
            }

            // A first frame unlike the next one is junk to FFmpeg, and so is the next while it is unlike its own next.
            while (TryReadHeader(_stream, pos, out var header) && pos + header.FrameLength < _stream.Length
                && TryReadHeader(_stream, pos + header.FrameLength, out var next) && !header.SameStream(next))
            {
                pos += header.FrameLength;
            }

            if (!TryReadHeader(_stream, pos, out var audio))
            {
                audio = first;
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

            _samplesPerFrame = audio.SamplesPerFrame;
            Audio = new AudioStreamInfo(CodecIds.Mp3, audio.SampleRate, audio.Channels, _frames.Count, new Rational(1, audio.SampleRate), ReadOnlyMemory<byte>.Empty, 0)
            {
                Duration = tagFrames * _samplesPerFrame,
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

    /// <summary>The MP3 stream, its rate and channels from the first audio frame's header.</summary>
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
    /// Whether <paramref name="stream"/> starts (after any ID3v2 tags) with a Layer III frame followed by another one or
    /// by the end of the file. Leaves the position at 0.
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
            yield return ReadStep.Yield(new Packet(info.StreamIndex, info.Sequence, data, info.IsKeyframe, info.Dts, info.Pts, info.Duration, info.SkipSamples)
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
            long end = time + _samplesPerFrame;
            int skipEnd = (int)Math.Max(0, Math.Min(end, _endSkipTo) - Math.Max(time, _endSkipFrom));
            var info = new PacketInfo(0, sequence, length, IsKeyframe: true, time, time, _samplesPerFrame, sequence == 0 ? _startSkip : 0)
            {
                SkipEndSamples = skipEnd,
            };
            yield return (pos, info);
            sequence++;
            time = end;
        }

        if (_truncatedFrame >= 0)
        {
            throw new InvalidDataException($"The frame at byte {_truncatedFrame} runs past the end of the file.");
        }
    }

    /// <summary>
    /// Whether the frame at <paramref name="pos"/> holds a tag that makes it no packet: a Xing or Info tag (LAME's) or a
    /// VBRI tag (Fraunhofer's) that counts the stream's frames or bytes. <paramref name="delay"/> and
    /// <paramref name="padding"/> are the LAME extension's, -1 without one.
    /// </summary>
    private bool TryReadTag(long pos, MpegAudioHeader header, out long frames, out int delay, out int padding)
    {
        frames = 0;
        delay = -1;
        padding = -1;
        var frame = new byte[header.FrameLength];
        _stream.Position = pos;
        int read = _stream.ReadAtLeast(frame, frame.Length, throwOnEndOfStream: false);
        var data = frame.AsSpan(0, read);

        // Xing and Info: after the side information, the flags, then the frame count (flag 1), the byte count (2), a table
        // of contents of 100 bytes (4) and a quality (8), each there only with its flag; LAME's extension follows them.
        int xing = MpegAudioHeader.Size + (header.HasCrc ? 2 : 0) + header.SideInfoLength;
        if (data.Length >= xing + 8 && (data[xing..(xing + 4)].SequenceEqual("Xing"u8) || data[xing..(xing + 4)].SequenceEqual("Info"u8)))
        {
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(data[(xing + 4)..]);
            int at = xing + 8;
            uint bytes = 0;
            if ((flags & 1) != 0 && data.Length >= at + 4)
            {
                frames = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
                at += 4;
            }

            if ((flags & 2) != 0 && data.Length >= at + 4)
            {
                bytes = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
                at += 4;
            }

            at += ((flags & 4) != 0 ? 100 : 0) + ((flags & 8) != 0 ? 4 : 0);

            // The LAME extension: a 9-byte encoder name, then 12 bytes up to the delay and padding, 12 bits each.
            if (data.Length >= at + 24 && (data[at..(at + 4)].SequenceEqual("LAME"u8) || data[at..(at + 4)].SequenceEqual("Lavf"u8)
                || data[at..(at + 4)].SequenceEqual("Lavc"u8)))
            {
                int both = (data[at + 21] << 16) | (data[at + 22] << 8) | data[at + 23];
                delay = both >> 12;
                padding = both & 0xFFF;
            }

            return frames != 0 || bytes != 0;
        }

        // VBRI: 32 bytes after the header whatever the mode; version, delay, quality, then the byte and frame counts.
        const int vbri = MpegAudioHeader.Size + 32;
        if (data.Length >= vbri + 18 && data[vbri..(vbri + 4)].SequenceEqual("VBRI"u8))
        {
            uint bytes = BinaryPrimitives.ReadUInt32BigEndian(data[(vbri + 10)..]);
            frames = BinaryPrimitives.ReadUInt32BigEndian(data[(vbri + 14)..]);
            return frames != 0 || bytes != 0;
        }

        return false;
    }

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

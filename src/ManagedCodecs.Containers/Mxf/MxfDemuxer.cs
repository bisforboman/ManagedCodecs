namespace ManagedCodecs.Containers;

/// <summary>
/// An MXF file (SMPTE ST 377-1) with frame-wrapped or clip-wrapped essence in the generic container (ST 379-1), as
/// FFmpeg's demuxer reads it: the material package's first picture track and first sound track, a packet an essence
/// element, or an edit unit of a clip-wrapped one.
/// </summary>
/// <remarks>
/// <para>
/// The header metadata (the closed partition's where there is one) gives the tracks: each material track's source clip
/// names a file package track, whose TrackNumber is the last four bytes of its essence element keys, and whose
/// descriptor gives the codec, the picture size or the sound's rate, channels and sample size. H.264 is
/// <see cref="CodecIds.H264"/> (an Annex B byte stream, no configuration record), PCM in the AES3 and Broadcast Wave
/// mappings (ST 382) <see cref="CodecIds.PcmS16Le"/>, <see cref="CodecIds.PcmS24Le"/> or <see cref="CodecIds.PcmS32Le"/>,
/// and D-10's sound (ST 386: 8-channel AES3 elements, ST 331 7.1) the same PCM, the descriptor's channels and 16 or 24
/// bits taken from each element's words, a packet's duration its element's sample count (1602 or 1601 at 29.97);
/// other pictures keep ffprobe's name (<c>prores</c>, <c>dnxhd</c>, <c>mpeg2video</c>), which no decoder here reads yet.
/// </para>
/// <para>
/// A picture packet is an edit unit: its decode time counts them (<see cref="VideoStreamInfo.TimeBase"/> is one over the
/// edit rate), its presentation time adds the index table's temporal offset where the index says the element is
/// reordered, and it is a keyframe where the index entry has its random access flag (without an index, the first), or
/// always for an intra-only codec (ProRes, VC-3, JPEG 2000, and D-10's MPEG-2, I pictures only by SMPTE ST 356), whose index entries FFmpeg's muxer leaves without it. A sound packet's times count samples, its duration its bytes over the block
/// alignment. The essence elements are found by walking the file's KLV items once, when opening; partition packs,
/// header metadata, index segments and fill items between them are skipped.
/// </para>
/// <para>
/// A track whose essence is a single element that its index table puts two or more edit units in is clip-wrapped (an
/// OP-Atom file's, ST 390, whatever its essence container label says: FFmpeg's OP-Atom muxer labels clip-wrapped essence
/// as frame-wrapped). Its pictures are cut by the index entries' stream offsets, which count from the element's value,
/// or by the index's constant edit unit size; its sound, whose edit unit is a sample, into pieces of a 25th of a second
/// (1920 samples at 48 kHz), as FFmpeg's demuxer reads it, the last one shorter. Essence in other files (an OP-Atom
/// clip's other tracks) is not read.
/// </para>
/// <para>
/// The size is the sampled rectangle's (the picture a decoder gives, without the stored padding of a macroblock row),
/// twice its height for separate fields. FFmpeg's demuxer reports the stored size, and its decoder the sampled one.
/// </para>
/// </remarks>
public sealed class MxfDemuxer : IDemuxer
{
    // A generic container essence element's key (ST 379-1 7.1): bytes 1 to 12, its version (byte 8) aside; bytes 13 to
    // 16 are the item type, count, element type and number, the file package track's TrackNumber.
    private static ReadOnlySpan<byte> EssenceElementPrefix => [0x06, 0x0E, 0x2B, 0x34, 0x01, 0x02, 0x01, 0x01, 0x0D, 0x01, 0x03, 0x01];

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly List<PacketInfo> _packets = [];
    private readonly List<long> _positions = [];

    // Each packet's bytes in the file: its size, but an 8-channel AES3 element's whole value.
    private readonly List<int> _lengths = [];

    // The sound track's 8-channel AES3 elements (D-10's): its channels and bytes a sample, or 0 where it has none.
    private int _aes3Channels;
    private int _aes3Bytes;

    // Where each stream's read starts, as a sequence: 0 until a Seek moves it.
    private long _videoStart;
    private long _audioStart;

    /// <summary>Reads the file's partitions and header metadata and finds every essence element; packets are read later, by <see cref="ReadPackets"/>.</summary>
    /// <param name="stream">A seekable stream holding the file.</param>
    /// <param name="leaveOpen">Whether <see cref="Dispose"/> leaves <paramref name="stream"/> open.</param>
    /// <exception cref="ArgumentException">The stream is not seekable.</exception>
    /// <exception cref="InvalidDataException">The file is not MXF, or is malformed.</exception>
    public MxfDemuxer(Stream stream, bool leaveOpen = false)
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
            var partitions = MxfPartitions.Read(stream);
            var header = MxfHeader.Read(stream, partitions);
            StartTimecode = header.StartTimecode;
            int videoIndex = header.Tracks.ToList().FindIndex(t => t.Kind == MxfTrackKind.Picture && t.BodySid != 0);
            int audioIndex = header.Tracks.ToList().FindIndex(t => t.Kind == MxfTrackKind.Sound && t.BodySid != 0);
            var video = videoIndex < 0 ? null : header.Tracks[videoIndex];
            var audio = audioIndex < 0 ? null : header.Tracks[audioIndex];
            if (audio is not null && (audio.AudioSamplingRate.Numerator <= 0 || audio.AudioSamplingRate.Denominator != 1 || audio.Channels <= 0 || audio.QuantizationBits <= 0))
            {
                audio = null;
            }

            if (video is not null && (video.EditRate.Numerator <= 0 || video.EditRate.Denominator <= 0))
            {
                video = null;
            }

            if (audio is not null && IsAes3Element(audio.TrackNumber) && audio.QuantizationBits is 16 or 24 && audio.Channels is >= 1 and <= 8)
            {
                _aes3Channels = audio.Channels;
                _aes3Bytes = audio.QuantizationBits / 8;
            }

            var indexes = Walk(partitions.Header.End, video, videoIndex, audio, audioIndex);
            if (video is not null && indexes.TryGetValue(video.BodySid, out var videoTable))
            {
                SplitClip(videoIndex, videoTable, 0);
            }

            if (audio is not null && _aes3Channels == 0 && indexes.TryGetValue(audio.BodySid, out var audioTable))
            {
                SplitClip(audioIndex, audioTable, Math.Max(1, audio.AudioSamplingRate.Numerator / 25) * (long)BlockAlign(audio));
            }

            if (video is not null)
            {
                Time(video, videoIndex, indexes.TryGetValue(video.BodySid, out var index) ? index : null);
                long count = _packets.Count(p => p.StreamIndex == videoIndex);
                Video = new VideoStreamInfo(
                    VideoCodec(video.Codec),
                    video.SampledWidth,
                    video.SampledHeight,
                    (double)video.EditRate.Numerator / video.EditRate.Denominator,
                    count,
                    new Rational(video.EditRate.Denominator, video.EditRate.Numerator),
                    ReadOnlyMemory<byte>.Empty,
                    videoIndex);
            }

            if (audio is not null)
            {
                int rate = audio.AudioSamplingRate.Numerator;
                Time(audio, audioIndex, null);
                var packets = _packets.Where(p => p.StreamIndex == audioIndex).ToList();
                Audio = new AudioStreamInfo(audio.Codec is { } codec ? AudioCodec(codec) : "unknown", rate, audio.Channels, packets.Count, new Rational(1, rate), ReadOnlyMemory<byte>.Empty, audioIndex)
                {
                    Duration = packets.Count == 0 ? 0 : packets[^1].Pts + packets[^1].Duration,
                };
            }

            if (Video is null && Audio is null)
            {
                throw new NotSupportedException("The MXF file has no picture or sound track with essence in it.");
            }
        }
        catch (Exception e)
        {
            if (!Malformed.IsMalformedInput(e))
            {
                throw;
            }

            throw Malformed.Wrap(e, "MXF");
        }
    }

    /// <summary>The material package's first picture track, or null if it has none with essence in the file.</summary>
    public VideoStreamInfo? Video { get; }

    /// <summary>The material package's first sound track, or null if it has none with essence in the file.</summary>
    public AudioStreamInfo? Audio { get; }

    /// <summary>
    /// The start of the material package's first timecode track (SMPTE ST 377-1 B.17: its timecode component's start,
    /// rounded base and drop frame), as ffprobe's <c>timecode</c> tag gives it; null if the package has none.
    /// </summary>
    public Timecode? StartTimecode { get; }

    /// <summary>Opens the file at <paramref name="path"/>, which the demuxer disposes with itself.</summary>
    public static MxfDemuxer Open(string path) => new(File.OpenRead(path));

    /// <inheritdoc cref="MxfDemuxer"/>
    public IEnumerable<Packet> ReadPackets() => ReadSteps.Run(_stream, Steps(), "MXF");

    /// <inheritdoc cref="AviDemuxer.ReadPacketsAsync"/>
    public IAsyncEnumerable<Packet> ReadPacketsAsync(CancellationToken cancellationToken = default) => ReadSteps.RunAsync(_stream, Steps(), "MXF", cancellationToken);

    /// <summary>The packets <see cref="ReadPackets"/> gives, in its order and with its values, without their data and with no read of the file.</summary>
    public IEnumerable<PacketInfo> ReadPacketInfo() => _packets.Where(Included);

    /// <inheritdoc/>
    /// <remarks>
    /// Each stream starts at its last keyframe at or before the time (the other stream's rescaled to its time base,
    /// rounded down), found among the packets listed when opening, with no read of the file: every sound packet is a
    /// keyframe, a picture packet where the index table says so.
    /// </remarks>
    public void Seek(long pts, int streamIndex)
    {
        Rational timeBase = streamIndex == Video?.StreamIndex ? Video.TimeBase
            : streamIndex == Audio?.StreamIndex ? Audio.TimeBase
            : throw new ArgumentOutOfRangeException(nameof(streamIndex), streamIndex, "No stream of the MXF demuxer has this index.");

        if (Video is { } video)
        {
            _videoStart = Start(video.StreamIndex, Rescale(pts, timeBase, video.TimeBase));
        }

        if (Audio is { } audio)
        {
            _audioStart = Start(audio.StreamIndex, Rescale(pts, timeBase, audio.TimeBase));
        }
    }

    /// <summary>Disposes the stream, unless the demuxer was made to leave it open.</summary>
    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    /// <summary>Whether <paramref name="stream"/> starts with an MXF header partition pack, after a run-in of under 64 KiB. Leaves the position at 0.</summary>
    internal static bool Probe(Stream stream)
    {
        bool found = MxfPartitions.Probe(stream);
        stream.Position = 0;
        return found;
    }

    // ffprobe's names to this library's, where it has one.
    private static string VideoCodec(string? codec) => codec switch
    {
        "h264" => CodecIds.H264,
        null => "unknown",
        _ => codec,
    };

    private static string AudioCodec(string codec) => codec switch
    {
        "pcm_u8" => CodecIds.PcmU8,
        "pcm_s16le" => CodecIds.PcmS16Le,
        "pcm_s24le" => CodecIds.PcmS24Le,
        "pcm_s32le" => CodecIds.PcmS32Le,
        _ => codec,
    };

    // value in `from` as a time in `to`, rounded down.
    private static long Rescale(long value, Rational from, Rational to)
    {
        var n = (System.Numerics.BigInteger)value * from.Numerator * to.Denominator;
        var d = (System.Numerics.BigInteger)from.Denominator * to.Numerator;
        var q = System.Numerics.BigInteger.DivRem(n, d, out var r);
        var floor = r.IsZero || (n.Sign < 0) == (d.Sign < 0) ? q : q - 1;
        return floor > long.MaxValue ? long.MaxValue : floor < long.MinValue ? long.MinValue : (long)floor;
    }

    // The sequence of the stream's last keyframe at or before pts, or 0.
    private long Start(int stream, long pts)
    {
        long start = 0;
        foreach (var packet in _packets)
        {
            if (packet.StreamIndex == stream && packet.IsKeyframe && packet.Pts <= pts)
            {
                start = packet.Sequence;
            }
        }

        return start;
    }

    private bool Included(PacketInfo packet) =>
        packet.StreamIndex == Video?.StreamIndex ? packet.Sequence >= _videoStart : packet.Sequence >= _audioStart;

    // Each included packet's read and packet, in file order.
    private IEnumerable<ReadStep> Steps()
    {
        for (int i = 0; i < _packets.Count; i++)
        {
            var info = _packets[i];
            if (!Included(info))
            {
                continue;
            }

            var data = new byte[info.Size];
            if (_aes3Channels > 0 && info.StreamIndex == Audio?.StreamIndex)
            {
                var element = new byte[_lengths[i]];
                yield return ReadStep.Read(_positions[i], element);
                Aes3ToPcm(element, data, _aes3Channels, _aes3Bytes);
            }
            else
            {
                yield return ReadStep.Read(_positions[i], data);
            }

            yield return ReadStep.Yield(new Packet(info.StreamIndex, info.Sequence, data, info.IsKeyframe, info.Dts, info.Pts, info.Duration));
        }
    }

    /// <summary>
    /// Walks every KLV item after the header partition pack: the essence elements of the two tracks become packets (in
    /// file order, timed later by <see cref="Time"/>), index table segments are read; the rest is skipped. Returns the
    /// index tables by BodySID.
    /// </summary>
    private Dictionary<uint, MxfIndex> Walk(long start, MxfTrack? video, int videoIndex, MxfTrack? audio, int audioIndex)
    {
        var indexes = new Dictionary<uint, MxfIndex>();
        Span<byte> key = stackalloc byte[Klv.KeyLength];
        long length = _stream.Length;
        long videoCount = 0;
        long audioCount = 0;
        for (long position = start; position < length;)
        {
            long valueStart = Klv.ReadHeader(_stream, position, key, out long size);
            long next = checked(valueStart + size);
            if (next > length)
            {
                throw new InvalidDataException($"A KLV item at byte {position} runs past the end of the MXF file.");
            }

            if (Klv.Matches(key, EssenceElementPrefix))
            {
                uint number = (uint)((key[12] << 24) | (key[13] << 16) | (key[14] << 8) | key[15]);
                if (size > int.MaxValue)
                {
                    throw new InvalidDataException($"An MXF essence element at byte {position} is larger than a packet can be.");
                }

                if (video is not null && number == video.TrackNumber)
                {
                    _packets.Add(new PacketInfo(videoIndex, videoCount++, (int)size, false, 0, 0, 0));
                    _positions.Add(valueStart);
                    _lengths.Add((int)size);
                }
                else if (audio is not null && number == audio.TrackNumber)
                {
                    int pcm = _aes3Channels > 0 ? Aes3PcmSize(valueStart, size) : (int)size;
                    _packets.Add(new PacketInfo(audioIndex, audioCount++, pcm, true, 0, 0, 0));
                    _positions.Add(valueStart);
                    _lengths.Add((int)size);
                }
            }
            else if (MxfIndex.IsSegmentKey(key))
            {
                if (size > 64 << 20)
                {
                    throw new InvalidDataException($"An MXF index table segment at byte {position} is {size} bytes.");
                }

                byte[] value = new byte[size];
                _stream.Position = valueStart;
                _stream.ReadExactly(value);
                MxfIndex.AddSegment(indexes, value, key[5] == 0x13);
            }
            else if (Klv.IsRandomIndexPack(key))
            {
                break;
            }

            position = next;
        }

        return indexes;
    }

    // Whether a generic container element numbered trackNumber (its key's bytes 13 to 16) is an 8-channel AES3 element:
    // the content package's sound item (item type 06h, ST 379-1 7.1) with element type 10h (SMPTE ST 331 7.1), as D-10's
    // sound is (ST 386).
    private static bool IsAes3Element(uint trackNumber) => (trackNumber >> 24) == 0x06 && ((trackNumber >> 8) & 0xFF) == 0x10;

    /// <summary>
    /// The PCM an 8-channel AES3 element at <paramref name="valueStart"/> (its value, <paramref name="size"/> bytes)
    /// gives: its audio sample count (header bytes 1 and 2, little-endian, ST 331 Figure 2) times the descriptor's
    /// channels and sample size. The element holds all 8 channels' words whether valid or not.
    /// </summary>
    private int Aes3PcmSize(long valueStart, long size)
    {
        Span<byte> header = stackalloc byte[4];
        if (size < header.Length)
        {
            throw new InvalidDataException($"An 8-channel AES3 element at byte {valueStart} is {size} bytes, under its 4-byte header.");
        }

        _stream.Position = valueStart;
        _stream.ReadExactly(header);
        int count = header[1] | (header[2] << 8);
        if (size < header.Length + (count * 8L * 4))
        {
            throw new InvalidDataException($"An 8-channel AES3 element at byte {valueStart} holds {size} bytes, under its {count} samples of 8 channels.");
        }

        return count * _aes3Channels * _aes3Bytes;
    }

    /// <summary>
    /// The little-endian PCM of an 8-channel AES3 element (SMPTE ST 331 7.1): after the 4-byte header, a 32-bit
    /// little-endian word per channel per sample, channels 1 to 8 in turn, each holding the AES3 audio bits a0 to a23 in
    /// bits 4 to 27 (the channel number and the F bit below, V, U, C and P above). The first <paramref name="channels"/>
    /// channels are kept, the 24 bits whole or their top 16.
    /// </summary>
    internal static void Aes3ToPcm(ReadOnlySpan<byte> element, Span<byte> pcm, int channels, int bytes)
    {
        int samples = pcm.Length / (channels * bytes);
        int o = 0;
        for (int s = 0; s < samples; s++)
        {
            for (int c = 0; c < channels; c++)
            {
                uint word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(element[(4 + (((s * 8) + c) * 4))..]);
                uint audio = (word >> 4) & 0xFFFFFF;
                if (bytes == 3)
                {
                    pcm[o++] = (byte)audio;
                }

                pcm[o++] = (byte)(audio >> 8);
                pcm[o++] = (byte)(audio >> 16);
            }
        }
    }

    // A sound sample's size: the descriptor's block alignment, else the channels' samples.
    private static int BlockAlign(MxfTrack track) => track.BlockAlign > 0 ? track.BlockAlign : track.Channels * ((track.QuantizationBits + 7) / 8);

    /// <summary>
    /// Cuts the stream's essence into packets where it is a single element that <paramref name="index"/> puts two or
    /// more edit units in (clip-wrapped, the class remarks say how): pictures an edit unit a packet; sound, whose index
    /// edit unit is a sample, in pieces of <paramref name="pieceBytes"/>, the last shorter, whole samples only.
    /// </summary>
    private void SplitClip(int stream, MxfIndex index, long pieceBytes)
    {
        int at = -1;
        for (int i = 0; i < _packets.Count; i++)
        {
            if (_packets[i].StreamIndex == stream)
            {
                if (at >= 0)
                {
                    return;
                }

                at = i;
            }
        }

        if (at < 0)
        {
            return;
        }

        var element = _packets[at];
        long position = _positions[at];
        var pieces = new List<(long Offset, long Length)>();
        if (pieceBytes > 0)
        {
            // Sound: an edit unit a sample, so the index's constant size is under the element's.
            long blockAlign = index.EditUnitByteCount;
            if (blockAlign <= 0 || blockAlign >= element.Size)
            {
                return;
            }

            long whole = element.Size - (element.Size % blockAlign);
            long piece = Math.Max(blockAlign, pieceBytes - (pieceBytes % blockAlign));
            for (long offset = 0; offset < whole; offset += piece)
            {
                pieces.Add((offset, Math.Min(piece, whole - offset)));
            }
        }
        else
        {
            pieces = index.EditUnits(element.Size);
        }

        if (pieces.Count < 2)
        {
            return;
        }

        _packets.RemoveAt(at);
        _positions.RemoveAt(at);
        _lengths.RemoveAt(at);
        for (int k = 0; k < pieces.Count; k++)
        {
            _packets.Insert(at + k, element with { Sequence = k, Size = (int)pieces[k].Length });
            _positions.Insert(at + k, position + pieces[k].Offset);
            _lengths.Insert(at + k, (int)pieces[k].Length);
        }
    }

    /// <summary>Gives the track's packets their times, durations and keyframe flags (the class remarks say how).</summary>
    private void Time(MxfTrack track, int stream, MxfIndex? index)
    {
        bool intra = track.Codec is "prores" or "dnxhd" or "jpeg2000" || MxfLabels.IsD10Container(track.EssenceContainer ?? []);
        int blockAlign = _aes3Channels > 0 && track.Kind == MxfTrackKind.Sound ? _aes3Channels * _aes3Bytes : BlockAlign(track);
        long samples = 0;
        for (int i = 0; i < _packets.Count; i++)
        {
            var packet = _packets[i];
            if (packet.StreamIndex != stream)
            {
                continue;
            }

            if (track.Kind == MxfTrackKind.Sound)
            {
                long duration = packet.Size / blockAlign;
                _packets[i] = packet with { Dts = samples, Pts = samples, Duration = duration };
                samples += duration;
            }
            else
            {
                long unit = packet.Sequence;
                var entry = index?.Entry(unit);
                long pts = unit + (entry is { } e && index!.FirstElementReordered ? e.TemporalOffset : 0);
                bool keyframe = intra || (entry is { } f ? (f.Flags & MxfIndex.RandomAccess) != 0 : unit == 0);
                _packets[i] = packet with { Dts = unit, Pts = pts, Duration = 1, IsKeyframe = keyframe };
            }
        }
    }
}

namespace ManagedCodecs.Containers;

/// <summary>
/// An MXF file (SMPTE ST 377-1) with frame-wrapped or clip-wrapped essence in the generic container (ST 379-1), as
/// FFmpeg's demuxer reads it: the material package's first picture track and its sound tracks (the first alone until
/// <see cref="SelectAudio"/> chooses others), a packet an essence element, or an edit unit of a clip-wrapped one.
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
/// A file cut short (a recording that never closed, a partial download) is read as far as it goes, as FFmpeg's demuxer
/// reads it: the essence element the end falls in is the last packet, of the bytes there (an 8-channel AES3 element's
/// samples whose kept channels are there); a clip-wrapped sound track whose index was cut off with the footer is still
/// cut into pieces where its edit unit is a sample. A growing file is read as far as it was written when opened, and
/// followed as it grows by <see cref="Refresh"/>.
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

    // The packet (an index into _packets) of the essence element a file cut short ends in, or -1.
    private int _cut = -1;

    // The tracks read, their stream indexes, and what the walk has found so far: where it stopped (the next item, or
    // the element it ended in), each track's packet count, the index tables by BodySID; and how many packets Refresh has
    // given. Refresh walks on from _walkEnd.
    private MxfTrack? _video;
    private int _videoIndex = -1;
    private readonly Dictionary<uint, MxfIndex> _indexes = [];
    private long _walkEnd;
    private long _videoCount;
    private int _given;
    private bool _clipWrapped;

    // Every sound track read, in the header's order, by its essence elements' number and by its stream index; and the
    // ones ReadPackets gives (SelectAudio), Audio's first.
    private readonly List<Sound> _sounds = [];
    private readonly Dictionary<uint, Sound> _soundsByNumber = [];
    private readonly Dictionary<int, Sound> _soundsByIndex = [];
    private Sound[] _selected = [];

    // Where the video's read starts, as a sequence: 0 until a Seek moves it (a sound track's is its Sound.Start).
    private long _videoStart;

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
            var video = videoIndex < 0 ? null : header.Tracks[videoIndex];
            if (video is not null && (video.EditRate.Numerator <= 0 || video.EditRate.Denominator <= 0))
            {
                video = null;
            }

            // The sound tracks with essence in the file, from the first on: a stream each, as FFmpeg's demuxer gives
            // them. One whose descriptor says no rate, channels or sample size is left out, and of two that name the same
            // essence elements the first is read.
            for (int i = 0; i < header.Tracks.Count; i++)
            {
                var track = header.Tracks[i];
                if (track.Kind != MxfTrackKind.Sound || track.BodySid == 0)
                {
                    continue;
                }

                if (track.AudioSamplingRate.Numerator <= 0 || track.AudioSamplingRate.Denominator != 1 || track.Channels <= 0 || track.QuantizationBits <= 0)
                {
                    continue;
                }

                bool aes3 = IsAes3Element(track.TrackNumber) && track.QuantizationBits is 16 or 24 && track.Channels is >= 1 and <= 8;
                var sound = new Sound(track, i, aes3 ? track.Channels : 0, aes3 ? track.QuantizationBits / 8 : 0);
                if (_soundsByNumber.TryAdd(track.TrackNumber, sound))
                {
                    _sounds.Add(sound);
                    _soundsByIndex.Add(i, sound);
                }
            }

            _selected = _sounds.Count == 0 ? [] : [_sounds[0]];
            (_video, _videoIndex) = (video, videoIndex);
            var indexes = _indexes;
            Walk(partitions.Header.End, partial: true);
            if (video is not null && indexes.TryGetValue(video.BodySid, out var videoTable))
            {
                SplitClip(videoIndex, videoTable, 0, 0);
            }

            foreach (var sound in _sounds)
            {
                if (sound.Aes3Channels > 0)
                {
                    continue;
                }

                // The index's constant edit unit size (a sample); without one (a file cut before its footer's index), the
                // descriptor's block alignment where the track's edit unit is a sample, as OP-Atom's is: FFmpeg's demuxer
                // cuts such a clip into its pieces all the same.
                var audio = sound.Track;
                long pieceBytes = Math.Max(1, audio.AudioSamplingRate.Numerator / 25) * (long)BlockAlign(audio);
                if (indexes.TryGetValue(audio.BodySid, out var audioTable))
                {
                    SplitClip(sound.Index, audioTable, pieceBytes, audioTable.EditUnitByteCount);
                }
                else if (audio.EditRate == (audio.AudioSamplingRate.Numerator, audio.AudioSamplingRate.Denominator))
                {
                    SplitClip(sound.Index, null, pieceBytes, BlockAlign(audio));
                }
            }

            Describe();
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
    public VideoStreamInfo? Video { get; private set; }

    /// <summary>
    /// The sound track <see cref="ReadPackets"/> gives: the material package's first, or the first of those chosen with
    /// <see cref="SelectAudio"/>. Null if the package has none with essence in the file.
    /// </summary>
    public AudioStreamInfo? Audio { get; private set; }

    /// <summary>
    /// The material package's sound tracks with essence in the file, in its order, a stream each as FFmpeg's demuxer
    /// gives them: broadcast files usually carry a mono track a channel (4, 8 or 16 of them).
    /// </summary>
    public IReadOnlyList<AudioStreamInfo> AudioTracks { get; private set; } = [];

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

    /// <summary>
    /// Reads a file that is still being written (a recorder's output, watched as it grows): gives the packets of the
    /// essence elements written whole since the last call, in file order, each read as it is enumerated; the first call
    /// gives every whole packet so far. Their <see cref="Packet.Sequence"/> and times are those a read of the finished
    /// file gives, and <see cref="ReadPackets"/>, <see cref="Video"/> and <see cref="Audio"/> include them from then on.
    /// Of the sound tracks, those selected at each call (<see cref="SelectAudio"/>) are given.
    /// The element the writer is still in is held back until it is whole (a file cut short read only by
    /// <see cref="ReadPackets"/> gives it as it is, the last packet), so poll this, as often as the caller likes, until
    /// the file is finished: its footer written, or nothing new for long enough.
    /// </summary>
    /// <exception cref="NotSupportedException">The file's essence is clip-wrapped.</exception>
    /// <exception cref="InvalidDataException">What was written since is malformed.</exception>
    /// <remarks>
    /// Keyframe flags and presentation times that come from an index table are those of the index tables read so far:
    /// FFmpeg's muxer writes them in the footer (or in body partitions after the essence they index), so until then a
    /// picture packet is a keyframe only for an intra-only codec or as the first, and its presentation time is its decode
    /// time; <see cref="ReadPacketInfo"/> gives them as the indexes read since say. Clip-wrapped essence (an OP-Atom
    /// file's single element a track) is whole only once the clip is finished, so it is not followed.
    /// </remarks>
    public IEnumerable<Packet> Refresh()
    {
        if (_clipWrapped)
        {
            throw new NotSupportedException("A clip-wrapped MXF file (OP-Atom) can't be followed as it is written: its essence is one element a track.");
        }

        try
        {
            if (_cut >= 0)
            {
                // The element the file ended in when opened: dropped, and read again whole by the walk.
                if (_packets[_cut].StreamIndex == _videoIndex)
                {
                    _videoCount--;
                }
                else
                {
                    _soundsByIndex[_packets[_cut].StreamIndex].Count--;
                }

                _packets.RemoveAt(_cut);
                _positions.RemoveAt(_cut);
                _lengths.RemoveAt(_cut);
                _cut = -1;
            }

            Walk(_walkEnd, partial: false);
            Describe();
        }
        catch (Exception e)
        {
            if (!Malformed.IsMalformedInput(e))
            {
                throw;
            }

            throw Malformed.Wrap(e, "MXF");
        }

        int from = Math.Min(_given, _packets.Count);
        _given = _packets.Count;
        return ReadSteps.Run(_stream, Steps(from, _given), "MXF");
    }

    /// <summary>The packets <see cref="ReadPackets"/> gives, in its order and with its values, without their data and with no read of the file.</summary>
    public IEnumerable<PacketInfo> ReadPacketInfo() => _packets.Where(Included);

    /// <inheritdoc/>
    /// <remarks>
    /// Costs no read of the file: every sound track's packets were listed when opening. A <see cref="Seek"/> made before
    /// holds for the tracks chosen after it.
    /// </remarks>
    public void SelectAudio(params int[] streamIndexes)
    {
        ArgumentNullException.ThrowIfNull(streamIndexes);
        if (streamIndexes.Length == 0)
        {
            throw new ArgumentException("At least one audio stream must be selected.", nameof(streamIndexes));
        }

        var selected = new Sound[streamIndexes.Length];
        for (int i = 0; i < selected.Length; i++)
        {
            if (!_soundsByIndex.TryGetValue(streamIndexes[i], out var sound))
            {
                throw new ArgumentOutOfRangeException(nameof(streamIndexes), streamIndexes[i], "No audio stream of the MXF demuxer has this index.");
            }

            if (Array.IndexOf(selected, sound, 0, i) >= 0)
            {
                throw new ArgumentException($"Audio stream {streamIndexes[i]} is selected twice.", nameof(streamIndexes));
            }

            selected[i] = sound;
        }

        _selected = selected;
        Audio = selected[0].Info;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Each stream starts at its last keyframe at or before the time (the other stream's rescaled to its time base,
    /// rounded down), found among the packets listed when opening, with no read of the file: every sound packet is a
    /// keyframe, a picture packet where the index table says so. Every sound track is moved, selected or not, and
    /// <paramref name="streamIndex"/> may be any of <see cref="AudioTracks"/>.
    /// </remarks>
    public void Seek(long pts, int streamIndex)
    {
        Rational timeBase = streamIndex == Video?.StreamIndex ? Video.TimeBase
            : _soundsByIndex.TryGetValue(streamIndex, out var named) ? named.Info!.TimeBase
            : throw new ArgumentOutOfRangeException(nameof(streamIndex), streamIndex, "No stream of the MXF demuxer has this index.");

        if (Video is { } video)
        {
            _videoStart = Start(video.StreamIndex, Rescale(pts, timeBase, video.TimeBase));
        }

        foreach (var sound in _sounds)
        {
            sound.Start = Start(sound.Index, Rescale(pts, timeBase, sound.Info!.TimeBase));
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

    // Whether ReadPackets gives the packet: the video's or a selected sound track's, from where its stream's read starts.
    private bool Included(PacketInfo packet) => Selected(packet, _selected, fromStart: false);

    private bool Selected(PacketInfo packet, Sound[] selected, bool fromStart)
    {
        if (packet.StreamIndex == _videoIndex)
        {
            return fromStart || packet.Sequence >= _videoStart;
        }

        foreach (var sound in selected)
        {
            if (sound.Index == packet.StreamIndex)
            {
                return fromStart || packet.Sequence >= sound.Start;
            }
        }

        return false;
    }

    // Each included packet's read and packet, in file order; or, for Refresh, those from `from` to `to`, wherever a Seek
    // put the streams' starts. The sound tracks are those selected when the read was started.
    private IEnumerable<ReadStep> Steps(int from = 0, int to = -1)
    {
        bool all = to >= 0;
        var selected = _selected;
        for (int i = from; i < (all ? to : _packets.Count); i++)
        {
            var info = _packets[i];
            if (!Selected(info, selected, fromStart: all))
            {
                continue;
            }

            var data = new byte[info.Size];
            if (_soundsByIndex.TryGetValue(info.StreamIndex, out var sound) && sound.Aes3Channels > 0)
            {
                var element = new byte[_lengths[i]];
                yield return ReadStep.Read(_positions[i], element);
                Aes3ToPcm(element, data, sound.Aes3Channels, sound.Aes3Bytes);
            }
            else
            {
                yield return ReadStep.Read(_positions[i], data);
            }

            yield return ReadStep.Yield(new Packet(info.StreamIndex, info.Sequence, data, info.IsKeyframe, info.Dts, info.Pts, info.Duration));
        }
    }

    /// <summary>
    /// Walks the KLV items from <paramref name="start"/> to the end of the file as it is now: the essence elements of the
    /// tracks read become packets (in file order, timed later by <see cref="Time"/>), index table segments are read into
    /// <see cref="_indexes"/>; the rest is skipped. An essence element the end falls in is a packet of the bytes there
    /// where <paramref name="partial"/> (opening), else left for a later walk (<see cref="Refresh"/>). Sets
    /// <see cref="_walkEnd"/> to where a later walk goes on.
    /// </summary>
    private void Walk(long start, bool partial)
    {
        var (video, videoIndex, indexes) = (_video, _videoIndex, _indexes);
        Span<byte> key = stackalloc byte[Klv.KeyLength];
        long length = _stream.Length;
        long position = start;
        for (; position < length && Klv.TryReadHeader(_stream, position, key, out long valueStart, out long size);)
        {
            // A file cut short (a recording that never closed, a partial download): the essence element the end falls in
            // is a packet of the bytes there, as FFmpeg's demuxer gives it (flagged corrupt), and the last; an index
            // table segment or anything else cut off is left out.
            long next = checked(valueStart + size);
            if (next > length)
            {
                if (!partial || !Klv.Matches(key, EssenceElementPrefix) || length <= valueStart)
                {
                    break;
                }

                _cut = _packets.Count;
                size = length - valueStart;
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
                    _packets.Add(new PacketInfo(videoIndex, _videoCount++, (int)size, false, 0, 0, 0));
                    _positions.Add(valueStart);
                    _lengths.Add((int)size);
                }
                else if (_soundsByNumber.TryGetValue(number, out var sound))
                {
                    int pcm = sound.Aes3Channels > 0 ? Aes3PcmSize(valueStart, size, _cut >= 0, sound.Aes3Channels, sound.Aes3Bytes) : (int)size;
                    _packets.Add(new PacketInfo(sound.Index, sound.Count++, pcm, true, 0, 0, 0));
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

            if (_cut >= 0)
            {
                break; // the element the file ends in: a later walk reads it again from its key
            }

            position = next;
        }

        _walkEnd = position;
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
    private int Aes3PcmSize(long valueStart, long size, bool cut, int channels, int bytes)
    {
        Span<byte> header = stackalloc byte[4];
        if (cut && size < header.Length)
        {
            return 0; // the file ends inside the element's header: no samples
        }

        if (size < header.Length)
        {
            throw new InvalidDataException($"An 8-channel AES3 element at byte {valueStart} is {size} bytes, under its 4-byte header.");
        }

        _stream.Position = valueStart;
        _stream.ReadExactly(header);
        int count = header[1] | (header[2] << 8);
        if (cut)
        {
            // The samples whose kept channels' words (the first `channels` of its 8) are all before the file's end, as
            // FFmpeg's demuxer counts them: a sample cut after them still gives its PCM.
            count = (int)Math.Min(count, (size - header.Length + ((8 - channels) * 4)) / (8 * 4));
        }

        if (!cut && size < header.Length + (count * 8L * 4))
        {
            throw new InvalidDataException($"An 8-channel AES3 element at byte {valueStart} holds {size} bytes, under its {count} samples of 8 channels.");
        }

        return count * channels * bytes;
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
    /// edit unit is a sample (<paramref name="blockAlign"/> bytes), in pieces of <paramref name="pieceBytes"/>, the last
    /// shorter, whole samples only but where the file ends in it.
    /// </summary>
    private void SplitClip(int stream, MxfIndex? index, long pieceBytes, long blockAlign)
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
            // Sound: an edit unit a sample, so its size (the index's constant one, or the block alignment) is under the
            // element's.
            if (blockAlign <= 0 || blockAlign >= element.Size)
            {
                return;
            }

            // Whole samples only; but a clip the file ends in keeps its last bytes, as FFmpeg's demuxer gives them.
            long whole = at == _cut ? element.Size : element.Size - (element.Size % blockAlign);
            long piece = Math.Max(blockAlign, pieceBytes - (pieceBytes % blockAlign));
            for (long offset = 0; offset < whole; offset += piece)
            {
                pieces.Add((offset, Math.Min(piece, whole - offset)));
            }
        }
        else
        {
            pieces = index!.EditUnits(element.Size);
        }

        if (pieces.Count < 2)
        {
            return;
        }

        _clipWrapped = true;

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

    /// <summary>Times every packet (<see cref="Time"/>) and describes the streams by the packets found so far.</summary>
    private void Describe()
    {
        if (_video is { } video)
        {
            Time(video, _videoIndex, _indexes.TryGetValue(video.BodySid, out var index) ? index : null);
            Video = new VideoStreamInfo(
                VideoCodec(video.Codec),
                video.SampledWidth,
                video.SampledHeight,
                (double)video.EditRate.Numerator / video.EditRate.Denominator,
                _packets.Count(p => p.StreamIndex == _videoIndex),
                new Rational(video.EditRate.Denominator, video.EditRate.Numerator),
                ReadOnlyMemory<byte>.Empty,
                _videoIndex)
            {
                ColorRange = video.ColorRange,
                ColorPrimaries = video.ColorPrimaries,
                ColorTransfer = video.ColorTransfer,
                ColorMatrix = video.ColorMatrix,
            };
        }

        foreach (var sound in _sounds)
        {
            var audio = sound.Track;
            int rate = audio.AudioSamplingRate.Numerator;
            long samples = Time(audio, sound.Index, null, sound.Aes3Channels * sound.Aes3Bytes);
            sound.Info = new AudioStreamInfo(audio.Codec is { } codec ? AudioCodec(codec) : "unknown", rate, audio.Channels, _packets.Count(p => p.StreamIndex == sound.Index), new Rational(1, rate), ReadOnlyMemory<byte>.Empty, sound.Index)
            {
                Duration = samples,
            };
        }

        AudioTracks = [.. _sounds.Select(s => s.Info!)];
        Audio = _selected.Length == 0 ? null : _selected[0].Info;
    }

    /// <summary>
    /// Gives the track's packets their times, durations and keyframe flags (the class remarks say how); a sound sample
    /// is <paramref name="aes3BlockAlign"/> bytes where its elements are 8-channel AES3 ones, else the descriptor's.
    /// Returns a sound track's samples.
    /// </summary>
    private long Time(MxfTrack track, int stream, MxfIndex? index, int aes3BlockAlign = 0)
    {
        bool intra = track.Codec is "prores" or "dnxhd" or "jpeg2000" || MxfLabels.IsD10Container(track.EssenceContainer ?? []);
        int blockAlign = aes3BlockAlign > 0 ? aes3BlockAlign : BlockAlign(track);
        long samples = 0;

        // A reordered picture's temporal offsets (ST 377-1 11.2.3, Table 28) lead from display order to stored order:
        // the edit unit shown n-th is stored at n + TemporalOffset[n]. Decode times then start early by the largest
        // offset, so that no picture is shown before it is decoded (FFmpeg's demuxer: -1 where B pictures follow their
        // anchor, MPEG-2's and H.264's usual layout).
        var shown = new Dictionary<long, long>();
        long delay = 0;
        if (track.Kind == MxfTrackKind.Picture && index is { HasReorderedElement: true })
        {
            long units = _packets.Count(p => p.StreamIndex == stream);
            for (long n = 0; n < units; n++)
            {
                if (index.Entry(n) is { } displayed)
                {
                    shown[n + displayed.TemporalOffset] = n;
                    delay = Math.Max(delay, displayed.TemporalOffset);
                }
            }
        }

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
                long pts = shown.TryGetValue(unit, out long display) ? display : unit;
                bool keyframe = intra || (entry is { } f ? (f.Flags & MxfIndex.RandomAccess) != 0 : unit == 0);
                _packets[i] = packet with { Dts = unit - delay, Pts = pts, Duration = 1, IsKeyframe = keyframe };
            }
        }

        return samples;
    }

    /// <summary>
    /// A sound track read: its header track, its stream index, its 8-channel AES3 elements' channels and bytes a sample
    /// (D-10's; 0 where its elements are PCM), and what changes: its packets so far, where its read starts as a
    /// sequence (0 until a Seek moves it), and the stream as last described.
    /// </summary>
    private sealed class Sound(MxfTrack track, int index, int aes3Channels, int aes3Bytes)
    {
        public MxfTrack Track { get; } = track;

        public int Index { get; } = index;

        public int Aes3Channels { get; } = aes3Channels;

        public int Aes3Bytes { get; } = aes3Bytes;

        public long Count { get; set; }

        public long Start { get; set; }

        public AudioStreamInfo? Info { get; set; }
    }
}

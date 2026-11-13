using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// MXF's header metadata (SMPTE ST 377-1 9, Annexes A, B, F, G): every samples/*.mxf against the streams FFmpeg's
/// demuxer reads from it without decoding (samples/*.headers.json, <c>ffprobe -nofind_stream_info</c>), and the rules
/// FFmpeg's files never reach on hand-made header metadata.
/// </summary>
public class MxfHeaderTests
{
    public static TheoryData<string> Samples() =>
        new(TestSamples.Directory is null
            ? Enumerable.Empty<string>()
            : Directory.EnumerateFiles(TestSamples.Directory, "*.mxf").Order()
                .Where(f => File.Exists(Path.ChangeExtension(f, ".headers.json")))
                .Select(f => Path.GetFileName(f)));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegsStreams(string sampleName)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(TestSamples.ReferenceOf(sampleName, ".headers.json")));
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToList();
        var format = json.RootElement.GetProperty("format").GetProperty("tags");

        using var stream = File.OpenRead(TestSamples.PathOf(sampleName));
        var header = MxfHeader.Read(stream, MxfPartitions.Read(stream));

        Assert.Equal(format.GetProperty("operational_pattern_ul").GetString(), Dotted(header.OperationalPattern!));
        Assert.Equal(format.GetProperty("material_package_umid").GetString(), "0x" + Convert.ToHexString(header.MaterialPackageUmid!));
        Assert.Equal(format.GetProperty("timecode").GetString(), header.StartTimecode.ToString());

        // FFmpeg makes a stream of each essence track of the material package, in its order.
        Assert.Equal(streams.Count, header.Tracks.Count);
        for (int i = 0; i < streams.Count; i++)
        {
            var expected = streams[i];
            var track = header.Tracks[i];
            Assert.Equal(expected.GetProperty("codec_name").GetString(), track.Codec);
            Assert.Equal(
                "0x" + Convert.ToHexString(track.FilePackageUmid!),
                expected.GetProperty("tags").GetProperty("file_package_umid").GetString());
            Assert.NotEqual(0u, track.TrackNumber);
            Assert.NotEqual(0u, track.BodySid);

            if (expected.GetProperty("codec_type").GetString() == "video")
            {
                Assert.Equal(MxfTrackKind.Picture, track.Kind);
                Assert.Equal(expected.GetProperty("width").GetInt32(), track.Width);
                Assert.Equal(expected.GetProperty("height").GetInt32(), track.Height);

                // FFmpeg's demuxer gives no sample aspect ratio (its decoder does), and no field order for separate
                // fields (its decoder does): here only progressive or not.
                Assert.Equal(expected.TryGetProperty("field_order", out var order) && order.GetString() == "progressive", track.FrameLayout == 0);
                Assert.Equal(expected.GetProperty("time_base").GetString(), $"{track.EditRate.Denominator}/{track.EditRate.Numerator}");
                Assert.Equal(DurationTs(expected), track.Duration);
            }
            else
            {
                Assert.Equal(MxfTrackKind.Sound, track.Kind);
                var rate = track.AudioSamplingRate;
                Assert.Equal(1, rate.Denominator);
                Assert.Equal(expected.GetProperty("sample_rate").GetString(), rate.Numerator.ToString(CultureInfo.InvariantCulture));
                Assert.Equal(expected.GetProperty("channels").GetInt32(), track.Channels);
                Assert.Equal(expected.GetProperty("bits_per_sample").GetInt32(), track.QuantizationBits);

                // The track counts edit units; FFmpeg's audio duration counts samples, rescaled to the nearest
                // (6 frames at 29.97 are 9609.6 samples: 9610, mxf-d10-525).
                long? samples = ((2 * track.Duration * rate.Numerator * track.EditRate.Denominator) + track.EditRate.Numerator) / (2L * track.EditRate.Numerator);
                Assert.Equal(DurationTs(expected), samples);
            }
        }
    }

    [Theory]
    [InlineData(90000L, 25, false, "01:00:00:00")]
    [InlineData(0L, 30, true, "00:00:00;00")]
    [InlineData(1800L, 30, true, "00:01:00;02")] // frames 0 and 1 of minute 1 are dropped
    [InlineData(17982L, 30, true, "00:10:00;00")] // but not those of minute 10
    [InlineData(107892L, 30, true, "01:00:00;00")]
    [InlineData(107894L, 30, true, "01:00:00;02")]
    [InlineData(215784L, 60, true, "01:00:00;00")] // 59.94: four dropped a minute
    public void FormatsTimecodes(long start, int timecodeBase, bool dropFrame, string expected) =>
        Assert.Equal(expected, new MxfTimecode(start, timecodeBase, dropFrame).ToString());

    [Fact]
    public void FollowsTheReferencesToEachTracksDescriptor()
    {
        var file = new HeaderBuilder();
        file.Preface();
        file.Picture(trackId: 1, fileTrackId: 2, trackNumber: 0x15010500, layout: 1, storedHeight: 288, aspect: (4, 3));
        file.Sound(trackId: 2, fileTrackId: 3, trackNumber: 0x16010101, bits: 24);
        var header = MxfHeader.Build(file.Partition, file.Read());

        Assert.Equal(2, header.Tracks.Count);
        var picture = header.Tracks[0];
        Assert.Equal(MxfTrackKind.Picture, picture.Kind);
        Assert.Equal(0x15010500u, picture.TrackNumber);
        Assert.Equal(1u, picture.BodySid);
        Assert.Equal(2u, picture.IndexSid);
        Assert.Equal(720, picture.Width);

        // Separate fields: the stored height is a field's, the frame twice it; 4:3 over 720x576 is 16:15.
        Assert.Equal(576, picture.Height);
        Assert.Equal((16, 15), picture.SampleAspectRatio!.Value);
        Assert.Equal("h264", picture.Codec);
        Assert.Equal(250L, picture.Duration);

        var sound = header.Tracks[1];
        Assert.Equal(MxfTrackKind.Sound, sound.Kind);
        Assert.Equal(0x16010101u, sound.TrackNumber);
        Assert.Equal("pcm_s24le", sound.Codec);
        Assert.Equal((48000, 1), sound.AudioSamplingRate);
        Assert.Equal(2, sound.Channels);
        Assert.Equal(3u, sound.FileTrackId);
    }

    [Fact]
    public void ReadsPropertiesWithBerLengthsAndSkipsWhatThePrimerDoesNotMap()
    {
        var file = new HeaderBuilder { BerLengths = true };
        file.Preface();
        file.Picture(trackId: 1, fileTrackId: 2, trackNumber: 1, layout: 0, storedHeight: 576, aspect: null);
        file.UnmappedTag = true;
        file.Sound(trackId: 2, fileTrackId: 3, trackNumber: 2, bits: 16);
        var header = MxfHeader.Build(file.Partition, file.Read());

        Assert.Equal(2, header.Tracks.Count);
        Assert.Equal(576, header.Tracks[0].Height);
        Assert.Null(header.Tracks[0].SampleAspectRatio);
        Assert.Equal("pcm_s16le", header.Tracks[1].Codec);
    }

    [Fact]
    public void LeavesAnUnknownDurationUnknown()
    {
        var file = new HeaderBuilder { Duration = -1 };
        file.Preface();
        file.Picture(trackId: 1, fileTrackId: 2, trackNumber: 1, layout: 0, storedHeight: 576, aspect: null);
        Assert.Null(MxfHeader.Build(file.Partition, file.Read()).Tracks[0].Duration);
    }

    [Fact]
    public void RejectsHeaderMetadataThatDoesNotHoldTogether()
    {
        // No preface; a property that runs past its set; header metadata that doesn't start with a primer pack.
        var noPreface = new HeaderBuilder();
        noPreface.Picture(trackId: 1, fileTrackId: 2, trackNumber: 1, layout: 0, storedHeight: 576, aspect: null);
        Assert.Throws<InvalidDataException>(() => MxfHeader.Build(noPreface.Partition, noPreface.Read()));

        var overrun = new HeaderBuilder { OverrunProperty = true };
        overrun.Preface();
        Assert.Throws<InvalidDataException>(() => overrun.Read());

        var noPrimer = new HeaderBuilder { OmitPrimer = true };
        noPrimer.Preface();
        Assert.Throws<InvalidDataException>(() => noPrimer.Read());
    }

    [Fact]
    public void ChoosesClosedHeaderMetadata()
    {
        // An open header with metadata and a closed footer with some: the footer's, which is final (ST 377-1 6.2.3).
        byte[] header = Partition(kind: 2, status: 1, thisPartition: 0, headerBytes: 100);
        byte[] footer = Partition(kind: 4, status: 4, thisPartition: (ulong)header.Length, headerBytes: 100);
        var partitions = MxfPartitions.Read(new MemoryStream([.. header, .. footer]));
        Assert.Same(partitions.Partitions[1], MxfHeader.ChoosePartition(partitions));

        // A closed header is taken first; a closed footer without metadata leaves the open header.
        byte[] closed = Partition(kind: 2, status: 4, thisPartition: 0, headerBytes: 100);
        var closedFirst = MxfPartitions.Read(new MemoryStream([.. closed, .. footer]));
        Assert.Same(closedFirst.Header, MxfHeader.ChoosePartition(closedFirst));
        byte[] bare = Partition(kind: 4, status: 4, thisPartition: (ulong)header.Length, headerBytes: 0);
        var open = MxfPartitions.Read(new MemoryStream([.. header, .. bare]));
        Assert.Same(open.Header, MxfHeader.ChoosePartition(open));
    }

    private static long? DurationTs(JsonElement stream) =>
        stream.TryGetProperty("duration_ts", out var value) ? value.GetInt64() : null;

    private static string Dotted(byte[] label) =>
        string.Join('.', Enumerable.Range(0, 4).Select(i => Convert.ToHexString(label, i * 4, 4).ToLowerInvariant()));

    private static byte[] Partition(byte kind, byte status, ulong thisPartition, ulong headerBytes)
    {
        var value = new byte[88];
        BinaryPrimitives.WriteUInt16BigEndian(value, 1);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(8), thisPartition);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(32), headerBytes);
        byte[] key = Convert.FromHexString("060E2B34020501010D01020101000000");
        key[13] = kind;
        key[14] = status;
        return [.. key, (byte)value.Length, .. value];
    }

    /// <summary>
    /// Builds a header partition with header metadata as ST 377-1 lays it out: a preface, a content storage, a material
    /// package whose tracks' source clips name a file package's tracks, that package's multiple descriptor, and an
    /// EssenceContainerData. Each set's properties use the static local tags of Annex H, which the primer maps.
    /// </summary>
    private sealed class HeaderBuilder
    {
        private static readonly byte[] MaterialUmid = [.. Enumerable.Repeat((byte)0xA1, 32)];
        private static readonly byte[] FileUmid = [.. Enumerable.Repeat((byte)0xF1, 32)];

        private readonly List<byte[]> _sets = [];
        private readonly List<(byte[] Uid, uint TrackId, MxfTrackKind Kind)> _materialTracks = [];
        private readonly List<byte[]> _fileTracks = [];
        private readonly List<byte[]> _subDescriptors = [];
        private readonly Dictionary<ushort, byte[]> _primer = [];
        private byte _next = 1;
        private bool _hasPreface;
        private MxfPartition? _partition;
        private MemoryStream? _stream;

        public bool BerLengths { get; init; }

        public long Duration { get; init; } = 250;

        public bool OverrunProperty { get; init; }

        public bool OmitPrimer { get; init; }

        /// <summary>Gives the next set a property whose tag the primer doesn't list.</summary>
        public bool UnmappedTag { get; set; }

        public MxfPartition Partition => _partition ?? Build();

        public MxfHeaderMetadata Read()
        {
            Build();
            return MxfHeaderMetadata.Read(_stream!, _partition!);
        }

        public void Preface() => _hasPreface = true;

        public void Picture(uint trackId, uint fileTrackId, uint trackNumber, byte layout, uint storedHeight, (int, int)? aspect)
        {
            var props = new List<(ushort, byte[])>
            {
                (0x3006, U32(fileTrackId)),
                (0x3004, Convert.FromHexString("060E2B340401010A0D01030102106001")),
                (0x3203, U32(720)),
                (0x3202, U32(storedHeight)),
                (0x320C, [layout]),
                (0x3201, Convert.FromHexString("060E2B34040101090401020201314001")),
            };
            if (aspect is { } a)
            {
                props.Add((0x320E, [.. I32(a.Item1), .. I32(a.Item2)]));
            }

            AddEssence(trackId, fileTrackId, trackNumber, MxfTrackKind.Picture, 0x0128, props);
        }

        public void Sound(uint trackId, uint fileTrackId, uint trackNumber, uint bits)
        {
            AddEssence(trackId,
            fileTrackId,
            trackNumber,
            MxfTrackKind.Sound,
            0x0147,
            [
                (0x3006, U32(fileTrackId)),
                (0x3004, Convert.FromHexString("060E2B34040101010D01030102060100")),
                (0x3D03, [.. I32(48000), .. I32(1)]),
                (0x3D07, U32(2)),
                (0x3D01, U32(bits)),
            ]);
        }

        private void AddEssence(uint trackId, uint fileTrackId, uint trackNumber, MxfTrackKind kind, int descriptorKind, List<(ushort, byte[])> descriptorProps)
        {
            byte[] dataDefinition = Convert.FromHexString(kind == MxfTrackKind.Picture ? "060E2B34040101010103020201000000" : "060E2B34040101010103020202000000");

            // The file package's track and its sequence and clip.
            byte[] fileClip = Set(0x0111, [(0x0201, dataDefinition), (0x0202, I64(Duration)), (0x1201, I64(0)), (0x1101, new byte[32]), (0x1102, U32(0))]);
            byte[] fileSequence = Set(0x010F, [(0x0201, dataDefinition), (0x0202, I64(Duration)), (0x1001, Batch(fileClip))]);
            _fileTracks.Add(Set(0x013B, [(0x4801, U32(fileTrackId)), (0x4804, U32(trackNumber)), (0x4B01, [.. I32(25), .. I32(1)]), (0x4803, fileSequence)]));

            // The material package's, whose clip names the file package's track.
            byte[] clip = Set(0x0111, [(0x0201, dataDefinition), (0x0202, I64(Duration)), (0x1201, I64(0)), (0x1101, FileUmid), (0x1102, U32(fileTrackId))]);
            byte[] sequence = Set(0x010F, [(0x0201, dataDefinition), (0x0202, I64(Duration)), (0x1001, Batch(clip))]);
            byte[] track = Set(0x013B, [(0x4801, U32(trackId)), (0x4804, U32(0)), (0x4B01, [.. I32(25), .. I32(1)]), (0x4803, sequence)]);
            _materialTracks.Add((track, trackId, kind));

            _subDescriptors.Add(Set(descriptorKind, descriptorProps));
        }

        private MxfPartition Build()
        {
            if (_partition is not null)
            {
                return _partition;
            }

            byte[] multiple = Set(0x0144, [(0x3001, [.. I32(25), .. I32(1)]), (0x3F01, Batch([.. _subDescriptors]))]);
            byte[] filePackage = Set(0x0137, [(0x4401, FileUmid), (0x4403, Batch([.. _fileTracks])), (0x4701, multiple)]);
            byte[] material = Set(0x0136, [(0x4401, MaterialUmid), (0x4403, Batch([.. _materialTracks.Select(t => t.Uid)]))]);
            byte[] data = Set(0x0123, [(0x2701, FileUmid), (0x3F07, U32(1)), (0x3F06, U32(2))]);
            byte[] storage = Set(0x0118, [(0x1901, Batch(material, filePackage)), (0x1902, Batch(data))]);
            if (_hasPreface)
            {
                Set(0x012F, [(0x3B03, storage), (0x3B09, Convert.FromHexString("060E2B34040101010D01020101010900"))]);
            }

            // The primer, with every tag the sets use mapped to its Annex H label (but the unmapped one).
            var primer = new List<byte>();
            foreach (var (tag, label) in _primer)
            {
                primer.AddRange([(byte)(tag >> 8), (byte)tag, .. label]);
            }

            byte[] primerValue = [.. U32((uint)_primer.Count), .. U32(18), .. primer];
            byte[] metadata = [.. OmitPrimer ? Array.Empty<byte>() : Klv("060E2B34020501010D01020101050100", primerValue), .. _sets.SelectMany(s => s)];

            byte[] partitionValue = new byte[88];
            BinaryPrimitives.WriteUInt16BigEndian(partitionValue, 1);
            BinaryPrimitives.WriteUInt64BigEndian(partitionValue.AsSpan(32), (ulong)metadata.Length);
            byte[] pack = [.. Convert.FromHexString("060E2B34020501010D01020101020400"), (byte)partitionValue.Length, .. partitionValue];
            byte[] file = [.. pack, .. metadata];
            _stream = new MemoryStream(file);
            _partition = MxfPartition.Read(_stream, 0);
            return _partition;
        }

        /// <summary>Adds a set and returns its InstanceUID; the properties' labels come from Annex H's static tags.</summary>
        private byte[] Set(int kind, List<(ushort Tag, byte[] Value)> properties)
        {
            byte[] uid = [.. Enumerable.Repeat((byte)0x55, 15), _next++];
            var value = new List<byte>();
            void Add(ushort tag, byte[] v, bool mapped = true)
            {
                if (mapped)
                {
                    _primer[tag] = StaticLabels[tag];
                }

                value.AddRange([(byte)(tag >> 8), (byte)tag]);
                if (BerLengths)
                {
                    value.AddRange([0x83, (byte)(v.Length >> 16), (byte)(v.Length >> 8), (byte)v.Length]);
                }
                else
                {
                    int length = OverrunProperty ? v.Length + 100 : v.Length;
                    value.AddRange([(byte)(length >> 8), (byte)length]);
                }

                value.AddRange(v);
            }

            Add(0x3C0A, uid);
            if (UnmappedTag)
            {
                Add(0x8001, [1, 2, 3], mapped: false);
                UnmappedTag = false;
            }

            foreach (var (tag, v) in properties)
            {
                Add(tag, v);
            }

            byte[] key = Convert.FromHexString("060E2B34025301010D01010101000000");
            key[5] = BerLengths ? (byte)0x13 : (byte)0x53;
            key[13] = (byte)(kind >> 8);
            key[14] = (byte)kind;
            _sets.Add(Klv(Convert.ToHexString(key), [.. value]));
            return uid;
        }

        private static byte[] Klv(string key, byte[] value) =>
            [.. Convert.FromHexString(key), 0x83, (byte)(value.Length >> 16), (byte)(value.Length >> 8), (byte)value.Length, .. value];

        private static byte[] Batch(params byte[][] items) => [.. U32((uint)items.Length), .. U32(16), .. items.SelectMany(i => i)];

        private static byte[] U32(uint v)
        {
            byte[] b = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            return b;
        }

        private static byte[] I32(int v) => U32((uint)v);

        private static byte[] I64(long v)
        {
            byte[] b = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, v);
            return b;
        }

        // ST 377-1 Annex H's labels for the static tags these sets use.
        private static readonly Dictionary<ushort, byte[]> StaticLabels = new()
        {
            [0x3C0A] = Convert.FromHexString("060E2B34010101010101150200000000"),
            [0x3B03] = Convert.FromHexString("060E2B34010101020601010402010000"),
            [0x3B09] = Convert.FromHexString("060E2B34010101050102020300000000"),
            [0x1901] = Convert.FromHexString("060E2B34010101020601010405010000"),
            [0x1902] = Convert.FromHexString("060E2B34010101020601010405020000"),
            [0x2701] = Convert.FromHexString("060E2B34010101020601010601000000"),
            [0x3F06] = Convert.FromHexString("060E2B34010101040103040500000000"),
            [0x3F07] = Convert.FromHexString("060E2B34010101040103040400000000"),
            [0x4401] = Convert.FromHexString("060E2B34010101010101151000000000"),
            [0x4403] = Convert.FromHexString("060E2B34010101020601010406050000"),
            [0x4701] = Convert.FromHexString("060E2B34010101020601010402030000"),
            [0x4801] = Convert.FromHexString("060E2B34010101020107010100000000"),
            [0x4804] = Convert.FromHexString("060E2B34010101020104010300000000"),
            [0x4803] = Convert.FromHexString("060E2B34010101020601010402040000"),
            [0x4B01] = Convert.FromHexString("060E2B34010101020530040500000000"),
            [0x0201] = Convert.FromHexString("060E2B34010101020407010000000000"),
            [0x0202] = Convert.FromHexString("060E2B34010101020702020101030000"),
            [0x1001] = Convert.FromHexString("060E2B34010101020601010406090000"),
            [0x1201] = Convert.FromHexString("060E2B34010101020702010301040000"),
            [0x1101] = Convert.FromHexString("060E2B34010101020601010301000000"),
            [0x1102] = Convert.FromHexString("060E2B34010101020601010302000000"),
            [0x3006] = Convert.FromHexString("060E2B34010101050601010305000000"),
            [0x3001] = Convert.FromHexString("060E2B34010101010406010100000000"),
            [0x3004] = Convert.FromHexString("060E2B34010101020601010401020000"),
            [0x3F01] = Convert.FromHexString("060E2B340101010406010104060B0000"),
            [0x3203] = Convert.FromHexString("060E2B34010101010401050202000000"),
            [0x3202] = Convert.FromHexString("060E2B34010101010401050201000000"),
            [0x320C] = Convert.FromHexString("060E2B34010101010401030104000000"),
            [0x320E] = Convert.FromHexString("060E2B34010101010401010101000000"),
            [0x3201] = Convert.FromHexString("060E2B34010101020401060100000000"),
            [0x3D03] = Convert.FromHexString("060E2B34010101050402030101010000"),
            [0x3D07] = Convert.FromHexString("060E2B34010101050402010104000000"),
            [0x3D01] = Convert.FromHexString("060E2B34010101040402030304000000"),
        };
    }
}

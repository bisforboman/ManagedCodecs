namespace ManagedCodecs.Containers;

/// <summary>What a track holds, by its sequence's data definition.</summary>
internal enum MxfTrackKind
{
    Picture,
    Sound,
    Data,
}

/// <summary>
/// One essence track as the header metadata describes it: the material package's track (what plays), the file
/// package's track its source clip names (where the essence is: its track number and the essence container's BodySID),
/// and the file descriptor of that track.
/// </summary>
internal sealed record MxfTrack
{
    public MxfTrackKind Kind { get; init; }

    /// <summary>The material package track's TrackID.</summary>
    public uint TrackId { get; init; }

    /// <summary>The material package track's edit rate (ST 377-1 B.11): its durations and positions count these.</summary>
    public (int Numerator, int Denominator) EditRate { get; init; }

    /// <summary>The material package track's sequence duration in edit units, or null where unknown (-1, or absent).</summary>
    public long? Duration { get; init; }

    /// <summary>The source clip's start in the file package track, in edit units.</summary>
    public long StartPosition { get; init; }

    /// <summary>The file package's UMID (32 bytes), or null if the source clip names no package in the file.</summary>
    public byte[]? FilePackageUmid { get; init; }

    /// <summary>The file package track's TrackID, which its descriptor's LinkedTrackID names.</summary>
    public uint FileTrackId { get; init; }

    /// <summary>The file package track's TrackNumber: the last four bytes of its essence element keys.</summary>
    public uint TrackNumber { get; init; }

    /// <summary>The essence container's BodySID (EssenceContainerData), or 0 for none in the file.</summary>
    public uint BodySid { get; init; }

    public uint IndexSid { get; init; }

    /// <summary>The descriptor's essence container label, or null.</summary>
    public byte[]? EssenceContainer { get; init; }

    /// <summary>ffprobe's name for the codec, or null where the labels name none known (<see cref="MxfLabels"/>).</summary>
    public string? Codec { get; init; }

    /// <summary>The picture's width, or 0.</summary>
    public int Width { get; init; }

    /// <summary>The picture's height: a frame's, twice the stored field height for separate fields (ST 377-1 G.2.1); or 0.</summary>
    public int Height { get; init; }

    /// <summary>The sample aspect ratio the descriptor's AspectRatio and display size give, reduced; or null without one.</summary>
    public (int Numerator, int Denominator)? SampleAspectRatio { get; init; }

    /// <summary>The descriptor's FrameLayout (ST 377-1 G.2.1: 0 full frame, 1 separate fields, ...), or null.</summary>
    public byte? FrameLayout { get; init; }

    /// <summary>The descriptor's FieldDominance (ST 377-1 G.2.19), or null where it gives none.</summary>
    public byte? FieldDominance { get; init; }

    /// <summary>The CDCI descriptor's ComponentDepth, HorizontalSubsampling and VerticalSubsampling, or 0.</summary>
    public int ComponentDepth { get; init; }

    public int HorizontalSubsampling { get; init; }

    public int VerticalSubsampling { get; init; }

    /// <summary>The sound's sampling rate, or (0, 0).</summary>
    public (int Numerator, int Denominator) AudioSamplingRate { get; init; }

    public int Channels { get; init; }

    public int QuantizationBits { get; init; }

    public int BlockAlign { get; init; }
}

/// <summary>A timecode component's start (SMPTE ST 377-1 B.17): frames since midnight at a rounded base, and drop frame.</summary>
internal readonly record struct MxfTimecode(long Start, int Base, bool DropFrame)
{
    /// <summary>hh:mm:ss:ff (hh:mm:ss;ff for drop frame, counted as SMPTE ST 12-1 drops frames 0 and 1 each minute but every tenth, scaled to the base).</summary>
    public override string ToString()
    {
        if (Base <= 0)
        {
            return "00:00:00:00";
        }

        long frames = Start;
        if (DropFrame && Base % 30 == 0)
        {
            // Frame numbers skip `drop` codes a minute, except every tenth minute: add them back to count codes.
            int drop = 2 * (Base / 30);
            long perTenMinutes = (Base * 600L) - (9L * drop);
            long perMinute = (Base * 60L) - drop;
            long tens = frames / perTenMinutes;
            long rest = frames % perTenMinutes;
            frames += (9L * drop * tens) + (rest > drop ? drop * ((rest - drop) / perMinute) : 0);
        }

        long ff = frames % Base;
        long seconds = frames / Base;
        return $"{(seconds / 3600) % 24:00}:{(seconds / 60) % 60:00}:{seconds % 60:00}{(DropFrame ? ';' : ':')}{ff:00}";
    }
}

/// <summary>
/// An MXF file's structural metadata (SMPTE ST 377-1 9.4, 9.5): the essence tracks of its material package, each
/// followed by its source clip to the top-level file package track and descriptor that describe its essence. Read from
/// the header partition's header metadata when that partition is closed, else from the last closed partition that has
/// some (ST 377-1 6.2.3: closed partitions' header metadata is final and the same in each), else from the open header.
/// </summary>
internal sealed class MxfHeader
{
    private MxfHeader(MxfPartition source, byte[]? operationalPattern, byte[]? materialPackageUmid, MxfTimecode? startTimecode, IReadOnlyList<MxfTrack> tracks)
    {
        Source = source;
        OperationalPattern = operationalPattern;
        MaterialPackageUmid = materialPackageUmid;
        StartTimecode = startTimecode;
        Tracks = tracks;
    }

    /// <summary>The partition whose header metadata this is.</summary>
    public MxfPartition Source { get; }

    public byte[]? OperationalPattern { get; }

    public byte[]? MaterialPackageUmid { get; }

    /// <summary>The material package's first timecode track's start, or null without one.</summary>
    public MxfTimecode? StartTimecode { get; }

    /// <summary>The material package's picture, sound and data tracks, in its order.</summary>
    public IReadOnlyList<MxfTrack> Tracks { get; }

    /// <summary>The header metadata of the file whose partitions are <paramref name="partitions"/>.</summary>
    /// <exception cref="InvalidDataException">No partition has header metadata, it has no preface, or it is malformed.</exception>
    public static MxfHeader Read(Stream stream, MxfPartitions partitions)
    {
        var source = ChoosePartition(partitions);
        return Build(source, MxfHeaderMetadata.Read(stream, source));
    }

    /// <summary>The partition whose header metadata to read (the class remarks say which).</summary>
    public static MxfPartition ChoosePartition(MxfPartitions partitions)
    {
        var header = partitions.Header;
        if (header.IsClosed && header.HeaderByteCount > 0)
        {
            return header;
        }

        for (int i = partitions.Partitions.Count - 1; i > 0; i--)
        {
            var partition = partitions.Partitions[i];
            if (partition.IsClosed && partition.HeaderByteCount > 0)
            {
                return partition;
            }
        }

        return header;
    }

    /// <summary>The structure <paramref name="metadata"/> describes.</summary>
    public static MxfHeader Build(MxfPartition source, MxfHeaderMetadata metadata)
    {
        var preface = metadata.Sets.FirstOrDefault(s => s.Kind == MxfSetKind.Preface)
            ?? throw new InvalidDataException("The MXF header metadata has no preface.");
        var storage = metadata.Resolve(preface.Reference(MxfLabels.ContentStorage))
            ?? throw new InvalidDataException("The MXF preface names no content storage set.");

        var packages = storage.References(MxfLabels.Packages).Select(metadata.Resolve).OfType<MxfSet>().ToList();
        var material = packages.FirstOrDefault(p => p.Kind == MxfSetKind.MaterialPackage);

        // Each essence container's stream IDs, by the package its EssenceContainerData links.
        var streams = new Dictionary<string, (uint Body, uint Index)>();
        foreach (var data in storage.References(MxfLabels.EssenceContainerData).Select(metadata.Resolve).OfType<MxfSet>())
        {
            if (data.Get(MxfLabels.LinkedPackageUid) is { } linked)
            {
                streams.TryAdd(Convert.ToHexString(linked), (data.UInt32(MxfLabels.BodySid) ?? 0, data.UInt32(MxfLabels.IndexSid) ?? 0));
            }
        }

        var filePackages = new Dictionary<string, MxfSet>();
        foreach (var package in packages.Where(p => p.Kind == MxfSetKind.SourcePackage))
        {
            if (package.Get(MxfLabels.PackageUid) is { } umid)
            {
                filePackages.TryAdd(Convert.ToHexString(umid), package);
            }
        }

        var tracks = new List<MxfTrack>();
        MxfTimecode? timecode = null;
        foreach (var track in material?.References(MxfLabels.Tracks).Select(metadata.Resolve).OfType<MxfSet>() ?? [])
        {
            if (track.Kind != MxfSetKind.TimelineTrack || metadata.Resolve(track.Reference(MxfLabels.Sequence)) is not { } sequence)
            {
                continue;
            }

            int kind = DataKind(sequence);
            var components = sequence.Kind == MxfSetKind.Sequence
                ? sequence.References(MxfLabels.StructuralComponents).Select(metadata.Resolve).OfType<MxfSet>().ToList()
                : [sequence];
            if (MxfLabels.IsTimecodeKind(kind))
            {
                if (timecode is null && components.FirstOrDefault(c => c.Kind == MxfSetKind.TimecodeComponent) is { } component)
                {
                    timecode = new MxfTimecode(component.Int64(MxfLabels.StartTimecode) ?? 0, component.UInt16(MxfLabels.RoundedTimecodeBase) ?? 0, component.UInt8(MxfLabels.DropFrame) is > 0);
                }

                continue;
            }

            if (kind is not (MxfLabels.PictureKind or MxfLabels.SoundKind or MxfLabels.DataKindValue))
            {
                continue;
            }

            var clip = components.FirstOrDefault(c => c.Kind == MxfSetKind.SourceClip);
            tracks.Add(Describe(metadata, track, sequence, clip, kind, filePackages, streams));
        }

        return new MxfHeader(source, preface.Get(MxfLabels.OperationalPattern), material?.Get(MxfLabels.PackageUid), timecode, tracks);
    }

    private static int DataKind(MxfSet component) => component.Get(MxfLabels.DataDefinition) is { } label ? MxfLabels.DataKind(label) : 0;

    private static MxfTrack Describe(MxfHeaderMetadata metadata, MxfSet track, MxfSet sequence, MxfSet? clip, int kind, Dictionary<string, MxfSet> filePackages, Dictionary<string, (uint Body, uint Index)> streams)
    {
        long? duration = sequence.Int64(MxfLabels.Duration) is { } d and >= 0 ? d : null;
        byte[]? umid = clip?.Get(MxfLabels.SourcePackageId);
        uint fileTrackId = clip?.UInt32(MxfLabels.SourceTrackId) ?? 0;
        string? umidKey = umid is null ? null : Convert.ToHexString(umid);

        uint trackNumber = 0;
        MxfSet? descriptor = null;
        (uint Body, uint Index) sids = default;
        if (umidKey is not null && filePackages.TryGetValue(umidKey, out var package))
        {
            var fileTrack = package.References(MxfLabels.Tracks).Select(metadata.Resolve).OfType<MxfSet>()
                .FirstOrDefault(t => t.UInt32(MxfLabels.TrackId) == fileTrackId);
            trackNumber = fileTrack?.UInt32(MxfLabels.TrackNumber) ?? 0;
            descriptor = FindDescriptor(metadata, metadata.Resolve(package.Reference(MxfLabels.Descriptor)), fileTrackId);
            streams.TryGetValue(umidKey, out sids);
        }
        else
        {
            umid = null;
        }

        var trackKind = kind switch
        {
            MxfLabels.PictureKind => MxfTrackKind.Picture,
            MxfLabels.SoundKind => MxfTrackKind.Sound,
            _ => MxfTrackKind.Data,
        };
        var common = new MxfTrack
        {
            Kind = trackKind,
            TrackId = track.UInt32(MxfLabels.TrackId) ?? 0,
            EditRate = track.Rational(MxfLabels.EditRate) ?? (0, 0),
            Duration = duration,
            StartPosition = clip?.Int64(MxfLabels.StartPosition) ?? 0,
            FilePackageUmid = umid,
            FileTrackId = fileTrackId,
            TrackNumber = trackNumber,
            BodySid = sids.Body,
            IndexSid = sids.Index,
            EssenceContainer = descriptor?.Get(MxfLabels.EssenceContainer),
        };

        return descriptor is null ? common : trackKind switch
        {
            MxfTrackKind.Picture => DescribePicture(common, descriptor),
            MxfTrackKind.Sound => DescribeSound(common, descriptor),
            _ => common,
        };
    }

    /// <summary>
    /// The descriptor of the file package track <paramref name="trackId"/>: the package's own, or, for a multiple
    /// descriptor (ST 377-1 F.3), the sub-descriptor whose LinkedTrackID names the track.
    /// </summary>
    private static MxfSet? FindDescriptor(MxfHeaderMetadata metadata, MxfSet? descriptor, uint trackId)
    {
        if (descriptor?.Kind != MxfSetKind.MultipleDescriptor)
        {
            return descriptor;
        }

        var subDescriptors = descriptor.References(MxfLabels.SubDescriptors).Select(metadata.Resolve).OfType<MxfSet>().ToList();
        return subDescriptors.FirstOrDefault(d => d.UInt32(MxfLabels.LinkedTrackId) == trackId)
            ?? (subDescriptors.Count == 1 ? subDescriptors[0] : null);
    }

    private static MxfTrack DescribePicture(MxfTrack common, MxfSet descriptor)
    {
        byte? layout = descriptor.UInt8(MxfLabels.FrameLayout);

        // Separate fields store a field (ST 377-1 G.2.1): the frame is twice as tall. The display rectangle too.
        int fields = layout == 1 ? 2 : 1;
        int width = (int)Math.Min(descriptor.UInt32(MxfLabels.StoredWidth) ?? 0, int.MaxValue);
        int height = (int)Math.Min((descriptor.UInt32(MxfLabels.StoredHeight) ?? 0) * (ulong)fields, int.MaxValue);
        long displayWidth = descriptor.UInt32(MxfLabels.DisplayWidth) ?? descriptor.UInt32(MxfLabels.SampledWidth) ?? (uint)width;
        long displayHeight = (descriptor.UInt32(MxfLabels.DisplayHeight) ?? descriptor.UInt32(MxfLabels.SampledHeight) ?? (uint)(height / fields)) * (long)fields;

        (int, int)? sampleAspect = null;
        if (descriptor.Rational(MxfLabels.AspectRatio) is { Numerator: > 0, Denominator: > 0 } aspect && displayWidth > 0 && displayHeight > 0)
        {
            // The picture's aspect over the display rectangle's: (num / den) / (width / height).
            sampleAspect = Reduce(checked(aspect.Numerator * displayHeight), checked(aspect.Denominator * displayWidth));
        }

        byte[] coding = descriptor.Get(MxfLabels.PictureEssenceCoding) ?? [];
        return common with
        {
            Codec = MxfLabels.PictureCodec(coding, common.EssenceContainer ?? []),
            Width = width,
            Height = height,
            SampleAspectRatio = sampleAspect,
            FrameLayout = layout,
            FieldDominance = descriptor.UInt8(MxfLabels.FieldDominance),
            ComponentDepth = (int)Math.Min(descriptor.UInt32(MxfLabels.ComponentDepth) ?? 0, int.MaxValue),
            HorizontalSubsampling = (int)Math.Min(descriptor.UInt32(MxfLabels.HorizontalSubsampling) ?? 0, int.MaxValue),
            VerticalSubsampling = (int)Math.Min(descriptor.UInt32(MxfLabels.VerticalSubsampling) ?? 0, int.MaxValue),
        };
    }

    private static MxfTrack DescribeSound(MxfTrack common, MxfSet descriptor)
    {
        int bits = (int)Math.Min(descriptor.UInt32(MxfLabels.QuantizationBits) ?? 0, int.MaxValue);
        return common with
        {
            Codec = MxfLabels.SoundCodec(common.EssenceContainer ?? [], bits),
            AudioSamplingRate = descriptor.Rational(MxfLabels.AudioSamplingRate) ?? (0, 0),
            Channels = (int)Math.Min(descriptor.UInt32(MxfLabels.ChannelCount) ?? 0, int.MaxValue),
            QuantizationBits = bits,
            BlockAlign = descriptor.UInt16(MxfLabels.BlockAlign) ?? 0,
        };
    }

    private static (int, int) Reduce(long numerator, long denominator)
    {
        long a = numerator;
        long b = denominator;
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return (checked((int)(numerator / a)), checked((int)(denominator / a)));
    }
}

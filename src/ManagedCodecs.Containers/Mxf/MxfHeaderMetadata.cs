using System.Buffers.Binary;

namespace ManagedCodecs.Containers;

/// <summary>A structural metadata set: its key's bytes 14 and 15 (SMPTE ST 377-1 Table 17; SMPTE ST 382 for the PCM descriptors).</summary>
internal enum MxfSetKind
{
    None = 0,
    Sequence = 0x010F,
    SourceClip = 0x0111,
    TimecodeComponent = 0x0114,
    ContentStorage = 0x0118,
    EssenceContainerData = 0x0123,
    FileDescriptor = 0x0125,
    PictureDescriptor = 0x0127,
    CdciDescriptor = 0x0128,
    RgbaDescriptor = 0x0129,
    Preface = 0x012F,
    MaterialPackage = 0x0136,
    SourcePackage = 0x0137,
    TimelineTrack = 0x013B,
    SoundDescriptor = 0x0142,
    DataDescriptor = 0x0143,
    MultipleDescriptor = 0x0144,
    Aes3Descriptor = 0x0147,
    WaveDescriptor = 0x0148,
}

/// <summary>One header metadata set: its kind, its InstanceUID and its properties by label (version byte cleared).</summary>
internal sealed class MxfSet
{
    private readonly Dictionary<Label, byte[]> _properties;

    public MxfSet(MxfSetKind kind, byte[] key, Dictionary<Label, byte[]> properties)
    {
        Kind = kind;
        Key = key;
        _properties = properties;
        InstanceUid = Get(MxfLabels.InstanceUid) is { Length: Klv.KeyLength } uid ? new Label(uid) : default;
    }

    public MxfSetKind Kind { get; }

    public byte[] Key { get; }

    public Label InstanceUid { get; }

    /// <summary>The value of the property <paramref name="label"/> names, or null if the set has none.</summary>
    public byte[]? Get(ReadOnlySpan<byte> label) => _properties.TryGetValue(new Label(label).WithoutVersion(), out var value) ? value : null;

    public uint? UInt32(ReadOnlySpan<byte> label) => Get(label) is { Length: 4 } v ? BinaryPrimitives.ReadUInt32BigEndian(v) : null;

    public ushort? UInt16(ReadOnlySpan<byte> label) => Get(label) is { Length: 2 } v ? BinaryPrimitives.ReadUInt16BigEndian(v) : null;

    public byte? UInt8(ReadOnlySpan<byte> label) => Get(label) is { Length: 1 } v ? v[0] : null;

    public long? Int64(ReadOnlySpan<byte> label) => Get(label) is { Length: 8 } v ? BinaryPrimitives.ReadInt64BigEndian(v) : null;

    /// <summary>A rational (ST 377-1 4.3: two Int32, numerator first), or null if absent or not one.</summary>
    public (int Numerator, int Denominator)? Rational(ReadOnlySpan<byte> label) =>
        Get(label) is { Length: 8 } v ? (BinaryPrimitives.ReadInt32BigEndian(v), BinaryPrimitives.ReadInt32BigEndian(v.AsSpan(4))) : null;

    /// <summary>A 16-byte reference (a strong reference, a UL), or null.</summary>
    public Label? Reference(ReadOnlySpan<byte> label) => Get(label) is { Length: Klv.KeyLength } v ? new Label(v) : null;

    /// <summary>
    /// An array or batch of 16-byte items (ST 377-1 4.3: a UInt32 count, a UInt32 item length, the items), or empty if
    /// absent or not one.
    /// </summary>
    public IReadOnlyList<Label> References(ReadOnlySpan<byte> label)
    {
        if (Get(label) is not { Length: >= 8 } v)
        {
            return [];
        }

        uint count = BinaryPrimitives.ReadUInt32BigEndian(v);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(v.AsSpan(4));
        if (length != Klv.KeyLength || count > (uint)(v.Length - 8) / Klv.KeyLength)
        {
            return [];
        }

        var items = new Label[count];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = new Label(v.AsSpan(8 + (i * Klv.KeyLength), Klv.KeyLength));
        }

        return items;
    }
}

/// <summary>
/// A 16-byte label or ID, compared by value; <see cref="WithoutVersion"/> clears a UL's byte 8 (its registry version),
/// which is how properties are looked up (<see cref="Klv.Matches"/>).
/// </summary>
internal readonly struct Label : IEquatable<Label>
{
    private readonly ulong _high;
    private readonly ulong _low;

    public Label(ReadOnlySpan<byte> bytes)
    {
        _high = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        _low = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
    }

    private Label(ulong high, ulong low)
    {
        _high = high;
        _low = low;
    }

    public bool IsZero => _high == 0 && _low == 0;

    /// <summary>The label with its byte 8 cleared, for comparing ULs across registry versions.</summary>
    public Label WithoutVersion() => new(_high & ~0xFFul, _low);

    public byte[] ToArray()
    {
        byte[] bytes = new byte[Klv.KeyLength];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, _high);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), _low);
        return bytes;
    }

    /// <inheritdoc/>
    public bool Equals(Label other) => _high == other._high && _low == other._low;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Label other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_high, _low);

    /// <inheritdoc/>
    public override string ToString() => Convert.ToHexString(ToArray());
}

/// <summary>
/// A partition's header metadata (SMPTE ST 377-1 9.1 to 9.3): the primer pack's local tags, then the sets, each
/// a local set of 2-byte tags with 2-byte or BER lengths, its properties keyed by the labels the primer gives their
/// tags. Sets that are not structural metadata (dark or descriptive metadata) are skipped, and so are properties whose
/// tag the primer doesn't map.
/// </summary>
internal sealed class MxfHeaderMetadata
{
    private readonly Dictionary<Label, MxfSet> _byInstance;

    private MxfHeaderMetadata(List<MxfSet> sets)
    {
        Sets = sets;
        _byInstance = [];
        foreach (var set in sets)
        {
            if (!set.InstanceUid.IsZero)
            {
                // A repeated InstanceUID (ST 377-1 9.3.3 has them unique): the first set keeps it.
                _byInstance.TryAdd(set.InstanceUid, set);
            }
        }
    }

    public IReadOnlyList<MxfSet> Sets { get; }

    /// <summary>The set a strong reference names, or null if none has that InstanceUID.</summary>
    public MxfSet? Resolve(Label? reference) => reference is { } r ? Resolve(r) : null;

    /// <summary>The set a strong reference names, or null if none has that InstanceUID.</summary>
    public MxfSet? Resolve(Label reference) => _byInstance.TryGetValue(reference, out var set) ? set : null;

    /// <summary>The partition's header metadata, from after its pack (and a fill item there) for its HeaderByteCount.</summary>
    /// <exception cref="InvalidDataException">The partition has none, or it is malformed.</exception>
    /// <exception cref="EndOfStreamException">The file ends inside it.</exception>
    public static MxfHeaderMetadata Read(Stream stream, MxfPartition partition)
    {
        if (partition.HeaderByteCount == 0)
        {
            throw new InvalidDataException($"The MXF partition at byte {partition.Position} has no header metadata.");
        }

        Span<byte> key = stackalloc byte[Klv.KeyLength];
        long position = partition.End;

        // One fill item may come between the partition pack and the primer pack (9.1); HeaderByteCount starts after it.
        long valueStart = Klv.ReadHeader(stream, position, key, out long length);
        if (Klv.IsFill(key))
        {
            position = checked(valueStart + length);
            valueStart = Klv.ReadHeader(stream, position, key, out length);
        }

        long end = checked(position + (long)Math.Min(partition.HeaderByteCount, (ulong)long.MaxValue));
        if (end > stream.Length)
        {
            throw new InvalidDataException($"The MXF header metadata at byte {position} runs past the end of the file.");
        }

        if (!Klv.Matches(key, MxfLabels.PrimerPack))
        {
            throw new InvalidDataException($"The MXF header metadata at byte {position} does not start with a primer pack.");
        }

        var primer = ReadPrimer(Value(stream, valueStart, length, end));
        var sets = new List<MxfSet>();
        position = checked(valueStart + length);
        while (position < end)
        {
            valueStart = Klv.ReadHeader(stream, position, key, out length);
            long next = checked(valueStart + length);
            if (next > end)
            {
                throw new InvalidDataException($"A KLV item at byte {position} runs past the MXF header metadata.");
            }

            var kind = MxfLabels.SetKind(key);
            if (kind != MxfSetKind.None)
            {
                byte[] keyBytes = key.ToArray();
                sets.Add(new MxfSet(kind, keyBytes, ReadLocalSet(Value(stream, valueStart, length, end), primer, MxfLabels.HasBerLocalLengths(keyBytes), position)));
            }

            position = next;
        }

        return new MxfHeaderMetadata(sets);
    }

    private static byte[] Value(Stream stream, long valueStart, long length, long end)
    {
        if (valueStart + length > end)
        {
            throw new InvalidDataException($"A KLV item at byte {valueStart} runs past the MXF header metadata.");
        }

        byte[] value = new byte[length];
        stream.Position = valueStart;
        stream.ReadExactly(value);
        return value;
    }

    /// <summary>The primer pack's LocalTagEntry batch (ST 377-1 Tables 14, 15): each tag's label, its version byte cleared.</summary>
    private static Dictionary<ushort, Label> ReadPrimer(ReadOnlySpan<byte> value)
    {
        const int EntryLength = 2 + Klv.KeyLength;
        if (value.Length < 8)
        {
            throw new InvalidDataException("The MXF primer pack is too short.");
        }

        uint count = BinaryPrimitives.ReadUInt32BigEndian(value);
        uint itemLength = BinaryPrimitives.ReadUInt32BigEndian(value[4..]);
        if ((count != 0 && itemLength != EntryLength) || count > (uint)(value.Length - 8) / EntryLength)
        {
            throw new InvalidDataException("The MXF primer pack's entries do not hold together.");
        }

        var primer = new Dictionary<ushort, Label>((int)count);
        for (int i = 0; i < count; i++)
        {
            var entry = value.Slice(8 + (i * EntryLength), EntryLength);
            primer[BinaryPrimitives.ReadUInt16BigEndian(entry)] = new Label(entry[2..]).WithoutVersion();
        }

        return primer;
    }

    /// <summary>A local set's properties (ST 377-1 9.3): 2-byte tags, each with a 2-byte or a BER length.</summary>
    private static Dictionary<Label, byte[]> ReadLocalSet(byte[] value, Dictionary<ushort, Label> primer, bool berLengths, long position)
    {
        var properties = new Dictionary<Label, byte[]>();
        using var reader = new MemoryStream(value, writable: false);
        int offset = 0;
        while (offset < value.Length)
        {
            if (value.Length - offset < 3)
            {
                throw new InvalidDataException($"The MXF set at byte {position} ends inside a property's tag or length.");
            }

            ushort tag = BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(offset));
            long length;
            if (berLengths)
            {
                reader.Position = offset + 2;
                length = Klv.ReadLength(reader, out int size);
                offset += 2 + size;
            }
            else
            {
                if (value.Length - offset < 4)
                {
                    throw new InvalidDataException($"The MXF set at byte {position} ends inside a property's length.");
                }

                length = BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(offset + 2));
                offset += 4;
            }

            if (length > value.Length - offset)
            {
                throw new InvalidDataException($"A property of the MXF set at byte {position} runs past the set.");
            }

            if (primer.TryGetValue(tag, out var label))
            {
                properties[label] = value.AsSpan(offset, (int)length).ToArray();
            }

            offset += (int)length;
        }

        return properties;
    }
}

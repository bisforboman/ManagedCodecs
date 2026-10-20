namespace ManagedCodecs.H264;

/// <summary>nal_unit_type values (Table 7-1) the decoder cares about.</summary>
internal enum NalUnitType
{
    Slice = 1,
    SliceDataPartitionA = 2,
    IdrSlice = 5,
    Sei = 6,
    Sps = 7,
    Pps = 8,
    AccessUnitDelimiter = 9,
}

/// <summary>A NAL unit with its header split off and its payload unescaped into an RBSP.</summary>
internal readonly record struct NalUnit(NalUnitType Type, int RefIdc, byte[] Rbsp, int RbspLength)
{
    public BitReader CreateReader() => new(Rbsp, RbspLength);

    public static NalUnit Parse(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 1)
        {
            throw new InvalidDataException("Empty NAL unit.");
        }

        if ((nal[0] & 0x80) != 0)
        {
            throw new InvalidDataException("forbidden_zero_bit is set.");
        }

        var rbsp = new byte[nal.Length - 1 + 8]; // reader padding
        int length = Unescape(nal[1..], rbsp);
        return new NalUnit((NalUnitType)(nal[0] & 0x1F), (nal[0] >> 5) & 3, rbsp, length);
    }

    /// <summary>Removes emulation_prevention_three_byte: 00 00 03 becomes 00 00 (7.4.1).</summary>
    public static int Unescape(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int written = 0;
        int zeros = 0;
        for (int i = 0; i < source.Length; i++)
        {
            byte b = source[i];
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }

            zeros = b == 0 ? zeros + 1 : 0;
            destination[written++] = b;
        }

        return written;
    }

    /// <summary>Splits a sample of length-prefixed NAL units (the MP4 / avcC layout).</summary>
    public static IEnumerable<(int Offset, int Length)> SplitLengthPrefixed(ReadOnlyMemory<byte> sample, int lengthSize)
    {
        int pos = 0;
        while (pos < sample.Length)
        {
            if (pos + lengthSize > sample.Length)
            {
                throw new InvalidDataException("Truncated NAL length prefix.");
            }

            var span = sample.Span;
            int length = 0;
            for (int i = 0; i < lengthSize; i++)
            {
                length = (length << 8) | span[pos + i];
            }

            pos += lengthSize;
            if (length < 0 || pos + length > sample.Length)
            {
                throw new InvalidDataException($"NAL unit length {length} overruns the sample.");
            }

            yield return (pos, length);
            pos += length;
        }
    }
}

/// <summary>AVCDecoderConfigurationRecord (ISO/IEC 14496-15 5.3.3.1), the MP4 'avcC' payload.</summary>
internal sealed record AvcConfiguration(int ProfileIdc, int LevelIdc, int LengthSize, IReadOnlyList<byte[]> Sps, IReadOnlyList<byte[]> Pps)
{
    public static AvcConfiguration Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7 || data[0] != 1)
        {
            throw new InvalidDataException("Not an AVCDecoderConfigurationRecord.");
        }

        int lengthSize = (data[4] & 3) + 1;
        if (lengthSize == 3)
        {
            throw new InvalidDataException("NAL length size 3 is not allowed.");
        }

        int pos = 5;
        var sps = ReadParameterSets(data, ref pos, data[pos++] & 0x1F);
        if (pos >= data.Length)
        {
            throw new InvalidDataException("avcC has no PPS count.");
        }

        var pps = ReadParameterSets(data, ref pos, data[pos++]);
        return new AvcConfiguration(data[1], data[3], lengthSize, sps, pps);
    }

    private static List<byte[]> ReadParameterSets(ReadOnlySpan<byte> data, ref int pos, int count)
    {
        var sets = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            if (pos + 2 > data.Length)
            {
                throw new InvalidDataException("Truncated avcC.");
            }

            int length = (data[pos] << 8) | data[pos + 1];
            pos += 2;
            if (pos + length > data.Length)
            {
                throw new InvalidDataException("Truncated avcC.");
            }

            sets.Add(data.Slice(pos, length).ToArray());
            pos += length;
        }

        return sets;
    }
}

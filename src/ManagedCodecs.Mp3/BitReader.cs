using System.Buffers.Binary;

namespace ManagedCodecs.Mp3;

/// <summary>
/// Reads bits most significant first from a byte buffer, up to a limit in bits; reading past the buffer's end reads
/// zeros (as the end of a granule's data is checked against its length, not the buffer's).
/// </summary>
internal struct BitReader
{
    private readonly byte[] _data;
    private readonly int _byteLength;

    public BitReader(byte[] data, int byteLength, int bitPosition = 0)
    {
        _data = data;
        _byteLength = byteLength;
        Position = bitPosition;
    }

    /// <summary>The position in bits from the buffer's start.</summary>
    public int Position { get; set; }

    /// <summary>The next <paramref name="count"/> bits (0 to 24), without moving on.</summary>
    public readonly int Peek(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        int bytePosition = Position >> 3;
        uint word;
        if ((uint)bytePosition + 4 <= (uint)_byteLength)
        {
            // Four whole bytes in the buffer: one big-endian read.
            word = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(bytePosition, 4));
            return (int)((word << (Position & 7)) >> (32 - count));
        }

        word = 0;
        for (int i = 0; i < 4; i++)
        {
            int index = bytePosition + i;
            word = (word << 8) | (index < _byteLength ? _data[index] : 0u);
        }

        return (int)((word << (Position & 7)) >> (32 - count));
    }

    /// <summary>The next <paramref name="count"/> bits (0 to 24).</summary>
    public int Read(int count)
    {
        int value = Peek(count);
        Position += count;
        return value;
    }

    /// <summary>The next bit.</summary>
    public int ReadBit() => Read(1);
}

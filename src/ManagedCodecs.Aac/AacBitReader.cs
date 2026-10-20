using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace ManagedCodecs.Aac;

/// <summary>MSB-first bit reader over one AAC access unit. Reading past the end throws.</summary>
internal sealed class AacBitReader
{
    private readonly byte[] _data;
    private readonly int _bitLength;
    private int _position;

    public AacBitReader(ReadOnlySpan<byte> data)
    {
        _data = new byte[data.Length + 8]; // padding for 8-byte peeks
        data.CopyTo(_data);
        _bitLength = data.Length * 8;
    }

    public int Position => _position;

    public int BitsLeft => _bitLength - _position;

    /// <summary>The next <paramref name="count"/> (1..32) bits, without consuming them; zeros past the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Peek(int count)
    {
        ulong word = BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(Math.Min(_position >> 3, _data.Length - 8)));
        return (uint)((word << (_position & 7)) >> (64 - count));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Skip(int count)
    {
        _position += count;
        if (_position > _bitLength)
        {
            throw new InvalidDataException("AAC access unit is truncated.");
        }
    }

    public int Read(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        int value = (int)Peek(count);
        Skip(count);
        return value;
    }

    public bool ReadFlag() => Read(1) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(Vlc vlc)
    {
        int value = vlc.Lookup(Peek(vlc.MaxLength), out int length);
        Skip(length);
        return value;
    }

    public void ByteAlign() => Skip((8 - (_position & 7)) & 7);
}

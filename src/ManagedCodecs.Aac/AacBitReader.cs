using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

    /// <summary>A <see cref="Cursor"/> at the reader's position, for a hot loop to hold in its locals.</summary>
    public Cursor Begin() => new(_data, _position);

    /// <summary>Takes the position a <see cref="Cursor"/> from <see cref="Begin"/> reached; throws if it is past the end.</summary>
    public void Commit(in Cursor cursor)
    {
        ThrowIfPastEnd(cursor.Position);
        _position = cursor.Position;
    }

    /// <summary>Throws as <see cref="Skip"/> does if <paramref name="position"/> is past the end.</summary>
    public void ThrowIfPastEnd(int position)
    {
        if (position > _bitLength)
        {
            throw new InvalidDataException("AAC access unit is truncated.");
        }
    }

    /// <summary>
    /// Reads as the reader does, through a 64-bit cache of the bits from the position on: a peek is a shift of the
    /// cache, and the bytes are read again only once fewer than 32 of its bits are left (each 8-byte read leaves at
    /// least 57 bits). Past the end it reads zeros without throwing: the caller checks the end (<see cref="Commit"/>,
    /// <see cref="ThrowIfPastEnd"/>).
    /// </summary>
    public ref struct Cursor
    {
        // At most 25 bits used before a peek, so at least 57 - 25 = 32 of the cache's bits are left for it, and a
        // peek's skip of up to 32 bits leaves _used below 64, the width a shift of the cache can take.
        private const int MaxUsed = 25;

        private readonly byte[] _data;

        // The cache's first bit, the 64 bits from it (MSB first) and how many of them have been read.
        private int _start;
        private ulong _cache;
        private int _used;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Cursor(byte[] data, int position)
        {
            _data = data;
            _start = position;
            _cache = Load(position);
            _used = 0;
        }

        public readonly int Position => _start + _used;

        /// <summary>The next <paramref name="count"/> (1..32) bits, without consuming them.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Peek(int count)
        {
            if (_used > MaxUsed)
            {
                _start += _used;
                _used = 0;
                _cache = Load(_start);
            }

            return (uint)((_cache << _used) >> (64 - count));
        }

        /// <summary>Consumes <paramref name="count"/> bits, at most as many as the last <see cref="Peek"/> returned.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Skip(int count) => _used += count;

        /// <summary>The 64 bits from bit <paramref name="position"/> on; past the data (and its zero padding) zeros.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly ulong Load(int position)
        {
            int bytePos = position >> 3;
            ulong word = (uint)bytePos <= (uint)(_data.Length - 8)
                ? BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data), bytePos)))
                : 0;
            return word << (position & 7);
        }
    }
}

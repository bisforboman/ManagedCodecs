using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManagedCodecs.H264;

/// <summary>
/// MSB-first reader over an RBSP (a NAL unit payload with emulation prevention bytes removed),
/// with the Exp-Golomb codes of H.264 clause 9.1. Reads past the end return zero bits.
/// </summary>
internal sealed class BitReader
{
    private readonly byte[] _data;
    private readonly int _length;
    private readonly long _trailingBitPosition;
    private long _position;

    /// <param name="data">The RBSP; it must have at least 8 bytes of slack after <paramref name="length"/>.</param>
    public BitReader(byte[] data, int length)
    {
        if (data.Length < length + 8)
        {
            throw new ArgumentException("The buffer needs 8 bytes of padding after the data.", nameof(data));
        }

        _data = data;
        _length = length;

        // rbsp_trailing_bits: the last 1 bit in the data is the stop bit (7.3.2.11).
        int last = length - 1;
        while (last >= 0 && data[last] == 0)
        {
            last--;
        }

        _trailingBitPosition = last < 0 ? 0 : (last * 8L) + 7 - BitOperations.TrailingZeroCount(data[last]);
    }

    public long Position => _position;

    public bool IsByteAligned => (_position & 7) == 0;

    /// <summary>Whether reads went past the end of the RBSP (into the zero bits after it): the data was cut short.</summary>
    public bool PastEnd => _position > _length * 8L;

    /// <summary>The RBSP's length in bits.</summary>
    public long BitLength => _length * 8L;

    /// <summary>more_rbsp_data() of 7.2: true while there is data before the trailing stop bit.</summary>
    public bool MoreRbspData() => _position < _trailingBitPosition;

    /// <summary>Returns the next <paramref name="count"/> (1..32) bits without consuming them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Peek(int count)
    {
        int bytePos = (int)(_position >> 3);
        ulong word;
        if ((uint)bytePos + 8 <= (uint)_length)
        {
            // The common case, all eight bytes inside the RBSP; the check also keeps the read
            // inside the array, which is at least _length + 8 long.
            word = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data), bytePos)));
        }
        else
        {
            word = PeekNearEnd(bytePos);
        }

        return (uint)((word << (int)(_position & 7)) >> (64 - count));
    }

    /// <summary>The 64 bits at <paramref name="bytePos"/> when fewer than 8 bytes of RBSP remain; padding reads as zero.</summary>
    private ulong PeekNearEnd(int bytePos) => PeekNearEnd(_data, _length, bytePos);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong PeekNearEnd(byte[] data, int length, int bytePos)
    {
        int valid = length - bytePos;
        if (valid <= 0)
        {
            return 0;
        }

        ulong word = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(bytePos));
        return word & (ulong.MaxValue << (64 - (valid * 8)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Skip(int count) => _position += count;

    /// <summary>
    /// A cursor over this reader for a run of reads in one method: the data, length and position in locals, where the
    /// JIT keeps them in registers (a field of the reader is reloaded after every store). <see cref="Commit"/> writes
    /// the position back; reads through the reader meanwhile would see the old one.
    /// </summary>
    public Cursor Begin() => new(_data, _length, _position);

    /// <summary>Takes the position a <see cref="Cursor"/> from <see cref="Begin"/> reached.</summary>
    public void Commit(in Cursor cursor) => _position = cursor.Position;

    /// <summary>The reads of <see cref="BitReader"/> on locals (<see cref="Begin"/>).</summary>
    public ref struct Cursor
    {
        private readonly ref byte _data;
        private readonly byte[] _array;
        private readonly int _length;

        public Cursor(byte[] data, int length, long position)
        {
            _array = data;
            _data = ref MemoryMarshal.GetArrayDataReference(data);
            _length = length;
            Position = position;
        }

        public long Position { readonly get; private set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly uint Peek(int count)
        {
            int bytePos = (int)(Position >> 3);
            ulong word = (uint)bytePos + 8 <= (uint)_length
                ? BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref _data, bytePos)))
                : PeekNearEnd(_array, _length, bytePos);
            return (uint)((word << (int)(Position & 7)) >> (64 - count));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Skip(int count) => Position += count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadBits(int count)
        {
            if (count == 0)
            {
                return 0;
            }

            uint value = Peek(count);
            Position += count;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadFlag() => ReadBits(1) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Read(Vlc vlc)
        {
            int value = vlc.Lookup(Peek(vlc.MaxLength), out int length);
            Position += length;
            return value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        uint value = Peek(count);
        _position += count;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadFlag() => ReadBits(1) != 0;

    /// <summary>ue(v): unsigned Exp-Golomb (9.1).</summary>
    /// <remarks>Inlined into the macroblock layer's every header field; codes of 16 or more leading zeros go out of line.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUe()
    {
        uint peek = Peek(32);
        int leadingZeros = BitOperations.LeadingZeroCount(peek);
        if (leadingZeros < 16)
        {
            // Fast path: the whole code fits in the 32 peeked bits.
            int length = (2 * leadingZeros) + 1;
            _position += length;
            return (peek >> (32 - length)) - 1;
        }

        return ReadLongUe(leadingZeros);
    }

    /// <summary><see cref="ReadUe"/> for a code of 16 or more leading zeros.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private uint ReadLongUe(int leadingZeros)
    {
        if (leadingZeros > 31)
        {
            throw new InvalidDataException("Exp-Golomb code too long.");
        }

        _position += leadingZeros + 1;
        return (uint)(((1UL << leadingZeros) - 1) + ReadBits(leadingZeros));
    }

    /// <summary>se(v): signed Exp-Golomb (9.1.1): 1, -1, 2, -2, ...</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadSe()
    {
        uint k = ReadUe();
        return (k & 1) != 0 ? (int)((k + 1) >> 1) : -(int)(k >> 1);
    }

    /// <summary>te(v) for a syntax element whose largest value is <paramref name="max"/> (9.1).</summary>
    public uint ReadTe(int max) => max > 1 ? ReadUe() : (ReadFlag() ? 0u : 1u);

    /// <summary>Reads one codeword of <paramref name="vlc"/> and returns its value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(Vlc vlc)
    {
        int value = vlc.Lookup(Peek(vlc.MaxLength), out int length);
        Skip(length);
        return value;
    }

    /// <summary>ue(v) checked against an inclusive upper bound.</summary>
    public int ReadUe(int max, string name)
    {
        uint value = ReadUe();
        if (value > (uint)max)
        {
            throw new InvalidDataException($"{name} {value} is out of range (max {max}).");
        }

        return (int)value;
    }
}

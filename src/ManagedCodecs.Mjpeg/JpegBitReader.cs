using System.Buffers.Binary;
using System.Diagnostics;

namespace ManagedCodecs.Mjpeg;

/// <summary>
/// MSB-first bit reader over entropy-coded scan data. Removes 0xFF00 byte stuffing and stops
/// at any marker; past the end of the data (or at a marker) it supplies zero bits. As FFmpeg's decoder reads a scan
/// that runs past its data (a cut frame): after the data, the bytes <see cref="Tail"/> gives for the marker that ends
/// it, before the zeros; a restart marker's 0xFF is data; a lone 0xFF ending the packet is not.
/// </summary>
internal ref struct JpegBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private ulong _buffer; // valid bits are left-aligned
    private int _count;
    private readonly ReadOnlySpan<byte> _tail;
    private int _tailIndex;
    private bool _atMarker;
    private long _dataBits; // bits of the data put into the buffer (not the zeros after it)
    private long _fedBits; // bits put into the buffer, the zeros after the data too (those read: _fedBits - _count)

    public JpegBitReader(ReadOnlySpan<byte> data, ReadOnlySpan<byte> tail = default)
    {
        _data = data;
        _tail = tail;
    }

    /// <summary>
    /// The bytes FFmpeg's decoder reads, as data, after a scan's data that a marker ends (<paramref name="marker"/>,
    /// from its first 0xFF): one 0xFF, and the marker's code after it where a 0xFF fill byte came before the marker
    /// ("FF FF D9"); none where the packet ended. Measured on cut frames against FFmpeg's output, not derived: what its
    /// reader takes past the unescaped scan.
    /// </summary>
    public static ReadOnlySpan<byte> Tail(ReadOnlySpan<byte> marker)
    {
        if (marker.IsEmpty)
        {
            return [];
        }

        int fill = 0;
        while (fill < marker.Length && marker[fill] == 0xFF)
        {
            fill++;
        }

        // Slices of the marker: its last fill byte and its code, or its first 0xFF.
        return fill >= 2 && fill < marker.Length ? marker.Slice(fill - 1, 2) : marker[..1];
    }

    /// <summary>
    /// Whether more bits were read than the data holds (zeros past its end): where FFmpeg's decoder stops a scan
    /// ("overread"), the rest of the image left at 0.
    /// </summary>
    public readonly bool Overread => _fedBits - _count > _dataBits;

    /// <summary>Returns the next <paramref name="n"/> bits (1..16) without consuming them.</summary>
    public int Peek(int n)
    {
        if (_count < n)
        {
            Fill();
        }

        return (int)(_buffer >> (64 - n));
    }

    public void Skip(int n)
    {
        _buffer <<= n;
        _count -= n;
    }

    public int ReadBits(int n)
    {
        int value = Peek(n);
        Skip(n);
        return value;
    }

    /// <summary>The buffered bits, left-aligned: with <see cref="Count"/>, the state a caller holds in its locals.</summary>
    public readonly ulong Buffer => _buffer;

    /// <summary>How many of <see cref="Buffer"/>'s bits are valid.</summary>
    public readonly int Count => _count;

    /// <summary>Puts back the state a caller held in its locals (<see cref="Buffer"/>, <see cref="Count"/>).</summary>
    public void Restore(ulong buffer, int count)
    {
        _buffer = buffer;
        _count = count;
    }

    /// <summary>
    /// Refills a caller's state held in locals: <paramref name="buffer"/> with <paramref name="count"/> valid bits.
    /// Returns the new buffer; its count is <see cref="FilledCount"/> of <paramref name="count"/>. Taking and returning
    /// values (not references) keeps the caller's locals in registers.
    /// </summary>
    public ulong Fill(ulong buffer, int count)
    {
        _buffer = buffer;
        _count = count;
        Fill();
        Debug.Assert(_count == FilledCount(count), "A refill takes whole bytes up to more than 56 bits.");
        return _buffer;
    }

    /// <summary>The count of valid bits after a refill from <paramref name="count"/>: whole bytes up to more than 56.</summary>
    public static int FilledCount(int count) => count + ((64 - count) & ~7);

    /// <summary>
    /// Drops buffered bits and consumes the RSTn marker that must come next.
    /// Returns false when the next marker is not a restart marker.
    /// </summary>
    public bool ConsumeRestartMarker(out int index)
    {
        _buffer = 0;
        _count = 0;
        _atMarker = false;
        _fedBits = _dataBits; // the buffered bits are dropped, not read

        // Skip to the next marker. Valid streams are already there (only padding bits remain).
        while (_pos + 1 < _data.Length && !(_data[_pos] == 0xFF && _data[_pos + 1] is not 0x00 and not 0xFF))
        {
            _pos++;
        }

        if (_pos + 1 < _data.Length && _data[_pos + 1] is >= JpegMarker.Rst0 and <= JpegMarker.Rst7)
        {
            index = _data[_pos + 1] - JpegMarker.Rst0;
            _pos += 2;
            return true;
        }

        index = -1;
        return false;
    }

    private void Fill()
    {
        // Fast path: eight bytes with no 0xFF among them hold no stuffing and no marker, so
        // as many whole bytes as fit can go in at once. The partial byte that also lands in
        // the low bits is the same byte a later refill puts there, so ORing it twice is fine.
        if (!_atMarker && _pos + 8 <= _data.Length)
        {
            ulong next = BinaryPrimitives.ReadUInt64BigEndian(_data[_pos..]);
            ulong inverted = ~next;
            if (((inverted - 0x0101010101010101UL) & ~inverted & 0x8080808080808080UL) == 0)
            {
                int bytes = (64 - _count) >> 3;
                _buffer |= next >> _count;
                _pos += bytes;
                _count += bytes * 8;
                _dataBits += bytes * 8;
                _fedBits += bytes * 8;
                return;
            }
        }

        while (_count <= 56)
        {
            ulong b = 0;
            if (!_atMarker && _pos < _data.Length)
            {
                b = _data[_pos];
                if (b == 0xFF)
                {
                    if (_pos + 1 < _data.Length && _data[_pos + 1] == 0x00)
                    {
                        _pos += 2;
                    }
                    else if (_pos + 1 < _data.Length)
                    {
                        // A marker (a restart marker, in the scan): leave it for ConsumeRestartMarker; its 0xFF is
                        // data, zeros follow.
                        _atMarker = true;
                    }
                    else
                    {
                        // A lone 0xFF ending the packet: not data.
                        _atMarker = true;
                        b = 0;
                        _dataBits -= 8;
                    }
                }
                else
                {
                    _pos++;
                }

                _dataBits += 8;
            }
            else if (!_atMarker && _tailIndex < _tail.Length)
            {
                // What FFmpeg's decoder reads after the data (Tail), then zeros.
                b = _tail[_tailIndex++];
                _dataBits += 8;
            }

            _buffer |= b << (56 - _count);
            _count += 8;
            _fedBits += 8;
        }
    }
}

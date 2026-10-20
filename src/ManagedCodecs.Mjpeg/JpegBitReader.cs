namespace ManagedCodecs.Mjpeg;

/// <summary>
/// MSB-first bit reader over entropy-coded scan data. Removes 0xFF00 byte stuffing and stops
/// at any marker; past the end of the data (or at a marker) it supplies zero bits.
/// </summary>
internal ref struct JpegBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private ulong _buffer; // valid bits are left-aligned
    private int _count;
    private bool _atMarker;

    public JpegBitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
    }

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

    /// <summary>Reads an <paramref name="n"/>-bit magnitude and sign-extends it (T.81 F.2.2.1, EXTEND).</summary>
    public int ReceiveExtend(int n)
    {
        if (n == 0)
        {
            return 0;
        }

        int value = ReadBits(n);
        return value < (1 << (n - 1)) ? value - (1 << n) + 1 : value;
    }

    /// <summary>
    /// Drops buffered bits and consumes the RSTn marker that must come next.
    /// Returns false when the next marker is not a restart marker.
    /// </summary>
    public bool ConsumeRestartMarker(out int index)
    {
        _buffer = 0;
        _count = 0;
        _atMarker = false;

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
                    else
                    {
                        // A marker: leave it for ConsumeRestartMarker and feed zeros.
                        _atMarker = true;
                        b = 0;
                    }
                }
                else
                {
                    _pos++;
                }
            }

            _buffer |= b << (56 - _count);
            _count += 8;
        }
    }
}

namespace ManagedCodecs;

/// <summary>
/// Plane buffers for tight frames (stride equal to the plane width, offsets 0), kept for reuse:
/// <see cref="Rent"/> takes planes that a disposed frame gave back, of the same sizes, or
/// allocates new ones. Their old samples are not cleared, so whoever rents a frame writes every
/// sample. Frames that are never disposed are collected as usual. Safe to use from several threads.
/// </summary>
internal sealed class VideoFramePool
{
    private readonly int _capacity;
    private readonly List<byte[][]> _free = [];

    /// <param name="capacity">How many frames' planes to keep at most; planes returned beyond it are dropped.</param>
    public VideoFramePool(int capacity = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _capacity = capacity;
    }

    /// <summary>A tight frame of the given size and format, whose samples hold anything until written.</summary>
    public VideoFrame Rent(int width, int height, PixelFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int count = format.PlaneCount();
        int[] strides = new int[count];
        int[] lengths = new int[count];
        for (int i = 0; i < count; i++)
        {
            var (planeWidth, planeHeight) = format.PlaneSize(i, width, height);
            strides[i] = planeWidth * format.BytesPerSample();
            lengths[i] = strides[i] * planeHeight;
        }

        var planes = Take(lengths);
        if (planes is null)
        {
            planes = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                planes[i] = GC.AllocateUninitializedArray<byte>(lengths[i]);
            }
        }

        return new VideoFrame(width, height, format, planes, strides, this);
    }

    /// <summary>
    /// Planes of these lengths, for a decoder to write a frame into before it wraps them (with their own strides) in a
    /// frame of this pool: from the free list, or new and uninitialized.
    /// </summary>
    internal byte[][] RentPlanes(int[] lengths)
    {
        var planes = Take(lengths);
        if (planes is null)
        {
            planes = new byte[lengths.Length][];
            for (int i = 0; i < lengths.Length; i++)
            {
                planes[i] = GC.AllocateUninitializedArray<byte>(lengths[i]);
            }
        }

        return planes;
    }

    /// <summary>Planes of exactly these lengths from the free list, or null.</summary>
    private byte[][]? Take(int[] lengths)
    {
        lock (_free)
        {
            for (int k = _free.Count - 1; k >= 0; k--)
            {
                var planes = _free[k];
                if (planes.Length == lengths.Length && planes.Select(p => p.Length).SequenceEqual(lengths))
                {
                    _free.RemoveAt(k);
                    return planes;
                }
            }
        }

        return null;
    }

    internal void Return(byte[][] planes)
    {
        lock (_free)
        {
            if (_free.Count < _capacity)
            {
                _free.Add(planes);
            }
        }
    }
}

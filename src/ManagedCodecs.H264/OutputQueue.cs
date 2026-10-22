namespace ManagedCodecs.H264;

/// <summary>
/// Holds decoded frames until no later frame in decoding order can be shown before them, then
/// releases them in picture order count order (the output part of C.4.5.3).
/// </summary>
internal sealed class OutputQueue
{
    private readonly List<(VideoFrame Frame, int Poc, Picture? Source)> _held = [];

    /// <summary>
    /// Adds a decoded frame and returns those now ready. <paramref name="restart"/> marks a frame
    /// whose order counts start over (IDR, memory_management_control_operation 5): every held frame
    /// goes first. <paramref name="depth"/> is how many frames may wait for reordering.
    /// </summary>
    /// <param name="source">The picture a borrowed frame's samples are in, which must not be reused while the frame waits here.</param>
    public List<VideoFrame> Add(VideoFrame frame, int poc, bool restart, int depth, Picture? source = null)
    {
        var ready = new List<VideoFrame>();
        if (restart)
        {
            Release(ready, _held.Count);
        }

        _held.Add((frame, poc, source));
        Release(ready, _held.Count - depth);
        return ready;
    }

    /// <summary>Whether a frame waiting here borrows the samples of <paramref name="picture"/>.</summary>
    public bool Holds(Picture picture)
    {
        foreach (var held in _held)
        {
            if (held.Source == picture)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns every held frame, in order.</summary>
    public List<VideoFrame> Flush()
    {
        var rest = new List<VideoFrame>();
        Release(rest, _held.Count);
        return rest;
    }

    private void Release(List<VideoFrame> output, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int next = 0;
            for (int j = 1; j < _held.Count; j++)
            {
                if (_held[j].Poc < _held[next].Poc)
                {
                    next = j;
                }
            }

            output.Add(_held[next].Frame);
            _held.RemoveAt(next);
        }
    }
}

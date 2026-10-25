namespace ManagedCodecs.H264;

/// <summary>
/// The thread of one picture decoder (frame threading, H264Decoder.Threads): it decodes the pictures given to that
/// decoder, one at a time, and stays for the next. A new thread a picture, with the front end spinning in task waits,
/// had cost 12% of the CPU and 22% of the time of 320x240 video at 4 threads.
/// </summary>
/// <remarks>
/// The decoder has no Dispose: the thread ends after <see cref="IdleTimeout"/> without a picture, or when
/// <see cref="Stop"/> is called (Flush), and the next picture starts a new one.
/// </remarks>
internal sealed class PictureThread
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private Action? _next;
    private bool _running;
    private bool _stop;

    /// <summary>Runs <paramref name="work"/> on the thread, starting one if none is waiting; the previous work must have returned.</summary>
    public void Post(Action work)
    {
        lock (_lock)
        {
            _next = work;
            _stop = false;
            if (_running)
            {
                Monitor.Pulse(_lock);
                return;
            }

            _running = true;
        }

        new Thread(Loop) { IsBackground = true, Name = "H.264 picture" }.Start();
    }

    /// <summary>Ends the thread once it has nothing left to do.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _stop = true;
            Monitor.Pulse(_lock);
        }
    }

    private void Loop()
    {
        while (true)
        {
            Action work;
            lock (_lock)
            {
                while (_next is null)
                {
                    if (_stop || (!Monitor.Wait(_lock, IdleTimeout) && _next is null))
                    {
                        _running = false;
                        return;
                    }
                }

                work = _next;
                _next = null;
            }

            work(); // catches what it throws itself (PictureJob.Complete)
        }
    }
}

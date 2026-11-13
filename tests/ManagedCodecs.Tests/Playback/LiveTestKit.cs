using System.Net;
using System.Net.Sockets;
using System.Text;
using ManagedCodecs.Containers;
using ManagedCodecs.Mjpeg;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// A clock that moves only when a test advances it, so that stalls and backoffs take no real time.
/// (<c>TaskCompletionSource&lt;bool&gt;</c>: the Unity build, which these tests also run against, has a
/// non-generic one of its own in Polyfills.cs, ambiguous with the runtime's.)
/// </summary>
internal sealed class FakeLiveClock : ILiveClock
{
    private readonly List<(TimeSpan Due, TimeSpan Length, TaskCompletionSource<bool> Done)> _waiters = [];
    private TimeSpan _now;

    public TimeSpan Elapsed
    {
        get
        {
            lock (_waiters)
            {
                return _now;
            }
        }
    }

    /// <summary>Delays waiting on the clock (not cancelled, not yet due).</summary>
    public int Waiting
    {
        get
        {
            lock (_waiters)
            {
                return _waiters.Count(w => !w.Done.Task.IsCompleted);
            }
        }
    }

    /// <summary>The lengths the waiting delays were asked for: a backoff's, or the stall watchdog's.</summary>
    public TimeSpan[] WaitingFor
    {
        get
        {
            lock (_waiters)
            {
                return _waiters.Where(w => !w.Done.Task.IsCompleted).Select(w => w.Length).ToArray();
            }
        }
    }

    public Task Delay(TimeSpan delay, CancellationToken token)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiters)
        {
            if (delay <= TimeSpan.Zero)
            {
                return Task.CompletedTask;
            }

            _waiters.Add((_now + delay, delay, done));
        }

        token.Register(() =>
        {
            lock (_waiters)
            {
                _waiters.RemoveAll(w => w.Done == done);
            }

            done.TrySetCanceled(token);
        });
        return done.Task;
    }

    public void Advance(TimeSpan by)
    {
        List<TaskCompletionSource<bool>> due;
        lock (_waiters)
        {
            _now += by;
            due = _waiters.Where(w => w.Due <= _now).Select(w => w.Done).ToList();
            _waiters.RemoveAll(w => w.Due <= _now);
        }

        foreach (var done in due)
        {
            done.TrySetResult(true);
        }
    }
}

/// <summary>
/// An HTTP server on the loopback interface (no ASP.NET) that answers each connection with a test's script: the
/// response's head, then whatever the script writes.
/// </summary>
internal sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<Stream, int, CancellationToken, Task> _script;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private int _connections;

    /// <param name="script">Writes the response to a connection (its index from 0), the request already read.</param>
    public LoopbackServer(Func<Stream, int, CancellationToken, Task> script)
    {
        _script = script;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public Uri Uri => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/video.mjpg");

    public int Connections => Volatile.Read(ref _connections);

    public static Task WriteAsync(Stream stream, string text, CancellationToken token) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), token).AsTask();

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _accepting.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
    }

    private async Task AcceptAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            int index = Interlocked.Increment(ref _connections) - 1;
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    try
                    {
                        await ReadRequestAsync(stream, token);
                        await _script(stream, index, token);
                    }
                    catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
                    {
                        // The player closed the connection, or the test ended.
                    }
                }
            });
        }
    }

    private static async Task ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var seen = new List<byte>();
        var one = new byte[1];
        while (seen.Count < 4 || !(seen[^4] == '\r' && seen[^3] == '\n' && seen[^2] == '\r' && seen[^1] == '\n'))
        {
            if (await stream.ReadAsync(one, token) == 0)
            {
                throw new IOException("The request ended early.");
            }

            seen.Add(one[0]);
        }
    }
}

/// <summary>The JPEGs of an MJPEG sample, and what the player should show for each.</summary>
internal static class LiveSamples
{
    public static byte[][] Jpegs(string name, int count)
    {
        using var demuxer = Demuxer.Open(TestSamples.PathOf(name));
        return demuxer.ReadPackets().Take(count).Select(p => p.Data.ToArray()).ToArray();
    }

    public static byte[] Rgba(byte[] jpeg)
    {
        using var frame = new MjpegDecoder().Decode(jpeg);
        var pixels = new byte[frame.Width * frame.Height * 4];
        YuvToRgba.For(frame).Convert(frame, pixels);
        return pixels;
    }

    /// <summary>A part: its delimiter line, its headers and its body, then the line end before the next delimiter.</summary>
    public static byte[] Part(string boundary, byte[] body, string type = "image/jpeg", bool length = true, string newline = "\r\n")
    {
        var head = $"--{boundary}{newline}Content-Type: {type}{newline}" + (length ? $"Content-Length: {body.Length}{newline}" : "") + newline;
        return [.. Encoding.ASCII.GetBytes(head), .. body, .. Encoding.ASCII.GetBytes(newline)];
    }

    public static string Head(string contentType) =>
        $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n";
}

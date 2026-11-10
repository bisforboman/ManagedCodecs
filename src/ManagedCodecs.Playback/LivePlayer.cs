using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Playback;

/// <summary>
/// Shows a live camera: after <see cref="Start"/>, it reads an MJPEG over HTTP stream and decodes on threads of its own,
/// and the caller takes the newest frame on its render tick (<see cref="TakeLatest"/>). No clock and no queue: a frame
/// older than the newest is never shown. While the decoder is busy, JPEGs that arrive are kept only until a newer one
/// does (<see cref="LiveStatistics.SkippedFrames"/>); a decoded frame not taken before the next is decoded is dropped
/// (<see cref="LiveStatistics.DroppedFrames"/>). A dropped or stalled connection is made again, after 0.5 s and then
/// up to 10 s apart, and <see cref="State"/> and <see cref="Error"/> say what is happening.
/// </summary>
/// <remarks>
/// <see cref="TakeLatest"/> and <see cref="Return"/> are for one thread, the UI's. A software decoder costs CPU a frame:
/// a wall of cameras wants their low-resolution substreams.
/// docs/decisions/2026-11-10-live-mjpeg-over-http.md.
/// </remarks>
public sealed class LivePlayer : IDisposable
{
    private const int SpareLimit = 4;
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    private readonly ILiveClock _clock;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _received = new(false);
    private readonly Thread _thread;
    private readonly ConcurrentBag<byte[]> _spare = [];
    private Task? _reader;
    private Received? _pending;
    private RgbaFrame? _latest;
    private TimeSpan _origin;
    private long _lastActivityTicks;
    private int _state;
    private int _started;
    private int _disposed;
    private volatile Exception? _error;
    private long _receivedCount;
    private long _decoded;
    private long _taken;
    private long _skipped;
    private long _dropped;
    private long _damaged;
    private long _reconnects;
    private long _decodeTicks;
    private long _convertTicks;

    /// <summary>A player for a source; call <see cref="Start"/> to connect.</summary>
    public LivePlayer(MjpegHttpSource source)
        : this(source, new SystemLiveClock())
    {
    }

    internal LivePlayer(MjpegHttpSource source, ILiveClock clock)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        _clock = clock;
        _ownsClient = source.Client is null;
        _client = source.Client ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _thread = new Thread(Decode) { IsBackground = true, Name = "ManagedCodecs live decoding" };
    }

    /// <summary>The source played.</summary>
    public MjpegHttpSource Source { get; }

    /// <summary>What the connection is doing.</summary>
    public LiveState State => (LiveState)Volatile.Read(ref _state);

    /// <summary>
    /// The last thing that went wrong, or null: why the connection dropped (kept while <see cref="State"/> is
    /// <see cref="LiveState.Reconnecting"/> and after it plays again), or why the player has
    /// <see cref="LiveState.Failed"/>. A <see cref="TimeoutException"/> for a stall. Not thrown: on the player's threads
    /// an exception would end the process.
    /// </summary>
    public Exception? Error => _error;

    /// <summary>What was received and what became of it.</summary>
    public LiveStatistics Statistics
    {
        get
        {
            long decoded = Interlocked.Read(ref _decoded);
            double perFrame = decoded == 0 ? 0 : 1000.0 / Stopwatch.Frequency / decoded;
            return new LiveStatistics(
                Interlocked.Read(ref _receivedCount),
                decoded,
                Interlocked.Read(ref _taken),
                Interlocked.Read(ref _skipped),
                Interlocked.Read(ref _dropped),
                Interlocked.Read(ref _damaged),
                Interlocked.Read(ref _reconnects),
                Interlocked.Read(ref _decodeTicks) * perFrame,
                Interlocked.Read(ref _convertTicks) * perFrame);
        }
    }

    /// <summary>Connects and starts decoding. Again does nothing.</summary>
    /// <exception cref="ObjectDisposedException">The player was disposed.</exception>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(LivePlayer));
        }

        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _origin = _clock.Elapsed;
            _thread.Start();
            _reader = Task.Run(ReadAsync);
        }
    }

    /// <summary>
    /// The newest decoded frame, or null when none was decoded since the last call. Its <see cref="RgbaFrame.Time"/> is
    /// when its JPEG had arrived whole, from <see cref="Start"/>.
    /// </summary>
    public RgbaFrame? TakeLatest()
    {
        var frame = Interlocked.Exchange(ref _latest, null);
        if (frame is not null)
        {
            Interlocked.Increment(ref _taken);
        }

        return frame;
    }

    /// <summary>
    /// Gives a frame's pixels back for a later frame, once they are no longer read (copied into a bitmap). Optional: a
    /// frame not returned is collected as usual. Return each frame once, and don't read its pixels after.
    /// </summary>
    public void Return(RgbaFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_spare.Count < SpareLimit)
        {
            _spare.Add(frame.Pixels);
        }
    }

    /// <summary>Closes the connection and stops decoding (waiting up to 2 seconds for each).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        _received.Set();
        if (Volatile.Read(ref _started) != 0)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
            try
            {
                _reader?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // ReadAsync catches what it can; anything else is moot once stopped.
            }
        }

        if (_ownsClient)
        {
            _client.Dispose();
        }

        Volatile.Write(ref _state, (int)LiveState.Stopped);
    }

    // Connects, reads until the connection ends, and connects again until stopped or failed.
    private async Task ReadAsync()
    {
        var token = _stop.Token;
        var backoff = FirstBackoff;
        bool first = true;
        while (!token.IsCancellationRequested)
        {
            if (!first)
            {
                Interlocked.Increment(ref _reconnects);
            }

            first = false;
            var connection = new Connection();
            try
            {
                await ConnectAsync(connection, token).ConfigureAwait(false);
                _error = new EndOfStreamException("The server ended the stream.");
            }
            catch (FatalException e)
            {
                Fail(e.InnerException!);
                return;
            }
            catch (Exception e)
            {
                // No exception filter (catch ... when) in the libraries: WebAssembly's AOT mistranslates them
                // (ExceptionFilterTests).
                if (token.IsCancellationRequested)
                {
                    return;
                }

                // A refused or dropped connection, an error status, a stall or a body that isn't multipart: all may be
                // gone on the next try, as cameras reboot and networks drop.
                _error = connection.Stalled
                    ? new TimeoutException($"No part for {Source.StallTimeout.TotalSeconds} s.", e)
                    : e;
            }

            if (connection.Frames)
            {
                backoff = FirstBackoff;
            }

            SetState(LiveState.Reconnecting);
            try
            {
                await _clock.Delay(backoff, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = backoff + backoff < MaxBackoff ? backoff + backoff : MaxBackoff;
        }
    }

    private async Task ConnectAsync(Connection connection, CancellationToken token)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        Touch();
        var watchdog = WatchAsync(connection, cancel);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Source.Uri);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                throw new FatalException(new HttpRequestException($"The server answered {(int)response.StatusCode} {response.ReasonPhrase}."));
            }

            response.EnsureSuccessStatusCode();
            var type = response.Content.Headers.ContentType;
            if (type?.MediaType is { } media && !media.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
            {
                throw new FatalException(new NotSupportedException($"The server sent {media}, not MJPEG over HTTP (multipart/x-mixed-replace)."));
            }

            string? boundary = null;
            foreach (var parameter in type?.Parameters ?? [])
            {
                if (parameter.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase))
                {
                    boundary = parameter.Value?.Trim('"');
                }
            }

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

            // A read that doesn't heed its token (some runtimes' network streams) ends when the stream is closed.
            using var close = cancel.Token.Register(stream.Dispose);
            var reader = new MultipartReader(stream, boundary);
            while (await reader.ReadPartAsync(cancel.Token).ConfigureAwait(false) is { } part)
            {
                Touch();
                Interlocked.Increment(ref _receivedCount);
                if (!part.IsJpeg)
                {
                    Interlocked.Increment(ref _damaged);
                    continue;
                }

                connection.Frames = true;
                SetState(LiveState.Playing);
                var received = new Received(part.Body, _clock.Elapsed - _origin);
                if (Interlocked.Exchange(ref _pending, received) is not null)
                {
                    Interlocked.Increment(ref _skipped);
                }

                _received.Set();
            }
        }
        finally
        {
            cancel.Cancel();
            await watchdog.ConfigureAwait(false);
        }
    }

    // Ends the connection once it has gone StallTimeout without a part (or, while connecting, without the headers).
    private async Task WatchAsync(Connection connection, CancellationTokenSource cancel)
    {
        try
        {
            while (true)
            {
                var remaining = Source.StallTimeout - (_clock.Elapsed - TimeSpan.FromTicks(Interlocked.Read(ref _lastActivityTicks)));
                if (remaining <= TimeSpan.Zero)
                {
                    connection.Stalled = true;
                    cancel.Cancel();
                    return;
                }

                await _clock.Delay(remaining, cancel.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The connection ended first.
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, _clock.Elapsed.Ticks);

    private void Decode()
    {
        var token = _stop.Token;
        var decoder = new MjpegDecoder();
        try
        {
            while (!token.IsCancellationRequested)
            {
                _received.WaitOne();
                while (!token.IsCancellationRequested && Interlocked.Exchange(ref _pending, null) is { } received)
                {
                    Show(decoder, received);
                }
            }
        }
        catch (Exception e)
        {
            // A bug, not a damaged JPEG (Show skips those): thrown on this thread it would end the process.
            Fail(e);
        }
    }

    private void Show(MjpegDecoder decoder, Received received)
    {
        long start = Stopwatch.GetTimestamp();
        VideoFrame frame;
        try
        {
            frame = decoder.Decode(received.Jpeg);
        }
        catch (Exception e)
        {
            if (e is not (InvalidDataException or NotSupportedException))
            {
                throw;
            }

            Interlocked.Increment(ref _damaged);
            return;
        }

        long decoded = Stopwatch.GetTimestamp();
        var pixels = Rent(frame.Width * frame.Height * 4);
        YuvToRgba.For(frame).Convert(frame, pixels);
        var rgba = new RgbaFrame(received.Time, frame.Width, frame.Height, pixels, frame.SampleAspectRatio);
        frame.Dispose();
        Interlocked.Add(ref _decodeTicks, decoded - start);
        Interlocked.Add(ref _convertTicks, Stopwatch.GetTimestamp() - decoded);
        Interlocked.Increment(ref _decoded);
        if (Interlocked.Exchange(ref _latest, rgba) is { } old)
        {
            Interlocked.Increment(ref _dropped);
            Return(old);
        }
    }

    private byte[] Rent(int length)
    {
        while (_spare.TryTake(out var pixels))
        {
            if (pixels.Length == length)
            {
                return pixels;
            }
        }

        return new byte[length];
    }

    private void Fail(Exception error)
    {
        _error = error;
        SetState(LiveState.Failed);
        _stop.Cancel();
        _received.Set();
    }

    // Failed and Stopped are final.
    private void SetState(LiveState state)
    {
        int current = Volatile.Read(ref _state);
        while (current is not ((int)LiveState.Failed or (int)LiveState.Stopped))
        {
            int seen = Interlocked.CompareExchange(ref _state, (int)state, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    private sealed class Received(byte[] jpeg, TimeSpan time)
    {
        public byte[] Jpeg { get; } = jpeg;

        public TimeSpan Time { get; } = time;
    }

    private sealed class Connection
    {
        public volatile bool Stalled;
        public volatile bool Frames;
    }

    // A reason not to connect again (an answer a retry won't change); its inner exception is the player's Error.
    private sealed class FatalException(Exception inner) : Exception(inner.Message, inner);
}

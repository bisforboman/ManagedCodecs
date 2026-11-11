using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;
using ManagedCodecs.Playback.Rtsp;

namespace ManagedCodecs.Playback;

/// <summary>
/// Shows a live camera: after <see cref="Start"/>, it reads an MJPEG over HTTP stream (<see cref="MjpegHttpSource"/>)
/// or H.264 over RTSP (<see cref="Playback.RtspSource"/>) and decodes on threads of its own, and the caller takes the
/// newest frame on its render tick (<see cref="TakeLatest"/>). No clock: a frame older than the newest is never shown,
/// and a decoded frame not taken before the next is decoded is dropped (<see cref="LiveStatistics.DroppedFrames"/>).
/// MJPEG: while the decoder is busy, JPEGs that arrive are kept only until a newer one does
/// (<see cref="LiveStatistics.SkippedFrames"/>). H.264: every picture is decoded (later ones are predicted from it)
/// until decoding falls <see cref="RtspSource.MaxLag"/> behind; then pictures are skipped up to the next IDR picture.
/// A dropped or stalled connection is made again, after 0.5 s and then up to 10 s apart, and <see cref="State"/> and
/// <see cref="Error"/> say what is happening.
/// </summary>
/// <remarks>
/// <see cref="TakeLatest"/> and <see cref="Return"/> are for one thread, the UI's. A software decoder costs CPU a frame:
/// a wall of cameras wants their low-resolution substreams.
/// docs/decisions/2026-11-10-live-mjpeg-over-http.md, docs/decisions/2026-11-10-live-rtsp-h264.md.
/// </remarks>
public sealed class LivePlayer : IDisposable
{
    private const int SpareLimit = 4;
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    private readonly ILiveClock _clock;
    private readonly HttpClient? _client;
    private readonly bool _ownsClient;
    private readonly ConcurrentQueue<QueuedUnit> _units = new();
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
    private long _lost;
    private int _connection;

    /// <summary>A player for an MJPEG over HTTP source; call <see cref="Start"/> to connect.</summary>
    public LivePlayer(MjpegHttpSource source)
        : this(source, new SystemLiveClock())
    {
    }

    /// <summary>A player for an RTSP source (H.264); call <see cref="Start"/> to connect.</summary>
    public LivePlayer(RtspSource source)
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

    internal LivePlayer(RtspSource source, ILiveClock clock)
    {
        ArgumentNullException.ThrowIfNull(source);
        RtspSource = source;
        _clock = clock;
        _thread = new Thread(DecodeH264) { IsBackground = true, Name = "ManagedCodecs live decoding" };
    }

    /// <summary>The MJPEG over HTTP source played, or null for an RTSP player (<see cref="RtspSource"/>).</summary>
    public MjpegHttpSource? Source { get; }

    /// <summary>The RTSP source played, or null for an MJPEG over HTTP player (<see cref="Source"/>).</summary>
    public RtspSource? RtspSource { get; }

    // While decoding waits for an IDR picture, a test may hold it before each access unit (falling behind on purpose).
    internal SemaphoreSlim? DecodeGate { get; set; }

    private TimeSpan StallTimeout => RtspSource?.StallTimeout ?? Source!.StallTimeout;

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
                Interlocked.Read(ref _convertTicks) * perFrame)
            {
                LostPackets = Interlocked.Read(ref _lost),
            };
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
    /// when its JPEG, or the H.264 access unit that gave it, had arrived whole, from <see cref="Start"/>.
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
        DecodeGate?.Release(int.MaxValue / 2);
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
            _client!.Dispose();
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
                await (RtspSource is null ? ConnectAsync(connection, token) : ConnectRtspAsync(connection, token)).ConfigureAwait(false);
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
                    ? new TimeoutException($"Nothing received for {StallTimeout.TotalSeconds} s.", e)
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
            using var request = new HttpRequestMessage(HttpMethod.Get, Source!.Uri);
            using var response = await _client!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                throw new FatalException(new HttpRequestException($"The server answered {(int)response.StatusCode} {response.ReasonPhrase}."));
            }

            response.EnsureSuccessStatusCode();
            var type = response.Content.Headers.ContentType;

            // application/octet-stream is what FFmpeg's HTTP server (-f mpjpeg -listen 1) sends for its multipart
            // body, so it is read as one; another type (a snapshot's image/jpeg, a login page) is not MJPEG over HTTP.
            if (type?.MediaType is { } media
                && !media.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)
                && !media.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
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

    // One RTSP session: OPTIONS, DESCRIBE, SETUP (RTP over this connection), PLAY, then the packets until the connection
    // ends, with keep-alives before the session times out. No TEARDOWN: with RTP on the RTSP connection, closing it ends
    // the camera's session.
    private async Task ConnectRtspAsync(Connection connection, CancellationToken token)
    {
        var source = RtspSource!;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        Touch();
        var watchdog = WatchAsync(connection, cancel);
        RtspConnection? rtsp = null;
        Task? keepAlive = null;
        try
        {
            rtsp = await RtspConnection.ConnectAsync(source.Uri.Host, source.Port, source.Credential, cancel.Token).ConfigureAwait(false);
            using var close = cancel.Token.Register(rtsp.Dispose);
            string url = source.RequestUrl;
            var options = await Request(rtsp, "OPTIONS", url, [], cancel.Token).ConfigureAwait(false);
            bool getParameter = options.Header("Public")?.IndexOf("GET_PARAMETER", StringComparison.OrdinalIgnoreCase) >= 0;

            var describe = await Request(rtsp, "DESCRIBE", url, [new("Accept", "application/sdp")], cancel.Token).ConfigureAwait(false);
            string baseUrl = describe.Header("Content-Base") ?? describe.Header("Content-Location") ?? url;
            var sdp = Sdp.Parse(describe.Body);
            var track = sdp.Track ?? throw new FatalException(new NotSupportedException("The camera's SDP has no H.264 video track."));
            if (track.PacketizationMode == 2)
            {
                throw new FatalException(new NotSupportedException("The camera sends H.264 in RTP's interleaved mode (packetization-mode=2), which isn't supported."));
            }

            var setup = await Request(rtsp, "SETUP", Sdp.Resolve(baseUrl, track.Control), [new("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1")], cancel.Token).ConfigureAwait(false);
            var session = setup.Header("Session") ?? throw new InvalidDataException("The camera's SETUP answer has no Session.");
            int semicolon = session.IndexOf(';');
            rtsp.Session = semicolon < 0 ? session.Trim() : session.Substring(0, semicolon).Trim();
            var timeout = TimeSpan.FromSeconds(60);
            if (semicolon >= 0 && System.Text.RegularExpressions.Regex.Match(session, @"timeout\s*=\s*(\d+)") is { Success: true } match)
            {
                timeout = TimeSpan.FromSeconds(Math.Max(2, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
            }

            int channel = 0;
            if (setup.Header("Transport") is { } transport
                && System.Text.RegularExpressions.Regex.Match(transport, @"interleaved\s*=\s*(\d+)") is { Success: true } interleaved)
            {
                channel = int.Parse(interleaved.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            string sessionUrl = Sdp.Resolve(baseUrl, sdp.SessionControl);
            await Request(rtsp, "PLAY", sessionUrl, [new("Range", "npt=0.000-")], cancel.Token).ConfigureAwait(false);
            keepAlive = KeepAliveAsync(rtsp, getParameter ? "GET_PARAMETER" : "OPTIONS", sessionUrl, timeout, cancel.Token);

            int generation = Interlocked.Increment(ref _connection);
            var depacketizer = new RtpH264Depacketizer(track.PayloadType, track.ParameterSets);
            var units = new List<RtpAccessUnit>();
            long lost = 0;
            while (await rtsp.ReadAsync(cancel.Token).ConfigureAwait(false) is { } message)
            {
                if (!message.IsPacket || message.Channel != channel)
                {
                    continue; // keep-alives' answers, RTCP
                }

                Touch();
                depacketizer.Add(message.Data, units);
                Interlocked.Add(ref _lost, depacketizer.LostPackets - lost);
                lost = depacketizer.LostPackets;
                foreach (var unit in units)
                {
                    Interlocked.Increment(ref _receivedCount);
                    connection.Frames = true;
                    SetState(LiveState.Playing);
                    _units.Enqueue(new QueuedUnit(unit, _clock.Elapsed - _origin, generation));
                }

                if (units.Count > 0)
                {
                    units.Clear();
                    _received.Set();
                }
            }
        }
        finally
        {
            cancel.Cancel();
            rtsp?.Dispose();
            await watchdog.ConfigureAwait(false);
            if (keepAlive is not null)
            {
                await keepAlive.ConfigureAwait(false);
            }
        }
    }

    // Sends a request and checks its answer: 401 (after the challenge's answer), 403, 404, 454 and 461 are final, as a
    // retry gets the same; another failure may pass on the next connection.
    private static async Task<RtspMessage> Request(RtspConnection rtsp, string method, string url, KeyValuePair<string, string>[] headers, CancellationToken token)
    {
        var response = await rtsp.RequestAsync(method, url, headers, token).ConfigureAwait(false);
        if (response.Status is >= 200 and < 300)
        {
            return response;
        }

        var error = new IOException($"The camera answered {response.Status} {response.Reason} to {method}.");
        if (response.Status is 401 or 403 or 404 or 454)
        {
            throw new FatalException(error);
        }

        if (response.Status == 461)
        {
            throw new FatalException(new NotSupportedException($"The camera refused RTP over the RTSP connection ({response.Status} {response.Reason}); RTP over UDP isn't supported yet."));
        }

        throw error;
    }

    // Keeps the session alive: a request every half of its timeout, until the connection ends.
    private async Task KeepAliveAsync(RtspConnection rtsp, string method, string url, TimeSpan timeout, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await _clock.Delay(TimeSpan.FromTicks(timeout.Ticks / 2), token).ConfigureAwait(false);
                await rtsp.SendAsync(method, url, [], token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The connection ended (cancelled, or the write failed): the reading loop sees it too.
        }
    }

    // Ends the connection once it has gone StallTimeout without a part (or, while connecting, without the headers).
    private async Task WatchAsync(Connection connection, CancellationTokenSource cancel)
    {
        try
        {
            while (true)
            {
                var remaining = StallTimeout - (_clock.Elapsed - TimeSpan.FromTicks(Interlocked.Read(ref _lastActivityTicks)));
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

    // H.264: every access unit in order, as later pictures are predicted from earlier ones. A new connection, and a unit
    // that arrived more than MaxLag ago, start a wait for an IDR picture, skipping the units before it; a unit with no
    // picture left (all its slices lost a fragment) is skipped, the next picture's frame_num gap telling the decoder.
    private void DecodeH264()
    {
        var token = _stop.Token;
        var maxLag = RtspSource!.MaxLag;
        H264Decoder? decoder = null;
        int generation = 0;
        bool waitForIdr = true;
        long pts = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                _received.WaitOne();
                while (!token.IsCancellationRequested && _units.TryDequeue(out var queued))
                {
                    DecodeGate?.Wait(token);
                    if (queued.Generation != generation)
                    {
                        generation = queued.Generation;
                        decoder = new H264Decoder(ReadOnlySpan<byte>.Empty) { ConcealErrors = true };
                        waitForIdr = true;
                    }

                    var unit = queued.Unit;
                    if (unit.HasIdr)
                    {
                        waitForIdr = false;
                    }
                    else if (waitForIdr || _clock.Elapsed - _origin - queued.Time > maxLag)
                    {
                        waitForIdr = true;
                        Interlocked.Increment(ref _skipped);
                        continue;
                    }

                    if (unit.Damaged)
                    {
                        Interlocked.Increment(ref _damaged);
                    }

                    if (!unit.HasPicture)
                    {
                        continue;
                    }

                    ShowH264(decoder!, unit, queued.Time, pts++, ref waitForIdr);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed while the gate held decoding.
        }
        catch (Exception e)
        {
            // A bug, or a stream the decoder doesn't support (NotSupportedException): thrown here it would end the process.
            Fail(e);
        }
    }

    private void ShowH264(H264Decoder decoder, RtpAccessUnit unit, TimeSpan time, long pts, ref bool waitForIdr)
    {
        long start = Stopwatch.GetTimestamp();
        IReadOnlyList<VideoFrame> frames;
        try
        {
            frames = decoder.Decode(unit.Data, pts);
        }
        catch (InvalidDataException)
        {
            // Damage concealment can't hide (a damaged header or parameter set): wait for the next IDR picture.
            Interlocked.Increment(ref _damaged);
            waitForIdr = true;
            return;
        }

        if (frames.Count == 0)
        {
            Interlocked.Add(ref _decodeTicks, Stopwatch.GetTimestamp() - start);
            return;
        }

        // Only the newest of the frames a unit gives is converted: the others would be replaced before anyone took them.
        for (int i = 0; i < frames.Count - 1; i++)
        {
            frames[i].Dispose();
            Interlocked.Increment(ref _dropped);
        }

        var frame = frames[^1];
        long decoded = Stopwatch.GetTimestamp();
        var pixels = Rent(frame.Width * frame.Height * 4);
        YuvToRgba.For(frame).Convert(frame, pixels);
        var rgba = new RgbaFrame(time, frame.Width, frame.Height, pixels, frame.SampleAspectRatio);
        frame.Dispose();
        Interlocked.Add(ref _decodeTicks, decoded - start);
        Interlocked.Add(ref _convertTicks, Stopwatch.GetTimestamp() - decoded);
        Interlocked.Add(ref _decoded, frames.Count);
        Publish(rgba);
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
        Publish(rgba);
    }

    // The newest frame wins the slot TakeLatest takes from; one replaced before it was taken is dropped.
    private void Publish(RgbaFrame rgba)
    {
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

    private sealed record QueuedUnit(RtpAccessUnit Unit, TimeSpan Time, int Generation);

    private sealed class Connection
    {
        public volatile bool Stalled;
        public volatile bool Frames;
    }

    // A reason not to connect again (an answer a retry won't change); its inner exception is the player's Error.
    private sealed class FatalException(Exception inner) : Exception(inner.Message, inner);
}

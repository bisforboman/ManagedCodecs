using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;
using ManagedCodecs.Playback.Rtsp;

namespace ManagedCodecs.Playback;

/// <summary>
/// Shows a live camera: after <see cref="Start"/>, it reads an MJPEG over HTTP stream (<see cref="MjpegHttpSource"/>)
/// or H.264 or JPEG over RTSP (<see cref="Playback.RtspSource"/>) and decodes on threads of its own, and the caller takes the
/// newest frame on its render tick (<see cref="TakeLatest"/>). No clock: a frame older than the newest is never shown,
/// and a decoded frame not taken before the next is decoded is dropped (<see cref="LiveStatistics.DroppedFrames"/>).
/// MJPEG, and JPEG over RTSP: while the decoder is busy, JPEGs that arrive are kept only until a newer one does
/// (<see cref="LiveStatistics.SkippedFrames"/>). H.264: every picture is decoded (later ones are predicted from it)
/// until decoding falls <see cref="RtspSource.MaxLag"/> behind; then pictures are skipped up to the next IDR picture.
/// A dropped or stalled connection is made again, after 0.5 s and then up to 10 s apart, and <see cref="State"/> and
/// <see cref="Error"/> say what is happening. With <see cref="RtspSource.Audio"/>, the camera's AAC or G.711 sound is
/// decoded too, on a thread of its own, and taken with <see cref="TakeAudio"/>: every chunk in order, nothing held back
/// for lip sync (<see cref="RgbaFrame.CaptureTime"/> and <see cref="AudioChunk.CaptureTime"/> say what belongs together),
/// up to half a second waiting (past it the oldest is dropped, <see cref="LiveStatistics.DroppedAudioFrames"/>).
/// </summary>
/// <remarks>
/// <see cref="TakeLatest"/> and <see cref="Return"/> are for one thread, the UI's (<see cref="TakeAudio"/> may be
/// called on another, the audio thread). A software decoder costs CPU a frame:
/// a wall of cameras wants their low-resolution substreams.
/// docs/decisions/2026-11-10-live-mjpeg-over-http.md, docs/decisions/2026-11-10-live-rtsp-h264.md,
/// docs/decisions/2026-11-12-live-rtsp-jpeg.md, docs/decisions/2026-11-15-live-rtsp-audio.md.
/// </remarks>
public sealed class LivePlayer : IDisposable
{
    private const int SpareLimit = 4;

    // The RTP clock of H.264 (RFC 6184) and JPEG (RFC 2435).
    private const int VideoClockRate = 90000;

    // How long a UDP packet ahead of a missing one is held for it (docs/decisions/2026-11-14-live-rtsp-udp.md, decision 2).
    private static readonly TimeSpan ReorderWindow = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    // How much decoded audio waits for TakeAudio (docs/decisions/2026-11-15-live-rtsp-audio.md, decision 3).
    private static readonly TimeSpan AudioQueue = TimeSpan.FromSeconds(0.5);

    // How often each track's RTCP receiver report goes to the camera (RFC 3550, 6.2's minimum;
    // docs/decisions/2026-11-15-live-rtcp.md).
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(5);

    private readonly ILiveClock _clock;
    private readonly HttpClient? _client;
    private readonly bool _ownsClient;
    private readonly ConcurrentQueue<QueuedUnit> _units = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _received = new(false);
    private readonly Thread _thread;
    private readonly ConcurrentBag<byte[]> _spare = [];
    private readonly ConcurrentQueue<QueuedAudio> _audioUnits = new();
    private readonly AutoResetEvent _audioReceived = new(false);
    private readonly Queue<AudioChunk> _audio = new();
    private readonly Thread? _audioThread;
    private long _audioQueuedTicks;
    private volatile AudioStreamInfo? _audioInfo;
    private volatile string? _skippedAudio;
    private long _audioDecoded;
    private long _audioDropped;
    private long _audioDamaged;
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

    /// <summary>A player for an RTSP source (H.264 or JPEG, as the camera's SDP says); call <see cref="Start"/> to connect.</summary>
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
        _thread = new Thread(DecodeRtsp) { IsBackground = true, Name = "ManagedCodecs live decoding" };
        if (source.Audio)
        {
            _audioThread = new Thread(DecodeAudio) { IsBackground = true, Name = "ManagedCodecs live audio decoding" };
        }
    }

    /// <summary>The MJPEG over HTTP source played, or null for an RTSP player (<see cref="RtspSource"/>).</summary>
    public MjpegHttpSource? Source { get; }

    /// <summary>The RTSP source played, or null for an MJPEG over HTTP player (<see cref="Source"/>).</summary>
    public RtspSource? RtspSource { get; }

    /// <summary>
    /// The camera's audio as it plays, once connected: its codec (<see cref="CodecIds.Aac"/>,
    /// <see cref="CodecIds.PcmAlaw"/> or <see cref="CodecIds.PcmMulaw"/>), and the rate and channels its SDP gives (an
    /// <see cref="AudioChunk"/> says the decoder's own rate, HE-AAC's twice this). Null without
    /// <see cref="RtspSource.Audio"/>, before the first connection, and when the camera has no audio that plays
    /// (<see cref="SkippedAudioCodec"/>) or refused to send it (<see cref="Error"/>).
    /// </summary>
    public AudioStreamInfo? Audio => _audioInfo;

    /// <summary>
    /// With <see cref="RtspSource.Audio"/>, when the camera has audio but none that plays (<see cref="Audio"/> is null):
    /// the SDP's name for its first audio format (<c>MP4A-LATM</c>, <c>G726-32</c>, <c>opus</c>). Null otherwise.
    /// </summary>
    public string? SkippedAudioCodec => _skippedAudio;

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
                DecodedAudioFrames = Interlocked.Read(ref _audioDecoded),
                DroppedAudioFrames = Interlocked.Read(ref _audioDropped),
                DamagedAudioFrames = Interlocked.Read(ref _audioDamaged),
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
            _audioThread?.Start();
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
    /// The next decoded audio, in order, or null when none waits (or ever will: no <see cref="RtspSource.Audio"/>, or
    /// <see cref="Audio"/> is null). Take it as the sound has room for it: no more than half a second waits, and past
    /// that the oldest chunk is dropped (<see cref="LiveStatistics.DroppedAudioFrames"/>), so a sound that plays slower
    /// than the camera sends loses a chunk now and then rather than falling ever further behind. Not synchronized with
    /// the picture: each comes as soon as it is decoded, and <see cref="AudioChunk.CaptureTime"/> and
    /// <see cref="RgbaFrame.CaptureTime"/> say when the camera captured each.
    /// </summary>
    public AudioChunk? TakeAudio()
    {
        lock (_audio)
        {
            if (_audio.Count == 0)
            {
                return null;
            }

            var chunk = _audio.Dequeue();
            _audioQueuedTicks -= Ticks(chunk);
            return chunk;
        }
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
        _audioReceived.Set();
        DecodeGate?.Release(int.MaxValue / 2);
        if (Volatile.Read(ref _started) != 0)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
            _audioThread?.Join(TimeSpan.FromSeconds(2));
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
                    ? new TimeoutException(
                        connection.Udp
                            ? $"Nothing received for {StallTimeout.TotalSeconds} s over UDP (a firewall or NAT may be dropping it)."
                            : $"Nothing received for {StallTimeout.TotalSeconds} s.",
                        e)
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
                Offer(new Received(part.Body, _clock.Elapsed - _origin));
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
        RtpUdpPorts? udp = null;
        AudioSession? audio = null;
        Task? keepAlive = null;
        Task? audioPump = null;
        Task? reports = null;
        try
        {
            try
            {
                rtsp = await RtspConnection.ConnectAsync(source.Uri.Host, source.Port, source.Credential, source.Secure, source.PinnedCertificate, cancel.Token).ConfigureAwait(false);
            }
            catch (System.Security.Authentication.AuthenticationException e)
            {
                // A refused certificate (or a server that speaks no TLS) is the same on the next try.
                throw new FatalException(e);
            }

            using var close = cancel.Token.Register(rtsp.Dispose);
            string url = source.RequestUrl;
            var options = await Request(rtsp, "OPTIONS", url, [], cancel.Token).ConfigureAwait(false);
            bool getParameter = options.Header("Public")?.IndexOf("GET_PARAMETER", StringComparison.OrdinalIgnoreCase) >= 0;

            var describe = await Request(rtsp, "DESCRIBE", url, [new("Accept", "application/sdp")], cancel.Token).ConfigureAwait(false);
            string baseUrl = describe.Header("Content-Base") ?? describe.Header("Content-Location") ?? url;
            var sdp = Sdp.Parse(describe.Body);
            var track = sdp.Track ?? throw new FatalException(new NotSupportedException("The camera's SDP has no H.264 or JPEG video track."));
            if (track.Codec == SdpCodec.H264 && track.PacketizationMode == 2)
            {
                throw new FatalException(new NotSupportedException("The camera sends H.264 in RTP's interleaved mode (packetization-mode=2), which isn't supported."));
            }

            // RTP over this connection; over UDP when the server refuses that (461; decision 1 of
            // docs/decisions/2026-11-14-live-rtsp-udp.md).
            string trackUrl = Sdp.Resolve(baseUrl, track.Control);
            var setup = await rtsp.RequestAsync("SETUP", trackUrl, [new("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1")], cancel.Token).ConfigureAwait(false);
            if (setup.Status == 461 && source.Secure)
            {
                // Over RTSPS the media stays inside TLS: UDP beside it would be in the clear, and encrypted UDP is SRTP
                // keyed by MIKEY, which isn't supported (decision 2 of docs/decisions/2026-11-15-live-rtsps.md).
                throw new FatalException(new NotSupportedException("The RTSPS server refuses RTP over the TLS connection (461) and wants SRTP over UDP, which isn't supported."));
            }

            if (setup.Status == 461)
            {
                udp = RtpUdpPorts.Bind(rtsp.LocalEndPoint.Address);
                cancel.Token.Register(udp.Dispose);
                connection.Udp = true;
                setup = await Request(rtsp, "SETUP", trackUrl, [new("Transport", $"RTP/AVP;unicast;client_port={udp.RtpPort}-{udp.RtcpPort}")], cancel.Token).ConfigureAwait(false);
            }
            else
            {
                Check(setup, "SETUP");
            }

            var session = setup.Header("Session") ?? throw new InvalidDataException("The camera's SETUP answer has no Session.");
            int semicolon = session.IndexOf(';');
            rtsp.Session = semicolon < 0 ? session.Trim() : session.Substring(0, semicolon).Trim();
            var timeout = TimeSpan.FromSeconds(60);
            if (semicolon >= 0 && System.Text.RegularExpressions.Regex.Match(session, @"timeout\s*=\s*(\d+)") is { Success: true } match)
            {
                timeout = TimeSpan.FromSeconds(Math.Max(2, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
            }

            var transport = RtspTransport.Parse(setup.Header("Transport"));

            // The audio, when asked for: a second SETUP in the same session, on the transport the video's got
            // (docs/decisions/2026-11-15-live-rtsp-audio.md).
            _skippedAudio = source.Audio ? sdp.SkippedAudio : null;
            _audioInfo = null;
            if (source.Audio && sdp.Audio is { } audioTrack)
            {
                audio = await SetupAudioAsync(rtsp, audioTrack, Sdp.Resolve(baseUrl, audioTrack.Control), udp is not null, transport.Channel, cancel.Token).ConfigureAwait(false);
                if (audio?.Udp is { } audioPorts)
                {
                    cancel.Token.Register(audioPorts.Dispose);
                }
            }

            string sessionUrl = Sdp.Resolve(baseUrl, sdp.SessionControl);
            await Request(rtsp, "PLAY", sessionUrl, [new("Range", "npt=0.000-")], cancel.Token).ConfigureAwait(false);
            keepAlive = KeepAliveAsync(rtsp, getParameter ? "GET_PARAMETER" : "OPTIONS", sessionUrl, timeout, cancel.Token);

            // Each track's receiver reports go back the way its packets come: on the RTSP connection's next channel,
            // or from the RTCP port to the server's (docs/decisions/2026-11-15-live-rtcp.md).
            var rtcp = new RtcpSession(VideoClockRate);
            var handle = track.Codec == SdpCodec.Jpeg ? JpegPackets(track, connection, rtcp) : H264Packets(track, connection, rtcp);
            var senders = new List<(RtcpSession Rtcp, Func<byte[], Task> Send)>();
            var ending = cancel.Token;
            if (udp is null)
            {
                var connected = rtsp;
                senders.Add((rtcp, new Func<byte[], Task>(report => connected.SendPacketAsync(transport.Channel + 1, report, ending))));
                if (audio is not null)
                {
                    senders.Add((audio.Rtcp, new Func<byte[], Task>(report => connected.SendPacketAsync(audio.Channel + 1, report, ending))));
                }

                reports = SendReportsAsync(senders, ending);
                await PumpTcpAsync(rtsp, transport.Channel, handle, rtcp, audio, ending).ConfigureAwait(false);
            }
            else
            {
                var server = Plain(transport.Source ?? rtsp.RemoteEndPoint.Address);
                if (transport.ServerRtpPort is { } serverRtp)
                {
                    var serverRtcp = new IPEndPoint(server, transport.ServerRtcpPort ?? serverRtp + 1);
                    udp.Punch(new IPEndPoint(server, serverRtp), serverRtcp);
                    senders.Add((rtcp, Sender(udp, serverRtcp)));
                }

                Observe(ReadReportsAsync(udp, server, rtcp));
                if (audio?.Udp is { } audioUdp)
                {
                    var audioServer = Plain(audio.Transport.Source ?? rtsp.RemoteEndPoint.Address);
                    if (audio.Transport.ServerRtpPort is { } audioRtp)
                    {
                        var audioRtcp = new IPEndPoint(audioServer, audio.Transport.ServerRtcpPort ?? audioRtp + 1);
                        audioUdp.Punch(new IPEndPoint(audioServer, audioRtp), audioRtcp);
                        senders.Add((audio.Rtcp, Sender(audioUdp, audioRtcp)));
                    }

                    Observe(ReadReportsAsync(audioUdp, audioServer, audio.Rtcp));

                    // Until the ending ends (the ending): the video's pump decides when that is.
                    audioPump = PumpUdpAsync(Task.Delay(Timeout.Infinite, ending), audioUdp, audioServer, audio.Transport.Ssrc, audio.Handle, audio.Rtcp, watched: false, ending);
                }

                reports = SendReportsAsync(senders, ending);
                await PumpUdpAsync(DrainAsync(rtsp, ending), udp, server, transport.Ssrc, handle, rtcp, watched: true, ending).ConfigureAwait(false);
            }
        }
        finally
        {
            cancel.Cancel();
            rtsp?.Dispose();
            udp?.Dispose();
            audio?.Udp?.Dispose();
            await watchdog.ConfigureAwait(false);
            if (audioPump is not null)
            {
                try
                {
                    await audioPump.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The session ended (cancelled, or its ports closed): the video's pump has said why.
                }
            }

            if (keepAlive is not null)
            {
                await keepAlive.ConfigureAwait(false);
            }

            if (reports is not null)
            {
                await reports.ConfigureAwait(false);
            }
        }
    }

    // The audio track's decoder and SETUP. Null, with Error saying why, when the decoder refuses the SDP's config or the
    // camera the SETUP: the video plays on without sound (a failing connection still throws, as for the video).
    private async Task<AudioSession?> SetupAudioAsync(RtspConnection rtsp, SdpAudioTrack track, string url, bool overUdp, int videoChannel, CancellationToken token)
    {
        var info = new AudioStreamInfo(track.Codec, track.SampleRate, track.Channels, 0, new Rational(1, track.SampleRate), track.Config, 1);
        IAudioDecoder decoder;
        try
        {
            decoder = AudioPipeline.CreateDecoder(info);
        }
        catch (Exception e)
        {
            if (e is not (InvalidDataException or NotSupportedException))
            {
                throw;
            }

            _error = e;
            return null;
        }

        var ports = overUdp ? RtpUdpPorts.Bind(rtsp.LocalEndPoint.Address) : null;
        try
        {
            string asked = ports is null ? "RTP/AVP/TCP;unicast;interleaved=2-3" : $"RTP/AVP;unicast;client_port={ports.RtpPort}-{ports.RtcpPort}";
            var setup = await rtsp.RequestAsync("SETUP", url, [new("Transport", asked)], token).ConfigureAwait(false);
            if (setup.Status is < 200 or >= 300)
            {
                _error = new IOException($"The camera answered {setup.Status} {setup.Reason} to the audio track's SETUP: playing without audio.");
                ports?.Dispose();
                return null;
            }

            var transport = RtspTransport.Parse(setup.Header("Transport"));

            // An answer that names no channel (read as 0) would put the audio on the video's: the one asked for.
            int channel = transport.Channel == videoChannel ? 2 : transport.Channel;
            _audioInfo = info;
            var rtcp = new RtcpSession(track.SampleRate);
            return new AudioSession(AudioPackets(track, decoder, rtcp), channel, ports, transport, rtcp);
        }
        catch (Exception)
        {
            ports?.Dispose();
            throw;
        }
    }

    // Audio over RTP: AAC's access units (RFC 3640) or G.711's samples to the audio decoding thread, in order. They
    // don't feed the stall watchdog or the state: sound without a picture is not a camera playing.
    private Action<byte[]> AudioPackets(SdpAudioTrack track, IAudioDecoder decoder, RtcpSession rtcp)
    {
        var depacketizer = new RtpAudioDepacketizer(track);
        var frames = new List<byte[]>();
        long lost = 0;
        long damaged = 0;
        bool off = false;
        return packet =>
        {
            if (off)
            {
                return;
            }

            try
            {
                depacketizer.Add(packet, frames);
            }
            catch (NotSupportedException e)
            {
                // Interleaved access units: no sound from this camera, and the picture goes on.
                _error = e;
                _audioInfo = null;
                off = true;
                return;
            }

            Interlocked.Add(ref _lost, depacketizer.LostPackets - lost);
            lost = depacketizer.LostPackets;
            Interlocked.Add(ref _audioDamaged, depacketizer.Damaged - damaged);
            damaged = depacketizer.Damaged;

            // The frames a packet completes are from its RTP timestamp on (a fragmented one's packets share theirs).
            for (int i = 0; i < frames.Count; i++)
            {
                _audioUnits.Enqueue(new QueuedAudio(frames[i], decoder, rtcp, RtpTimestamp(packet), i));
            }

            if (frames.Count > 0)
            {
                frames.Clear();
                _audioReceived.Set();
            }
        };
    }

    // H.264 over RTP (RFC 6184): access units in order to the decoding thread, each with its arrival time.
    private Action<byte[]> H264Packets(SdpVideoTrack track, Connection connection, RtcpSession rtcp)
    {
        int generation = Interlocked.Increment(ref _connection);
        var depacketizer = new RtpH264Depacketizer(track.PayloadType, track.ParameterSets);
        var units = new List<RtpAccessUnit>();
        long lost = 0;
        return packet =>
        {
            depacketizer.Add(packet, units);
            Interlocked.Add(ref _lost, depacketizer.LostPackets - lost);
            lost = depacketizer.LostPackets;
            foreach (var unit in units)
            {
                Interlocked.Increment(ref _receivedCount);
                connection.Frames = true;
                SetState(LiveState.Playing);
                _units.Enqueue(new QueuedUnit(unit, _clock.Elapsed - _origin, generation, rtcp));
            }

            if (units.Count > 0)
            {
                units.Clear();
                _received.Set();
            }
        };
    }

    // JPEG over RTP (RFC 2435): each frame rebuilt as a whole JPEG and offered to decoding as one over HTTP is; an
    // incomplete one is dropped (docs/decisions/2026-11-12-live-rtsp-jpeg.md, decision 1).
    private Action<byte[]> JpegPackets(SdpVideoTrack track, Connection connection, RtcpSession rtcp)
    {
        var depacketizer = new RtpJpegDepacketizer(track.PayloadType, track.Width, track.Height);
        var frames = new List<RtpJpegFrame>();
        long lost = 0;
        return packet =>
        {
            try
            {
                depacketizer.Add(packet, frames);
            }
            catch (NotSupportedException e)
            {
                throw new FatalException(e); // a type or Q no frame of this camera's can be rebuilt from
            }

            Interlocked.Add(ref _lost, depacketizer.LostPackets - lost);
            lost = depacketizer.LostPackets;
            foreach (var frame in frames)
            {
                Interlocked.Increment(ref _receivedCount);
                connection.Frames = true;
                SetState(LiveState.Playing);
                if (frame.Jpeg is null)
                {
                    Interlocked.Increment(ref _damaged);
                }
                else
                {
                    Offer(new Received(frame.Jpeg, _clock.Elapsed - _origin) { CaptureTime = rtcp.CaptureTime(frame.Timestamp) });
                }
            }

            frames.Clear();
        };
    }

    // RTP interleaved on the RTSP connection: the packets of the video's channel, and of the audio's when it has one,
    // until the connection ends, and on the channel after each its RTCP (the camera's sender reports). Only the
    // video's packets count against a stall.
    private async Task PumpTcpAsync(RtspConnection rtsp, int channel, Action<byte[]> handle, RtcpSession rtcp, AudioSession? audio, CancellationToken token)
    {
        int audioChannel = audio?.Channel ?? -1;
        while (await rtsp.ReadAsync(token).ConfigureAwait(false) is { } message)
        {
            if (!message.IsPacket)
            {
                continue; // a keep-alive's answer
            }

            if (message.Channel == channel)
            {
                Touch();
                rtcp.Received(message.Data, _clock.Elapsed);
                handle(message.Data);
            }
            else if (message.Channel == channel + 1)
            {
                rtcp.Report(message.Data, _clock.Elapsed);
            }
            else if (message.Channel == audioChannel)
            {
                audio!.Rtcp.Received(message.Data, _clock.Elapsed);
                audio.Handle(message.Data);
            }
            else if (audio is not null && message.Channel == audioChannel + 1)
            {
                audio.Rtcp.Report(message.Data, _clock.Elapsed);
            }
        }
    }

    // The receiver reports: each track's, every ReportInterval until the connection ends. A camera that times out a
    // reader it hears no RTCP from hears these.
    private async Task SendReportsAsync(List<(RtcpSession Rtcp, Func<byte[], Task> Send)> tracks, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await _clock.Delay(ReportInterval, token).ConfigureAwait(false);
                foreach (var (rtcp, send) in tracks)
                {
                    await send(rtcp.Build(_clock.Elapsed)).ConfigureAwait(false);
                }
            }
        }
        catch (Exception)
        {
            // The connection ended (cancelled, or the write failed): the reading loop sees it too.
        }
    }

    private static Func<byte[], Task> Sender(RtpUdpPorts udp, IPEndPoint to) => report =>
    {
        udp.SendReport(report, to);
        return Task.CompletedTask;
    };

    // RTCP over UDP: the server's sender reports, until the ports close with the session (the receive then throws).
    private async Task ReadReportsAsync(RtpUdpPorts udp, IPAddress server, RtcpSession rtcp)
    {
        while (true)
        {
            var (packet, from) = await udp.ReceiveReportAsync().ConfigureAwait(false);
            if (Plain(from.Address).Equals(server))
            {
                rtcp.Report(packet, _clock.Elapsed);
            }
        }
    }

    // RTP over UDP: the server's packets (its address, and its SSRC when SETUP's answer gave one) through the reorder
    // window, until control ends: the video's is the read of the RTSP connection, which still carries the keep-alives.
    // The video's packets (watched) count against a stall; the audio's don't.
    private async Task PumpUdpAsync(Task control, RtpUdpPorts udp, IPAddress server, uint? ssrc, Action<byte[]> handle, RtcpSession rtcp, bool watched, CancellationToken token)
    {
        var reorder = new RtpReorderBuffer(ReorderWindow);
        var ready = new List<byte[]>();
        var never = Task.Delay(Timeout.Infinite, token);
        Task<(byte[] Packet, IPEndPoint From)>? receive = null;
        try
        {
            while (true)
            {
                receive ??= udp.ReceiveAsync();
                var wait = reorder.Remaining(_clock.Elapsed) is { } remaining ? _clock.Delay(remaining, token) : never;
                var done = await Task.WhenAny(receive, control, wait).ConfigureAwait(false);
                if (done == control)
                {
                    await control.ConfigureAwait(false); // the connection ended, or failed: its exception
                    return;
                }

                // Stopped, or the watchdog's stall: as the read of the RTSP connection throws over TCP.
                token.ThrowIfCancellationRequested();
                if (done == receive)
                {
                    var (packet, from) = await receive.ConfigureAwait(false);
                    receive = null;
                    if (!Plain(from.Address).Equals(server) || (ssrc is { } expected && (packet.Length < 12 || Ssrc(packet) != expected)))
                    {
                        continue; // not this session's stream
                    }

                    if (watched)
                    {
                        Touch();
                    }

                    rtcp.Received(packet, _clock.Elapsed);
                    reorder.Add(packet, _clock.Elapsed, ready);
                }
                else
                {
                    reorder.Poll(_clock.Elapsed, ready);
                }

                foreach (var packet in ready)
                {
                    handle(packet);
                }

                ready.Clear();
            }
        }
        finally
        {
            // The ports close with the session (the caller disposes them), ending a receive still waiting.
            Observe(receive);
            Observe(control);
        }
    }

    // Reads the RTSP connection (keep-alives' answers) until it ends.
    private static async Task DrainAsync(RtspConnection rtsp, CancellationToken token)
    {
        while (await rtsp.ReadAsync(token).ConfigureAwait(false) is not null)
        {
        }
    }

    private static uint RtpTimestamp(byte[] packet) => (uint)((packet[4] << 24) | (packet[5] << 16) | (packet[6] << 8) | packet[7]);

    private static uint Ssrc(byte[] packet) => (uint)((packet[8] << 24) | (packet[9] << 16) | (packet[10] << 8) | packet[11]);

    private static IPAddress Plain(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    // A task left running when the session ends: its exception (the closed socket's) is expected, not unobserved.
    private static void Observe(Task? task) => task?.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    // A JPEG for decoding: it replaces one still waiting, which is skipped (newest wins before decoding).
    private void Offer(Received received)
    {
        if (Interlocked.Exchange(ref _pending, received) is not null)
        {
            Interlocked.Increment(ref _skipped);
        }

        _received.Set();
    }

    // Sends a request and checks its answer (Check).
    private static async Task<RtspMessage> Request(RtspConnection rtsp, string method, string url, KeyValuePair<string, string>[] headers, CancellationToken token) =>
        Check(await rtsp.RequestAsync(method, url, headers, token).ConfigureAwait(false), method);

    // A request's answer: 401 (after the challenge's answer), 403, 404, 454 and 461 are final, as a retry gets the same;
    // another failure may pass on the next connection.
    private static RtspMessage Check(RtspMessage response, string method)
    {
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
            throw new FatalException(new NotSupportedException($"The camera refused RTP both over the RTSP connection and over UDP ({response.Status} {response.Reason} to {method}); multicast isn't supported."));
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

    // RTSP: JPEG frames as MJPEG over HTTP's (the newest waiting), and H.264 access units, every one in order, as later
    // pictures are predicted from earlier ones. A new connection, and a unit that arrived more than MaxLag ago, start a
    // wait for an IDR picture, skipping the units before it; a unit with no picture left (all its slices lost a
    // fragment) is skipped, the next picture's frame_num gap telling the decoder.
    private void DecodeRtsp()
    {
        var token = _stop.Token;
        var maxLag = RtspSource!.MaxLag;
        H264Decoder? decoder = null;
        MjpegDecoder? jpeg = null;
        int generation = 0;
        bool waitForIdr = true;

        // The RTP timestamp extended past 32 bits: each picture's time for the decoder, which its frame comes out with
        // (B pictures are sent before pictures shown ahead of them, so a frame is not its unit's picture).
        long timestamp = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                _received.WaitOne();
                while (!token.IsCancellationRequested && Interlocked.Exchange(ref _pending, null) is { } received)
                {
                    Show(jpeg ??= new MjpegDecoder(), received);
                }

                while (!token.IsCancellationRequested && _units.TryDequeue(out var queued))
                {
                    DecodeGate?.Wait(token);
                    if (queued.Generation != generation)
                    {
                        generation = queued.Generation;
                        decoder = new H264Decoder(ReadOnlySpan<byte>.Empty) { ConcealErrors = true, PacketTimes = true };
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

                    timestamp += (int)(unit.Timestamp - (uint)timestamp);
                    ShowH264(decoder!, queued, timestamp, ref waitForIdr);
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

    // The audio, every frame in order: a frame the decoder rejects is skipped, and what it gives waits for TakeAudio.
    private void DecodeAudio()
    {
        var token = _stop.Token;
        long sincePacket = 0; // the length decoded of the packet's frames so far, in ticks
        try
        {
            while (!token.IsCancellationRequested)
            {
                _audioReceived.WaitOne();
                while (!token.IsCancellationRequested && _audioUnits.TryDequeue(out var queued))
                {
                    if (queued.Index == 0)
                    {
                        sincePacket = 0;
                    }

                    AudioFrame frame;
                    try
                    {
                        frame = queued.Decoder.Decode(queued.Data);
                    }
                    catch (Exception e)
                    {
                        if (e is not (InvalidDataException or NotSupportedException))
                        {
                            throw;
                        }

                        Interlocked.Increment(ref _audioDamaged);
                        continue;
                    }

                    if (frame.SampleCount > 0)
                    {
                        // Captured when the packet's RTP timestamp says, by the camera's clock, and the length of
                        // the packet's earlier frames after that (docs/decisions/2026-11-15-live-rtcp.md, point 3).
                        Interlocked.Increment(ref _audioDecoded);
                        var chunk = AudioPipeline.ToStereo(frame, queued.Rtcp.CaptureTime(queued.Timestamp)?.AddTicks(sincePacket));
                        sincePacket += Ticks(chunk);
                        Queue(chunk);
                    }
                }
            }
        }
        catch (Exception e)
        {
            // A bug: thrown on this thread it would end the process.
            Fail(e);
        }
    }

    // A chunk for TakeAudio: past half a second waiting, the oldest go (decision 3 of
    // docs/decisions/2026-11-15-live-rtsp-audio.md).
    private void Queue(AudioChunk chunk)
    {
        long ticks = Ticks(chunk);
        lock (_audio)
        {
            while (_audio.Count > 0 && _audioQueuedTicks + ticks > AudioQueue.Ticks)
            {
                _audioQueuedTicks -= Ticks(_audio.Dequeue());
                Interlocked.Increment(ref _audioDropped);
            }

            _audio.Enqueue(chunk);
            _audioQueuedTicks += ticks;
        }
    }

    // A chunk's length: its stereo samples at its rate.
    private static long Ticks(AudioChunk chunk) => chunk.Samples.Length / 2 * TimeSpan.TicksPerSecond / chunk.SampleRate;

    private void ShowH264(H264Decoder decoder, QueuedUnit queued, long pts, ref bool waitForIdr)
    {
        var unit = queued.Unit;
        var time = queued.Time;
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
        var rgba = new RgbaFrame(time, frame.Width, frame.Height, pixels, frame.SampleAspectRatio) { CaptureTime = queued.Rtcp.CaptureTime((uint)frame.Pts) };
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
        var rgba = new RgbaFrame(received.Time, frame.Width, frame.Height, pixels, frame.SampleAspectRatio) { CaptureTime = received.CaptureTime };
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
        _audioReceived.Set();
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

        public DateTime? CaptureTime { get; init; }
    }

    private sealed record QueuedUnit(RtpAccessUnit Unit, TimeSpan Time, int Generation, RtcpSession Rtcp);

    // An audio frame and its connection's decoder (a new connection starts a new one).
    // Its RTP timestamp is its packet's, and Index its place among the frames that packet completed.
    private sealed record QueuedAudio(byte[] Data, IAudioDecoder Decoder, RtcpSession Rtcp, uint Timestamp, int Index);

    // The audio track as set up: where its packets go, its channel on the RTSP connection or its UDP ports, and SETUP's answer.
    private sealed record AudioSession(Action<byte[]> Handle, int Channel, RtpUdpPorts? Udp, RtspTransport Transport, RtcpSession Rtcp);

    private sealed class Connection
    {
        public volatile bool Stalled;
        public volatile bool Frames;
        public volatile bool Udp;
    }

    // A reason not to connect again (an answer a retry won't change); its inner exception is the player's Error.
    private sealed class FatalException(Exception inner) : Exception(inner.Message, inner);
}

namespace ManagedCodecs.Playback;

/// <summary>What a <see cref="LivePlayer"/>'s connection is doing.</summary>
public enum LiveState
{
    /// <summary>Not started, or connecting for the first time.</summary>
    Connecting,

    /// <summary>Connected, and a frame has arrived on this connection.</summary>
    Playing,

    /// <summary>
    /// The connection dropped, stalled or was refused for a reason a retry may fix (<see cref="LivePlayer.Error"/> says
    /// which), and the player waits before connecting again.
    /// </summary>
    Reconnecting,

    /// <summary>Disposed.</summary>
    Stopped,

    /// <summary>
    /// Given up, for a reason a retry won't fix (<see cref="LivePlayer.Error"/>): the server answered 401, 403 or 404,
    /// sent something other than MJPEG over HTTP, or the decoding thread hit a bug.
    /// </summary>
    Failed,
}

/// <summary>What a <see cref="LivePlayer"/> received and what became of it, for a status line or a log.</summary>
/// <param name="ReceivedFrames">Parts received whole, on every connection.</param>
/// <param name="DecodedFrames">JPEGs decoded and converted to RGBA.</param>
/// <param name="TakenFrames">Frames <see cref="LivePlayer.TakeLatest"/> returned.</param>
/// <param name="SkippedFrames">
/// JPEGs never decoded because a newer one arrived while the decoder was busy: the camera sends faster than this machine
/// decodes.
/// </param>
/// <param name="DroppedFrames">
/// Frames decoded but replaced by a newer one before <see cref="LivePlayer.TakeLatest"/> took them: the camera sends
/// faster than the caller takes.
/// </param>
/// <param name="DamagedFrames">Parts that weren't a JPEG, or a JPEG the decoder rejected: skipped, not fatal.</param>
/// <param name="Reconnects">Connections made after the first.</param>
/// <param name="DecodeMilliseconds">The decoder's time a frame, the mean over the run.</param>
/// <param name="ConvertMilliseconds">The conversion to RGBA's time a frame, the mean over the run.</param>
public readonly record struct LiveStatistics(
    long ReceivedFrames,
    long DecodedFrames,
    long TakenFrames,
    long SkippedFrames,
    long DroppedFrames,
    long DamagedFrames,
    long Reconnects,
    double DecodeMilliseconds,
    double ConvertMilliseconds);

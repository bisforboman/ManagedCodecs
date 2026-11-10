using System.Diagnostics;

namespace ManagedCodecs.Playback;

/// <summary>
/// The time <see cref="LivePlayer"/> reconnects and detects stalls by: the system's, or a test's own, so that a test of
/// a 5 second stall needn't wait 5 seconds. Not <c>TimeProvider</c>, which the Unity build's netstandard2.1 lacks
/// (docs/decisions/2026-11-10-live-mjpeg-over-http.md).
/// </summary>
internal interface ILiveClock
{
    /// <summary>The time since some fixed start; only differences mean anything.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Completes once <paramref name="delay"/> has passed by this clock; cancelled by the token.</summary>
    Task Delay(TimeSpan delay, CancellationToken token);
}

internal sealed class SystemLiveClock : ILiveClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    /// <inheritdoc/>
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <inheritdoc/>
    public Task Delay(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
}

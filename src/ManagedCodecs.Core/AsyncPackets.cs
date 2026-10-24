using System.Runtime.CompilerServices;

namespace ManagedCodecs;

/// <summary>
/// The packets of a synchronous reader as an asynchronous sequence: the default of <see cref="IDemuxer.ReadPacketsAsync"/>
/// for a demuxer without reads of its own to await.
/// </summary>
internal static class AsyncPackets
{
    /// <summary>
    /// Each packet of <paramref name="packets"/>, yielding the thread between them, so that a caller awaiting packets does not
    /// hold its thread for the whole file; reading a packet itself is synchronous.
    /// </summary>
    public static async IAsyncEnumerable<Packet> Of(IEnumerable<Packet> packets, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var packet in packets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return packet;
            await Task.Yield();
        }
    }
}

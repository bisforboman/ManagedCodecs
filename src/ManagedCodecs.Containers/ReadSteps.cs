using System.Runtime.CompilerServices;

namespace ManagedCodecs.Containers;

/// <summary>
/// One step of a demuxer's packet walk: read <see cref="Buffer"/>'s length of bytes at <see cref="Offset"/> into it, or
/// hand out <see cref="Packet"/>, or a packet's <see cref="Info"/> alone (a walk for <c>ReadPacketInfo</c>, which skips
/// the data). A demuxer walks its file once, as such steps, and <see cref="ReadSteps"/> carries them out with
/// synchronous or asynchronous reads, so both APIs share one parser.
/// </summary>
internal readonly record struct ReadStep(long Offset, Memory<byte> Buffer, Packet? Packet, PacketInfo? Info = null)
{
    public static ReadStep Read(long offset, Memory<byte> buffer) => new(offset, buffer, null);

    public static ReadStep Yield(Packet packet) => new(0, default, packet);

    public static ReadStep Describe(PacketInfo info) => new(0, default, null, info);
}

internal static class ReadSteps
{
    /// <summary>The packets of <paramref name="steps"/>, its reads done synchronously; malformed input as <see cref="Malformed"/> says.</summary>
    public static IEnumerable<Packet> Run(Stream stream, IEnumerable<ReadStep> steps, string format) =>
        Malformed.Guard(RunCore(stream, steps), format);

    /// <summary>
    /// The packets of <paramref name="steps"/>, each read awaited (<see cref="Stream.ReadExactlyAsync(Memory{byte}, CancellationToken)"/>)
    /// and cancellation checked before every step; malformed input as <see cref="Malformed"/> says.
    /// </summary>
    public static async IAsyncEnumerable<Packet> RunAsync(Stream stream, IEnumerable<ReadStep> steps, string format, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var enumerator = steps.GetEnumerator();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Packet packet;
            try
            {
                if (!enumerator.MoveNext())
                {
                    yield break;
                }

                var step = enumerator.Current;
                if (step.Packet is null)
                {
                    stream.Position = step.Offset;
                    await stream.ReadExactlyAsync(step.Buffer, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                packet = step.Packet.Value;
            }
            catch (Exception e) when (Malformed.IsMalformedInput(e))
            {
                throw Malformed.Wrap(e, format);
            }

            yield return packet;
        }
    }

    /// <summary>The packet descriptions of <paramref name="steps"/> (a walk that skips the data), its reads done synchronously.</summary>
    public static IEnumerable<PacketInfo> RunInfo(Stream stream, IEnumerable<ReadStep> steps, string format) =>
        Malformed.Guard(RunInfoCore(stream, steps), format);

    private static IEnumerable<PacketInfo> RunInfoCore(Stream stream, IEnumerable<ReadStep> steps)
    {
        foreach (var step in steps)
        {
            if (step.Info is { } info)
            {
                yield return info;
            }
            else
            {
                stream.Position = step.Offset;
                stream.ReadExactly(step.Buffer.Span);
            }
        }
    }

    private static IEnumerable<Packet> RunCore(Stream stream, IEnumerable<ReadStep> steps)
    {
        foreach (var step in steps)
        {
            if (step.Packet is { } packet)
            {
                yield return packet;
            }
            else
            {
                stream.Position = step.Offset;
                stream.ReadExactly(step.Buffer.Span);
            }
        }
    }
}

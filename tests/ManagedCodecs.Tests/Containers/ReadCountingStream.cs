namespace ManagedCodecs.Tests.Containers;

/// <summary>A read-only memory stream that records the byte count of each synchronous read, for the demuxers' <c>ReadPacketInfo</c> tests.</summary>
internal sealed class ReadCountingStream(byte[] data) : MemoryStream(data, writable: false)
{
    public List<int> Reads { get; } = [];

    /// <summary>A packet without its data, as <c>ReadPacketInfo</c> describes it.</summary>
    public static PacketInfo InfoOf(Packet p) => new(p.StreamIndex, p.Sequence, p.Data.Length, p.IsKeyframe, p.Dts, p.Pts, p.Duration, p.SkipSamples, p.Discard);

    public override int Read(byte[] buffer, int offset, int count)
    {
        Reads.Add(count);
        return base.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        Reads.Add(buffer.Length);
        return base.Read(buffer);
    }
}

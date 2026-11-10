using System.Buffers.Binary;
using System.Globalization;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// <see cref="WavDemuxer"/>: every samples/*.wav against ffprobe's packets (name.packets.csv): each packet's size, MD5,
/// timestamps and duration; and its rules on files built here.
/// </summary>
public class WavDemuxerTests
{
    public static TheoryData<string> Samples() =>
        new(TestSamples.WithReference(".packets.csv").Where(name => name.EndsWith(".wav", StringComparison.Ordinal)));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegPacketByPacket(string sampleName)
    {
        var theirs = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.csv")).Select(Probed.Parse).ToList();
        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        Assert.IsType<WavDemuxer>(demuxer);
        Assert.Null(demuxer.Video);
        var audio = demuxer.Audio!;
        Assert.Equal(new Rational(1, audio.SampleRate), audio.TimeBase);

        var ours = demuxer.ReadPackets().ToList();
        Assert.Equal(theirs.Count, ours.Count);
        Assert.Equal(theirs.Count, audio.FrameCount);
        for (int i = 0; i < ours.Count; i++)
        {
            var (p, e) = (ours[i], theirs[i]);
            Assert.Equal(i, p.Sequence);
            Assert.Equal(e.Size, p.Data.Length);
            Assert.Equal(e.Md5, "MD5:" + FrameMd5.Hash(p.Data.Span));
            Assert.Equal((e.Pts, e.Dts, e.Duration), (p.Pts, p.Dts, p.Duration));
        }

        Assert.Equal(ours.Select(p => (p.Data.Length, p.Dts, p.Duration)), ((WavDemuxer)demuxer).ReadPacketInfo().Select(p => (p.Size, p.Dts, p.Duration)));
    }

    /// <summary>
    /// Chunks before and after 'data' are skipped (an odd-sized one padded to even), the 'data' chunk ends where it says;
    /// a size of 0xFFFFFFFF (a recording never closed) or 0 runs to the end of the file, as ffprobe reads such files.
    /// </summary>
    [Fact]
    public void SkipsOtherChunksAndBoundsTheData()
    {
        var pcm = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        byte[] file = Wave(Format(1, 1, 8000, 1, 8), [.. Chunk("junk", [1, 2, 3])], pcm, [.. Chunk("LIST", new byte[10])]);
        using (var demuxer = new WavDemuxer(new MemoryStream(file)))
        {
            Assert.Equal(CodecIds.PcmU8, demuxer.Audio.Codec);
            var packets = demuxer.ReadPackets().ToList();
            Assert.Equal([4096, 904], packets.Select(p => p.Data.Length));
            Assert.Equal([0L, 4096], packets.Select(p => p.Pts));
            Assert.Equal(pcm, packets.SelectMany(p => p.Data.ToArray()));
        }

        // The same 'data' chunk sized 0xFFFFFFFF, the 'LIST' chunk after it then read as data too.
        byte[] open = Wave(Format(1, 1, 8000, 1, 8), [], pcm, [.. Chunk("LIST", new byte[10])]);
        int size = open.AsSpan().IndexOf("data"u8) + 4;
        BinaryPrimitives.WriteUInt32LittleEndian(open.AsSpan(size), uint.MaxValue);
        using (var unbounded = new WavDemuxer(new MemoryStream(open)))
        {
            Assert.Equal(5000 + 18, unbounded.ReadPackets().Sum(p => p.Data.Length));
        }

        // Sized 0, the same, as ffprobe reads such a file.
        BinaryPrimitives.WriteUInt32LittleEndian(open.AsSpan(size), 0);
        using var zero = new WavDemuxer(new MemoryStream(open));
        Assert.Equal([4096, 922], zero.ReadPackets().Select(p => p.Data.Length));
    }

    /// <summary>
    /// Packets of 4096 bytes rounded down to whole blocks (blocks of 6 bytes: 4092), one block where a block is longer;
    /// WAVE_FORMAT_EXTENSIBLE named by its sub-format, its 22 bytes no codec private data.
    /// </summary>
    [Fact]
    public void CutsWholeBlocksAndReadsTheSubFormat()
    {
        // WAVEFORMATEXTENSIBLE: 24-bit stereo, cbSize 22, valid bits 24, channel mask 3, sub-format GUID starting 1 (PCM).
        byte[] extensible = [.. Format(0xFFFE, 2, 48000, 6, 24), 22, 0, 24, 0, 3, 0, 0, 0, 1, 0, .. new byte[14]];
        using (var demuxer = new WavDemuxer(new MemoryStream(Wave(extensible, [], new byte[10000], []))))
        {
            Assert.Equal(CodecIds.PcmS24Le, demuxer.Audio.Codec);
            Assert.True(demuxer.Audio.CodecPrivate.IsEmpty);
            Assert.Equal([4092, 4092, 1816], demuxer.ReadPackets().Select(p => p.Data.Length));
            Assert.Equal([682L, 682, 302], demuxer.ReadPacketInfo().Select(p => p.Duration));
        }

        using var big = new WavDemuxer(new MemoryStream(Wave(Format(0x0002, 2, 8000, 5000, 4), [], new byte[12000], [])));
        Assert.Equal("0x0002", big.Audio.Codec);
        Assert.Equal([5000, 5000, 2000], big.ReadPackets().Select(p => p.Data.Length));
    }

    /// <summary>A file that is not RIFF WAVE, or whose 'data' comes before any 'fmt ', is refused.</summary>
    [Fact]
    public void RefusesWhatIsNotWave()
    {
        Assert.Throws<InvalidDataException>(() => new WavDemuxer(new MemoryStream(new byte[64])));
        byte[] dataFirst = [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, .. Chunk("data", new byte[8]), .. Chunk("fmt ", Format(1, 1, 8000, 1, 8))];
        Assert.Throws<InvalidDataException>(() => new WavDemuxer(new MemoryStream(dataFirst)));
    }

    // One line of ffprobe -show_packets -show_data_hash MD5 (stream_index, pts, dts, duration, size, flags, data_hash).
    private sealed record Probed(long Pts, long Dts, long Duration, int Size, string Md5)
    {
        public static Probed Parse(string line)
        {
            var f = line.Split(',');
            return new Probed(long.Parse(f[1], CultureInfo.InvariantCulture),
                long.Parse(f[2], CultureInfo.InvariantCulture),
                long.Parse(f[3], CultureInfo.InvariantCulture),
                int.Parse(f[4], CultureInfo.InvariantCulture),
                f[6]);
        }
    }

    private static byte[] Format(int tag, int channels, int rate, int blockAlign, int bits)
    {
        var format = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(format, (ushort)tag);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(format.AsSpan(4), rate);
        BinaryPrimitives.WriteInt32LittleEndian(format.AsSpan(8), rate * blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(12), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(14), (ushort)bits);
        return format;
    }

    private static byte[] Wave(byte[] format, byte[] before, byte[] data, byte[] after)
    {
        byte[] body = [.. "WAVE"u8, .. Chunk("fmt ", format), .. before, .. Chunk("data", data), .. after];
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, body.Length);
        return [.. "RIFF"u8, .. size, .. body];
    }

    private static byte[] Chunk(string id, byte[] payload)
    {
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, payload.Length);
        return [.. System.Text.Encoding.ASCII.GetBytes(id), .. size, .. payload, .. payload.Length % 2 == 1 ? new byte[1] : []];
    }
}

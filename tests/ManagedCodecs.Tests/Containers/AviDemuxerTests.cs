using System.Buffers.Binary;
using System.Text;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

public class AviDemuxerTests
{
    [Fact]
    public void ReadsHeaderAndVideoPackets()
    {
        using var demuxer = new AviDemuxer(new MemoryStream(TinyAvi()));

        Assert.Equal("MJPG", demuxer.Video.Codec);
        Assert.Equal(32, demuxer.Video.Width);
        Assert.Equal(16, demuxer.Video.Height);
        Assert.Equal(25.0, demuxer.Video.FramesPerSecond, precision: 6);
        Assert.Equal(2, demuxer.Video.FrameCount);
        Assert.Equal(new Rational(1, 25), demuxer.Video.TimeBase);

        var packets = demuxer.ReadPackets().Where(p => p.StreamIndex == 0).ToList();

        Assert.Equal(2, packets.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, packets[0].Data.ToArray());          // odd size: padding skipped
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, packets[1].Data.ToArray());       // inside a 'rec ' list
        Assert.Equal(new long[] { 0, 1 }, packets.Select(p => p.Sequence).ToArray());
        Assert.Equal(new long[] { 0, 1 }, packets.Select(p => p.Pts).ToArray());
    }

    /// <summary>
    /// The FourCCs FFmpeg's AVI demuxer reads as MJPEG (each checked with `ffmpeg -c copy -tag:v X` and ffprobe), in any
    /// case as it matches them; others are left as they are. samples/mjpeg-fourcc-dmb1.avi decodes one against FFmpeg.
    /// </summary>
    [Theory]
    [InlineData("MJPG", "MJPG")]
    [InlineData("mjpg", "MJPG")]
    [InlineData("AVI1", "MJPG")]
    [InlineData("dmb1", "MJPG")]
    [InlineData("JPGL", "MJPG")]
    [InlineData("LJPG", "MJPG")]
    [InlineData("IJPG", "MJPG")]
    [InlineData("ACDV", "MJPG")]
    [InlineData("QIVG", "MJPG")]
    [InlineData("SLMJ", "MJPG")]
    [InlineData("CJPG", "MJPG")]
    [InlineData("jpeg", "MJPG")]
    [InlineData("MMJP", "MJPG")]
    [InlineData("MVJP", "MJPG")]
    [InlineData("mjpa", "MJPG")]
    [InlineData("MJPB", "MJPB")] // MJPEG-B, another codec
    [InlineData("XVID", "XVID")]
    public void MapsMjpegFourCCs(string fourCC, string codec)
    {
        using var demuxer = new AviDemuxer(new MemoryStream(TinyAvi(fourCC: fourCC)));
        Assert.Equal(codec, demuxer.Video.Codec);
    }

    /// <summary>
    /// An empty video chunk is a dropped frame: no packet comes out for it, but it takes its tick, as in FFmpeg, so the
    /// packets after it are a frame later.
    /// </summary>
    [Fact]
    public void CountsAnEmptyChunkAsADroppedFrame()
    {
        using var demuxer = new AviDemuxer(new MemoryStream(TinyAvi(dropFrame: true)));

        var packets = demuxer.ReadPackets().Where(p => p.StreamIndex == 0).ToList();

        Assert.Equal(2, packets.Count);
        Assert.Equal(new long[] { 0, 1 }, packets.Select(p => p.Sequence).ToArray());
        Assert.Equal(new long[] { 0, 2 }, packets.Select(p => p.Pts).ToArray());
        Assert.Equal(new long[] { 0, 2 }, packets.Select(p => p.Dts).ToArray());
    }

    /// <summary>
    /// The sample for it really has the empty chunk (its pts, against FFmpeg's, are checked by the MJPEG conformance
    /// tests): a muxer that stops writing one would leave them nothing to catch.
    /// </summary>
    [Fact]
    public void DroppedFrameSampleHasAGap()
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf("avi-dropped-frame.avi")))
        {
            return;
        }

        using var demuxer = AviDemuxer.Open(TestSamples.PathOf("avi-dropped-frame.avi"));
        var pts = demuxer.ReadPackets().Where(p => p.StreamIndex == demuxer.Video.StreamIndex).Select(p => p.Pts).ToList();

        Assert.True(pts.Count < pts[^1] + 1, $"No pts gap in {pts.Count} packets.");
    }

    public static TheoryData<string> Samples() =>
        new(TestSamples.WithReference(".framemd5").Where(name => name.EndsWith(".avi", StringComparison.Ordinal)));

    /// <summary>
    /// <see cref="AviDemuxer.ReadPacketInfo"/> gives <see cref="AviDemuxer.ReadPackets"/>'s packets (whose frames and
    /// timestamps the MJPEG conformance tests check against FFmpeg's) without their data, in the same order: its walk
    /// through the 'movi' list reads only chunk headers, 8 bytes each (but the chunks of MP3 cut anywhere in a frame,
    /// avi-mp3-cbr's, which are read to find the frames).
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void PacketInfo_IsThePacketsWithoutData(string sampleName)
    {
        using var stream = new ReadCountingStream(File.ReadAllBytes(TestSamples.PathOf(sampleName)));
        using var demuxer = new AviDemuxer(stream);
        var expected = demuxer.ReadPackets().Select(ReadCountingStream.InfoOf).ToList();

        stream.Reads.Clear();
        Assert.Equal(expected, demuxer.ReadPacketInfo());
        Assert.NotEmpty(stream.Reads);
        if (!sampleName.Contains("mp3-cbr", StringComparison.Ordinal))
        {
            Assert.All(stream.Reads, count => Assert.Equal(8, count));
        }
    }

    /// <summary>
    /// The audio stream: its format tag as the codec (in hex where it is none of <see cref="CodecIds"/>), and a packet a
    /// chunk where dwSampleSize is 0, in file order with the video's.
    /// </summary>
    [Fact]
    public void ReadsTheAudioStream()
    {
        using var demuxer = new AviDemuxer(new MemoryStream(TinyAvi()));
        var audio = demuxer.Audio!;
        Assert.Equal(("0x0000", 1), (audio.Codec, audio.StreamIndex));

        var packets = demuxer.ReadPackets().ToList();
        Assert.Equal(new[] { 0, 1, 0 }, packets.Select(p => p.StreamIndex));
        Assert.Equal(new byte[] { 9, 9 }, packets[1].Data.ToArray());
        Assert.Equal((0L, 0L, 1L, true), (packets[1].Sequence, packets[1].Pts, packets[1].Duration, packets[1].IsKeyframe));
    }

    /// <summary>The packets without their data from a hand-built file: an odd-sized chunk, a 'rec ' list and a dropped frame.</summary>
    [Fact]
    public void PacketInfo_OfTinyAvi()
    {
        using var demuxer = new AviDemuxer(new MemoryStream(TinyAvi(dropFrame: true)));
        Assert.Equal(demuxer.ReadPackets().Select(ReadCountingStream.InfoOf), demuxer.ReadPacketInfo());
        Assert.Equal(new long[] { 0, 2 }, demuxer.ReadPacketInfo().Where(p => p.StreamIndex == 0).Select(p => p.Pts));
    }

    /// <summary>
    /// Audio with a dwSampleSize, as FFmpeg's AVI demuxer cuts it (each case checked with ffprobe on an FFmpeg AVI whose
    /// PCM chunks were merged): a chunk in pieces of 1024 blocks below 32 bytes a block, of one block from 32 bytes up,
    /// whole at 1 byte; each piece timed by the bytes before it over dwSampleSize, rounded down, and lasting its own
    /// bytes over it. Empty chunks give nothing.
    /// </summary>
    [Theory]
    [InlineData(2, new[] { 5001, 0, 1001 }, new long[] { 2048, 2048, 905, 1001 }, new long[] { 0, 1024, 2048, 2500 }, new long[] { 1024, 1024, 452, 500 })]
    [InlineData(12, new[] { 30001 }, new long[] { 12288, 12288, 5425 }, new long[] { 0, 1024, 2048 }, new long[] { 1024, 1024, 452 })]
    [InlineData(32, new[] { 96 }, new long[] { 32, 32, 32 }, new long[] { 0, 1, 2 }, new long[] { 1, 1, 1 })]
    [InlineData(1, new[] { 5000, 3 }, new long[] { 5000, 3 }, new long[] { 0, 5000 }, new long[] { 5000, 3 })]
    public void CutsAudioChunksAsFfmpeg(int sampleSize, int[] chunks, long[] sizes, long[] pts, long[] durations)
    {
        using var demuxer = new AviDemuxer(new MemoryStream(TinyAvi(audioSampleSize: sampleSize, audioChunks: chunks)));
        var audio = demuxer.Audio!;
        Assert.Equal("0x0001", audio.Codec);
        Assert.Equal(new Rational(1, 8000), audio.TimeBase);

        var packets = demuxer.ReadPackets().Where(p => p.StreamIndex == audio.StreamIndex).ToList();
        Assert.Equal(sizes, packets.Select(p => (long)p.Data.Length));
        Assert.Equal(pts, packets.Select(p => p.Pts));
        Assert.Equal(durations, packets.Select(p => p.Duration));
        Assert.Equal(Enumerable.Range(0, sizes.Length).Select(i => (long)i), packets.Select(p => p.Sequence));
        Assert.Equal(demuxer.ReadPackets().Select(ReadCountingStream.InfoOf), demuxer.ReadPacketInfo());
    }

    [Fact]
    public void RejectsNonAvi()
    {
        var data = Encoding.ASCII.GetBytes("RIFF\u0004\0\0\0WAVE");

        Assert.Throws<InvalidDataException>(() => new AviDemuxer(new MemoryStream(data)));
    }

    // AVI with one MJPEG video stream (index 0) and one audio stream (index 1); with an empty video chunk (a dropped
    // frame) between the two packets if asked.
    /// <summary>
    /// The 'vprp' sample aspect ratio, as FFmpeg's avidec reads it: the picture's ratio (dwFrameAspectRatio, Y in its low
    /// word) over the frame's (dwFrameWidthInPixels, dwFrameHeightInLines), in lowest terms; none with a zero anywhere,
    /// a chunk of 36 bytes or fewer, or no chunk.
    /// </summary>
    [Theory]
    [InlineData(16, 9, 320, 240, 36 + 8, 4, 3)]
    [InlineData(4, 3, 720, 576, 36 + 8, 16, 15)]
    [InlineData(4, 3, 320, 240, 36 + 8, 1, 1)]
    [InlineData(0, 3, 320, 240, 36 + 8, 0, 1)]
    [InlineData(4, 3, 0, 240, 36 + 8, 0, 1)]
    [InlineData(4, 3, 320, 240, 36, 0, 1)]
    public void AspectRatio_FromVprp(int x, int y, int width, int height, int size, int numerator, int denominator)
    {
        var vprp = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(vprp.AsSpan(20), (ushort)y);
        BinaryPrimitives.WriteUInt16LittleEndian(vprp.AsSpan(22), (ushort)x);
        BinaryPrimitives.WriteUInt32LittleEndian(vprp.AsSpan(24), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(vprp.AsSpan(28), (uint)height);
        using var demuxer = new AviDemuxer(new MemoryStream(TinyAvi(vprp: vprp)));
        Assert.Equal(new Rational(numerator, denominator), demuxer.Video!.SampleAspectRatio);
        using var none = new AviDemuxer(new MemoryStream(TinyAvi()));
        Assert.Equal(new Rational(0, 1), none.Video!.SampleAspectRatio);
    }

    private static byte[] TinyAvi(bool dropFrame = false, byte[]? vprp = null, string fourCC = "MJPG", int audioSampleSize = 0, int[]? audioChunks = null)
    {
        var avih = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(0), 40_000); // microseconds per frame = 25 fps
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(16), 2);     // total frames
        BinaryPrimitives.WriteInt32LittleEndian(avih.AsSpan(32), 32);     // width
        BinaryPrimitives.WriteInt32LittleEndian(avih.AsSpan(36), 16);     // height

        var videoStrh = new byte[56];
        Encoding.ASCII.GetBytes("vidsMJPG").CopyTo(videoStrh, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(videoStrh.AsSpan(20), 2);  // dwScale
        BinaryPrimitives.WriteUInt32LittleEndian(videoStrh.AsSpan(24), 50); // dwRate: 2/50 = 1/25

        var videoStrf = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(videoStrf.AsSpan(0), 40);
        Encoding.ASCII.GetBytes(fourCC).CopyTo(videoStrf, 16);

        var audioStrh = new byte[56];
        Encoding.ASCII.GetBytes("auds").CopyTo(audioStrh, 0);
        var audioStrf = new byte[16];
        if (audioChunks is not null)
        {
            // PCM at 8 kHz, a tick a block: dwScale 1, dwRate 8000, dwSampleSize; WAVEFORMATEX's tag 1 (PCM).
            BinaryPrimitives.WriteUInt32LittleEndian(audioStrh.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(audioStrh.AsSpan(24), 8000);
            BinaryPrimitives.WriteUInt32LittleEndian(audioStrh.AsSpan(44), (uint)audioSampleSize);
            BinaryPrimitives.WriteUInt16LittleEndian(audioStrf.AsSpan(0), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(audioStrf.AsSpan(2), 1);
            BinaryPrimitives.WriteInt32LittleEndian(audioStrf.AsSpan(4), 8000);
        }

        var audioData = audioChunks?.Select(n => Chunk("01wb", Enumerable.Range(0, n).Select(i => (byte)i).ToArray())).SelectMany(c => c).ToArray()
            ?? Chunk("01wb", [9, 9]);

        return Riff(
            "AVI ",
            List("hdrl",
                Chunk("avih", avih),
                List("strl", [.. Chunk("strh", videoStrh), .. Chunk("strf", videoStrf), .. vprp is null ? [] : Chunk("vprp", vprp)]),
                List("strl", Chunk("strh", audioStrh), Chunk("strf", audioStrf))),
            List("movi",
                Chunk("00dc", [1, 2, 3]),
                audioData,
                dropFrame ? Chunk("00dc", []) : [],
                List("rec ", Chunk("00dc", [4, 5, 6, 7]))));
    }

    private static byte[] Riff(string type, params byte[][] children) => Container("RIFF", type, children);

    private static byte[] List(string type, params byte[][] children) => Container("LIST", type, children);

    private static byte[] Container(string id, string type, byte[][] children) =>
        Chunk(id, [.. Encoding.ASCII.GetBytes(type), .. children.SelectMany(c => c)]);

    private static byte[] Chunk(string id, byte[] data)
    {
        var chunk = new byte[8 + data.Length + (data.Length & 1)];
        Encoding.ASCII.GetBytes(id).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)data.Length);
        data.CopyTo(chunk, 8);
        return chunk;
    }
}

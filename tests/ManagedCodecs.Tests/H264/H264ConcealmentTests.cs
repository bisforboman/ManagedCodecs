using System.Buffers.Binary;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// Error concealment (<see cref="H264Decoder.ConcealErrors"/>). One packet loses the second half of its last NAL unit, as
/// a lost tail would: the decoder always detects that (the slice data runs out inside a macroblock). Concealed frames
/// match no reference, so the clean decode (bit-exact with FFmpeg by the conformance tests) is the oracle for the rest:
/// the frame count is the clean one's, every frame not flagged <see cref="VideoFrame.IsDamaged"/> is bit-exact with the
/// clean frame of its timestamp, and the flagged ones come close (a low floor, which catches a wrong plane or offset,
/// not the quality of the guess: docs/conformance.md has the PSNR measured against FFmpeg's).
/// </summary>
public class H264ConcealmentTests
{
    // Peak signal-to-noise ratio, in dB, every flagged frame keeps against the clean decode.
    private const double PsnrFloor = 8;

    /// <summary>The damaged packet: the first (an IDR picture, concealed in grey) or the middle one (an inter picture).</summary>
    [Theory]
    [InlineData("h264-slices.mp4", false)] // CAVLC, four slices a picture
    [InlineData("h264-slices.mp4", true)]
    [InlineData("h264-main-slices.mp4", false)] // CABAC, four slices a picture
    [InlineData("h264-main-slices.mp4", true)]
    [InlineData("h264-b-pyramid.mp4", true)] // B pictures predicted from B pictures
    [InlineData("h264-mbaff-b-temporal.mp4", true)] // MBAFF, temporal direct
    [InlineData("h264-444-b.mp4", true)]
    [InlineData("jm/jm-paff-b.mp4", false)] // field pictures
    [InlineData("jm/jm-paff-b.mp4", true)]
    [InlineData("jm-planes.mp4", false)] // separate colour planes: the Cr plane's last slice is cut
    [InlineData("jm-planes.mp4", true)]
    public void CutPacket_ConcealedAndFlagged(string sampleName, bool middle)
    {
        if (!ParameterSetRewriter.Exists(sampleName))
        {
            return;
        }

        var (video, packets) = Read(sampleName);
        int damaged = middle ? packets.Count / 2 : 0;
        var clean = Decode(video, packets, damaged: -1, conceal: false, threads: 1).ToDictionary(f => f.Pts);
        var concealed = Decode(video, packets, damaged, conceal: true, threads: 1);

        Assert.Equal(clean.Count, concealed.Count);
        Assert.Contains(concealed, f => f.Damaged);
        foreach (var frame in concealed)
        {
            var expected = clean[frame.Pts].Data;
            if (frame.Damaged)
            {
                Assert.True(Psnr(expected, frame.Data) > PsnrFloor, $"frame {frame.Pts}: {Psnr(expected, frame.Data):F1} dB");
            }
            else
            {
                Assert.True(expected.AsSpan().SequenceEqual(frame.Data), $"frame {frame.Pts} differs and is not flagged");
            }
        }

        // Frame threading (with random delays, so that pictures overtake each other) gives the same frames and flags:
        // a picture's flag depends on its references' being final first.
        var threaded = Decode(video, packets, damaged, conceal: true, threads: 3, rowDelay: 2);
        Assert.Equal(concealed.Select(f => (f.Pts, f.Damaged, FrameMd5.Hash(f.Data))), threaded.Select(f => (f.Pts, f.Damaged, FrameMd5.Hash(f.Data))));
    }

    /// <summary>Without concealment the damaged picture fails, and so do the pictures predicted from it: no frame that comes out is wrong.</summary>
    [Theory]
    [InlineData("h264-main-slices.mp4", 1)]
    [InlineData("h264-main-slices.mp4", 3)]
    [InlineData("h264-b-pyramid.mp4", 1)]
    [InlineData("h264-b-pyramid.mp4", 3)]
    public void CutPacket_WithoutConcealment_NoWrongFrame(string sampleName, int threads)
    {
        if (!ParameterSetRewriter.Exists(sampleName))
        {
            return;
        }

        var (video, packets) = Read(sampleName);
        var clean = Decode(video, packets, damaged: -1, conceal: false, threads: 1).ToDictionary(f => f.Pts);
        var frames = Decode(video, packets, packets.Count / 3, conceal: false, threads);
        Assert.True(frames.Count < clean.Count);
        Assert.All(frames, f => Assert.True(clean[f.Pts].Data.AsSpan().SequenceEqual(f.Data), $"frame {f.Pts} is wrong"));
        Assert.DoesNotContain(frames, f => f.Damaged);
    }

    /// <summary>
    /// A packet whose structure is damaged (a NAL unit length of zero) fails before its picture starts, and with threads
    /// the frames of earlier pictures that finished while it waited for a decoder are not lost with it: they come out of
    /// the next call, so threads give the frames one thread gives.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BrokenPacket_LosesNoOtherFrame(bool conceal)
    {
        if (!ParameterSetRewriter.Exists("h264-main-slices.mp4"))
        {
            return;
        }

        var (video, packets) = Read("h264-main-slices.mp4");

        // Packet 8's second half zeroed: its second slice's length prefix among the zeros.
        var broken = packets.ToList();
        var data = (byte[])broken[8].Data.Clone();
        data.AsSpan(data.Length / 2).Clear();
        broken[8] = (data, broken[8].Pts);
        var one = Decode(video, broken, damaged: -1, conceal, threads: 1);
        var three = Decode(video, broken, damaged: -1, conceal, threads: 3, rowDelay: 2);
        Assert.Equal(one.Select(f => (f.Pts, FrameMd5.Hash(f.Data))), three.Select(f => (f.Pts, FrameMd5.Hash(f.Data))));
    }

    /// <summary>
    /// A lost packet: a gap in frame_num where it was a reference picture (and nothing where it was not). The frame
    /// inferred for the gap copies the latest reference, so the pictures predicted from it decode, flagged; dropping
    /// the first packet (the IDR picture) leaves the next pictures without any reference, and they conceal in grey.
    /// A field picture stream loses both fields of a frame.
    /// </summary>
    [Theory]
    [InlineData("h264-main-slices.mp4", 0, 1)]
    [InlineData("h264-main-slices.mp4", 8, 1)]
    [InlineData("h264-b-pyramid.mp4", 33, 1)] // a B picture other B pictures predict from, with its MMCO
    [InlineData("h264-mbaff-b-temporal.mp4", 12, 1)]
    [InlineData("openh264/oh-cabac.mp4", 10, 1)] // long-term references
    [InlineData("h264-p-ref1-nodeblock.mp4", 10, 3)] // a gap longer than max_num_ref_frames (1): only its last frame is filled
    [InlineData("jm/jm-paff-b.mp4", 6, 2)]
    [InlineData("jm/jm-paff-b.mp4", 7, 1)] // one field lost: an unpaired reference top field
    [InlineData("jm/jm-paff-b.mp4", 9, 1)] // an unpaired non-reference top field
    [InlineData("jm/jm-paff-b.mp4", 6, 1)] // an unpaired reference bottom field (its top lost)
    [InlineData("jm/jm-paff-b.mp4", 8, 1)] // an unpaired non-reference bottom field
    [InlineData("jm-planes.mp4", 3, 1)] // separate colour planes
    public void DroppedPackets_ConcealedAndFlagged(string sampleName, int first, int count)
    {
        if (!ParameterSetRewriter.Exists(sampleName))
        {
            return;
        }

        var (video, packets) = Read(sampleName);
        var clean = Decode(video, packets, damaged: -1, conceal: false, threads: 1).ToDictionary(f => f.Pts);
        var kept = packets.Where((_, i) => i < first || i >= first + count).ToList();
        var concealed = Decode(video, kept, damaged: -1, conceal: true, threads: 1);

        Assert.Equal(clean.Count - (sampleName.StartsWith("jm/jm-paff", StringComparison.Ordinal) ? count / 2 : count), concealed.Count);
        Assert.Contains(concealed, f => f.Damaged);
        foreach (var frame in concealed)
        {
            // An unpaired bottom field's frame has its own packet's timestamp, not the lost top field's that the clean
            // frame took: it stands for the clean frame just before it.
            var expected = (clean.TryGetValue(frame.Pts, out var same) ? same : clean.Values.Where(f => f.Pts < frame.Pts).MaxBy(f => f.Pts)).Data;
            if (frame.Damaged)
            {
                Assert.True(Psnr(expected, frame.Data) > PsnrFloor, $"frame {frame.Pts}: {Psnr(expected, frame.Data):F1} dB");
            }
            else
            {
                Assert.True(expected.AsSpan().SequenceEqual(frame.Data), $"frame {frame.Pts} differs and is not flagged");
            }
        }

        var threaded = Decode(video, kept, damaged: -1, conceal: true, threads: 3, rowDelay: 2);
        Assert.Equal(concealed.Select(f => (f.Pts, f.Damaged, FrameMd5.Hash(f.Data))), threaded.Select(f => (f.Pts, f.Damaged, FrameMd5.Hash(f.Data))));
    }

    /// <summary>
    /// Without concealment, a lost reference picture (a gap the SPS does not allow) fails every picture up to the next IDR
    /// picture: its reference marking went with it, so later lists could name the wrong pictures without an error.
    /// </summary>
    [Theory]
    [InlineData("h264-b-pyramid.mp4", 1)]
    [InlineData("h264-b-pyramid.mp4", 3)]
    [InlineData("h264-bframes.mp4", 1)]
    public void DroppedPacket_WithoutConcealment_NoWrongFrame(string sampleName, int threads)
    {
        if (!ParameterSetRewriter.Exists(sampleName))
        {
            return;
        }

        var (video, packets) = Read(sampleName);
        var clean = Decode(video, packets, damaged: -1, conceal: false, threads: 1).ToDictionary(f => f.Pts);
        int dropped = sampleName == "h264-bframes.mp4" ? 16 : 33;
        var frames = Decode(video, packets.Where((_, i) => i != dropped).ToList(), damaged: -1, conceal: false, threads);
        Assert.True(frames.Count < clean.Count - 1);
        Assert.All(frames, f => Assert.True(clean[f.Pts].Data.AsSpan().SequenceEqual(f.Data), $"frame {f.Pts} is wrong"));
    }

    private static (VideoStreamInfo Video, List<(byte[] Data, long Pts)> Packets) Read(string sampleName)
    {
        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        var video = demuxer.Video!;
        var packets = demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex).Select(p => (p.Data.ToArray(), p.Pts)).ToList();
        return (video, packets);
    }

    // Decodes every packet, packet `damaged` with its last NAL unit cut to half; what fails is skipped, as a player would.
    private static List<(long Pts, byte[] Data, bool Damaged)> Decode(VideoStreamInfo video, List<(byte[] Data, long Pts)> packets, int damaged, bool conceal, int threads, int rowDelay = 0)
    {
        int lengthSize = (video.CodecPrivate.Span[4] & 3) + 1;

        // The neighbour context checked against its lookups too: macroblocks after a lost slice, and concealed ones.
        var decoder = new H264Decoder(video) { ConcealErrors = conceal, Threads = threads, RowDelay = rowDelay, VerifyNeighbourContext = true };
        var frames = new List<(long, byte[], bool)>();
        void Take(IReadOnlyList<VideoFrame> output)
        {
            foreach (var frame in output)
            {
                var data = new MemoryStream();
                frame.WritePacked(data);
                frames.Add((frame.Pts, data.ToArray(), frame.IsDamaged));
                frame.Dispose();
            }
        }

        for (int i = 0; i < packets.Count; i++)
        {
            try
            {
                Take(decoder.Decode(i == damaged ? CutLastNal(packets[i].Data, lengthSize) : packets[i].Data, packets[i].Pts));
            }
            catch (InvalidDataException)
            {
            }
        }

        while (true)
        {
            try
            {
                Take(decoder.Flush());
                return frames;
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    private static byte[] CutLastNal(byte[] packet, int lengthSize)
    {
        int pos = 0;
        int last = 0;
        while (pos + lengthSize <= packet.Length)
        {
            last = pos;
            pos += lengthSize + (int)ReadLength(packet.AsSpan(pos, lengthSize));
        }

        int kept = (int)ReadLength(packet.AsSpan(last, lengthSize)) / 2;
        var cut = packet.AsSpan(0, last + lengthSize + kept).ToArray();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)kept);
        length[(4 - lengthSize)..].CopyTo(cut.AsSpan(last, lengthSize));
        return cut;
    }

    private static uint ReadLength(ReadOnlySpan<byte> bytes)
    {
        uint value = 0;
        foreach (byte b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    private static double Psnr(byte[] expected, byte[] actual)
    {
        double sum = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            double d = expected[i] - actual[i];
            sum += d * d;
        }

        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 * expected.Length / sum);
    }
}

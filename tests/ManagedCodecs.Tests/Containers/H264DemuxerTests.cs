using System.Globalization;
using System.Text.Json;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// The raw H.264 demuxer against ffprobe (samples/*.h264 and *.264): its packets are FFmpeg's parser's access units
/// (size, MD5, keyframe flag; FFmpeg gives them no times), its stream facts ffprobe's, its time base FFmpeg's frames'
/// (framemd5's). Its SPS reader against the decoder's on every H.264 sample's SPS. The frames themselves are checked
/// against FFmpeg's with the other H.264 samples (H264ConformanceTests).
/// </summary>
public class H264DemuxerTests
{
    public static TheoryData<string> RawSamples() =>
        new(TestSamples.WithReference(".packets.csv").Where(n => n.EndsWith(".h264", StringComparison.Ordinal) || n.EndsWith(".264", StringComparison.Ordinal)));

    [Theory]
    [MemberData(nameof(RawSamples))]
    public void MatchesFfprobePacketByPacket(string sampleName)
    {
        var expected = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.csv")).Select(l => l.Split(',')).ToList();
        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        Assert.IsType<H264Demuxer>(demuxer);
        var packets = demuxer.ReadPackets().ToList();
        Assert.Equal(expected.Count, packets.Count);
        Assert.Equal(packets.Count, demuxer.Video!.FrameCount);
        for (int i = 0; i < packets.Count; i++)
        {
            var (p, e) = (packets[i], expected[i]);
            Assert.Equal(int.Parse(e[4], CultureInfo.InvariantCulture), p.Data.Length);
            Assert.Equal(e[6], "MD5:" + FrameMd5.Hash(p.Data.Span));
            Assert.True(e[5].StartsWith('K') == p.IsKeyframe, $"Packet {i}: keyframe {p.IsKeyframe}, FFmpeg's {e[5]}.");
            Assert.Equal((i, i, i, 1L), (p.Sequence, p.Dts, p.Pts, p.Duration));
        }

        Assert.Equal(packets.Select(ReadCountingStream.InfoOf), ((H264Demuxer)demuxer).ReadPacketInfo());
        Assert.Throws<NotSupportedException>(() => demuxer.Seek(0, 0));
    }

    /// <summary>The size is ffprobe's, the time base FFmpeg's frames' (the VUI's frame rate, a tick a frame).</summary>
    [Theory]
    [MemberData(nameof(RawSamples))]
    public void StreamFactsAreFfmpegs(string sampleName)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(TestSamples.PathOf(sampleName), ".probe.json")));
        var stream = json.RootElement.GetProperty("streams")[0];
        using var demuxer = H264Demuxer.Open(TestSamples.PathOf(sampleName));
        Assert.Equal((stream.GetProperty("width").GetInt32(), stream.GetProperty("height").GetInt32()), (demuxer.Video.Width, demuxer.Video.Height));
        var lines = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".framemd5"));
        Assert.Equal(FrameMd5.ParseTimeBase(lines), demuxer.Video.TimeBase);
        Assert.Equal(1 / demuxer.Video.TimeBase.ToDouble(), demuxer.Video.FramesPerSecond, 9);
    }

    public static TheoryData<string> H264Samples() =>
        new(TestSamples.WithReference(".framemd5").Where(n => n.EndsWith(".mp4", StringComparison.Ordinal) || n.EndsWith(".mkv", StringComparison.Ordinal)));

    /// <summary>
    /// <see cref="SpsInfo"/> (the demuxer's) gives the decoder's size for every SPS of every MP4 and Matroska H.264
    /// sample's configuration record: profiles with and without chroma format and scaling lists, field pairs, cropping.
    /// </summary>
    [Theory]
    [MemberData(nameof(H264Samples))]
    public void SpsReaderAgreesWithTheDecoders(string sampleName)
    {
        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        if (demuxer.Video is not { Codec: CodecIds.H264 } video || video.CodecPrivate.IsEmpty)
        {
            return;
        }

        foreach (var sps in AvcConfiguration.Parse(video.CodecPrivate.Span).Sps)
        {
            var nal = NalUnit.Parse(sps);
            if (nal.Type != NalUnitType.Sps)
            {
                continue;
            }

            var decoders = SequenceParameterSet.Parse(nal.CreateReader());
            var ours = SpsInfo.Parse(sps);
            Assert.NotNull(ours);
            Assert.Equal((decoders.Width, decoders.Height), (ours.Value.Width, ours.Value.Height));
        }
    }
}

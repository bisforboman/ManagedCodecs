using ManagedCodecs.Containers;
using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// Frames are deblocked a row behind decoding, once the macroblocks before the row below are all decoded, whatever
/// order the slices come in. Samples with several slices a picture, decoded with each packet's slices in reverse: the
/// frames must be the ones of the slices in order (which the conformance tests check against FFmpeg). Reversed slices
/// are arbitrary slice order, which only Baseline streams may use, but nothing in decoding a slice depends on the order.
/// </summary>
public class H264SliceOrderTests
{
    public static TheoryData<string> Samples() =>
        new(new[] { "h264-slices.mp4", "h264-main-slices.mp4", "oh-slices-num.mp4", "h264-mbaff-intra-slices.mp4", "h264-mbaff-p-slices.mp4", "h264-mbaff-b-slices-temporal.mp4", "h264-422-mbaff-slices.mp4" }.Where(ParameterSetRewriter.Exists));

    [Theory]
    [MemberData(nameof(Samples))]
    public void SlicesInReverse_GiveTheSameFrames(string sampleName)
    {
        var (inOrder, slicesPerPicture) = Decode(sampleName, reverse: false);
        var (reversed, _) = Decode(sampleName, reverse: true);

        Assert.True(slicesPerPicture > 1, "The sample has one slice a picture: nothing to reorder.");
        Assert.Equal(inOrder.Count, reversed.Count);
        for (int i = 0; i < inOrder.Count; i++)
        {
            for (int plane = 0; plane < 3; plane++)
            {
                Assert.Equal(inOrder[i].Planes[plane], reversed[i].Planes[plane]);
            }
        }
    }

    private static (List<VideoFrame> Frames, int MostSlices) Decode(string sampleName, bool reverse)
    {
        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        var video = demuxer.Video!;
        int lengthSize = AvcConfiguration.Parse(video.CodecPrivate.Span).LengthSize;
        var decoder = new H264Decoder(video);
        var frames = new List<VideoFrame>();
        int mostSlices = 0;
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex))
        {
            var nals = Split(packet.Data.ToArray(), lengthSize);
            bool IsSlice(byte[] nal) => (nal[lengthSize] & 0x1F) is 1 or 5; // non-IDR or IDR slice
            var slices = nals.Where(IsSlice).ToList();
            mostSlices = Math.Max(mostSlices, slices.Count);
            if (reverse)
            {
                slices.Reverse();
            }

            byte[] data = [.. nals.Where(nal => !IsSlice(nal)).Concat(slices).SelectMany(nal => nal)];
            frames.AddRange(decoder.Decode(packet with { Data = data }));
        }

        frames.AddRange(decoder.Flush());
        return (frames, mostSlices);
    }

    // The packet's NAL units, each with its length prefix.
    private static List<byte[]> Split(byte[] data, int lengthSize)
    {
        var nals = new List<byte[]>();
        for (int pos = 0; pos < data.Length;)
        {
            int length = 0;
            for (int i = 0; i < lengthSize; i++)
            {
                length = (length << 8) | data[pos + i];
            }

            nals.Add(data[pos..(pos + lengthSize + length)]);
            pos += lengthSize + length;
        }

        return nals;
    }
}

using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// Monochrome above 10 bits, from JM. JM writes bit_depth_chroma 8 in 4:0:0 whatever the luma's depth, which FFmpeg
/// refuses (it cannot even mux such a stream), so make-jm-samples.sh gives the samples' SPS the luma's depth for chroma
/// too, and the conformance tests check them against FFmpeg (JM's own decoder gives the same frames). Here the SPS goes
/// back to JM's 8 bits: no plane uses the chroma depth (7.4.2.1.1), so the frames are the same.
/// </summary>
public class H264MonochromeDepthTests
{
    [Theory]
    [InlineData("jm-gray-12bit.mp4", PixelFormat.Gray12)]
    [InlineData("jm-gray-14bit.mp4", PixelFormat.Gray14)]
    public void JmChromaDepth_GivesTheSameFrames(string sampleName, PixelFormat format)
    {
        if (!ParameterSetRewriter.Exists(sampleName))
        {
            return;
        }

        var expected = ParameterSetRewriter.Decode(sampleName);
        var actual = ParameterSetRewriter.Decode(sampleName, sps: nal => ParameterSetRewriter.SpsChromaDepth(nal, chromaDepthMinus8: 0));
        Assert.Equal(10, expected.Count);
        Assert.All(expected, f => Assert.Equal(format, f.Format));
        Assert.Equal(expected.Select(FrameMd5.Hash), actual.Select(FrameMd5.Hash));
    }
}

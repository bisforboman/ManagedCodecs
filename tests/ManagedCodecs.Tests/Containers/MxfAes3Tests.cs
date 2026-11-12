using System.Buffers.Binary;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// The 8-channel AES3 element (SMPTE ST 331 7.1, D-10's sound) as PCM, on hand-made elements whose words set every bit
/// beside the audio (channel number, F, V, U, C, P), so only bits 4 to 27 may reach the samples. The D-10 samples
/// (MxfConformanceTests, PcmDecoderTests) check the same against FFmpeg.
/// </summary>
public class MxfAes3Tests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 2)]
    [InlineData(4, 3)]
    [InlineData(8, 3)]
    public void KeepsTheFirstChannelsAudioBits(int channels, int bytes)
    {
        const int samples = 3;
        var element = Element(samples);
        var pcm = new byte[samples * channels * bytes];

        MxfDemuxer.Aes3ToPcm(element, pcm, channels, bytes);

        int o = 0;
        for (int s = 0; s < samples; s++)
        {
            for (int c = 0; c < channels; c++)
            {
                int audio = Audio(s, c);
                int value = bytes == 3 ? pcm[o] | (pcm[o + 1] << 8) | (pcm[o + 2] << 16) : pcm[o] | (pcm[o + 1] << 8);
                Assert.Equal(bytes == 3 ? audio : audio >> 8, value);
                o += bytes;
            }
        }
    }

    // A sample's 24 audio bits for channel c, distinct for every sample and channel, with the top bit set at times.
    private static int Audio(int sample, int channel) => ((sample * 8) + channel + 1) * 0x10_2041 & 0xFF_FFFF;

    // An element: the header (FVUCP valid, sample count little-endian, every channel valid), then 8 words a sample, each
    // with its channel number, the F bit and V, U, C and P set around the audio bits.
    private static byte[] Element(int samples)
    {
        var element = new byte[4 + (samples * 8 * 4)];
        element[0] = 0x80;
        BinaryPrimitives.WriteUInt16LittleEndian(element.AsSpan(1), (ushort)samples);
        element[3] = 0xFF;
        for (int s = 0; s < samples; s++)
        {
            for (int c = 0; c < 8; c++)
            {
                uint word = (uint)c | 0x8 | ((uint)Audio(s, c) << 4) | 0xF000_0000;
                BinaryPrimitives.WriteUInt32LittleEndian(element.AsSpan(4 + (((s * 8) + c) * 4)), word);
            }
        }

        return element;
    }
}

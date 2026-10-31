using System.Runtime.Intrinsics.X86;
using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Tests.Mjpeg;

/// <summary>
/// The scalar IDCT is checked against FFmpeg by the conformance tests; the fast paths must
/// match it exactly for every input, including ones no valid stream produces.
/// </summary>
public class SimpleIdctTests
{
    private const int Stride = 13; // not a multiple of 8, so row offsets are checked too

    public static TheoryData<string> Distributions() => new("full-range", "sparse", "dc-rows");

#if !UNITY_BUILD
    [Theory]
    [MemberData(nameof(Distributions))]
    public void Avx2_MatchesScalar(string distribution)
    {
        if (!Avx2.IsSupported)
        {
            return; // nothing to compare on this machine
        }

        var random = new Random(1234);
        var block = new short[64];
        for (int i = 0; i < 20_000; i++)
        {
            Fill(block, distribution, random);
            var expected = Run(SimpleIdct.PutScalar, block);
            var actual = Run(SimpleIdct.PutAvx2, block);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Block {i} ({distribution}) differs: [{string.Join(", ", block)}]");
        }
    }
#endif

#if !UNITY_BUILD
    /// <summary>
    /// The <see cref="System.Runtime.Intrinsics.Vector128"/> path (ARM64's, and x64's without AVX2)
    /// is called directly, so it runs here too whatever the machine prefers.
    /// </summary>
    [Theory]
    [MemberData(nameof(Distributions))]
    public void Vector128_MatchesScalar(string distribution)
    {
        var random = new Random(5678);
        var block = new short[64];
        for (int i = 0; i < 20_000; i++)
        {
            Fill(block, distribution, random);
            var expected = Run(SimpleIdct.PutScalar, block);
            var actual = Run(SimpleIdct.PutVector128, block);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Block {i} ({distribution}) differs: [{string.Join(", ", block)}]");
        }
    }
#endif

    [Fact]
    public void DcOnly_MatchesScalarForEveryDcValue()
    {
        var block = new short[64];
        var actual = new byte[(7 * Stride) + 8];
        for (int dc = short.MinValue; dc <= short.MaxValue; dc++)
        {
            block[0] = (short)dc;
            var expected = Run(SimpleIdct.PutScalar, block);
            SimpleIdct.PutDcOnly((short)dc, actual, Stride);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"DC {dc} differs.");
        }
    }

    private static byte[] Run(PutMethod put, short[] block)
    {
        var dest = new byte[(7 * Stride) + 8];
        put(block.ToArray(), dest, Stride); // copy: the scalar path transforms in place
        return dest;
    }

    private static void Fill(short[] block, string distribution, Random random)
    {
        Array.Clear(block);
        switch (distribution)
        {
            case "full-range":
                // Anything goes: exercises the 16-bit wrap after the row pass and the clamps.
                for (int j = 0; j < 64; j++)
                {
                    block[j] = (short)random.Next(short.MinValue, short.MaxValue + 1);
                }

                break;

            case "sparse":
                // Like real dequantized blocks: a DC near the level shift and a few small ACs.
                block[0] = (short)random.Next(-1024, 3072);
                for (int n = random.Next(0, 10); n > 0; n--)
                {
                    block[random.Next(1, 64)] = (short)random.Next(-600, 601);
                }

                break;

            case "dc-rows":
                // Some rows DC-only, some not, to check the per-row shortcut is chosen per lane.
                for (int row = 0; row < 8; row++)
                {
                    block[row * 8] = (short)random.Next(-4096, 4097);
                    if (random.Next(2) == 0)
                    {
                        block[(row * 8) + random.Next(1, 8)] = (short)random.Next(-300, 301);
                    }
                }

                break;
        }
    }

    private delegate void PutMethod(Span<short> block, Span<byte> dest, int stride);
}

using System.Runtime.Intrinsics;

namespace ManagedCodecs.Tests;

/// <summary>
/// VectorZip's dispatching operations (SSE2's unpacks and SSSE3's pshufb on x64) give what its portable forms and the
/// definitions do, on random input.
/// </summary>
public class VectorZipTests
{
    private static Vector128<byte> RandomBytes(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return Vector128.Create(bytes);
    }

    [Fact]
    public void Interleaves_MatchThePortableFormsAndTheDefinition()
    {
        var random = new Random(1);
        for (int i = 0; i < 1000; i++)
        {
            var a = RandomBytes(random);
            var b = RandomBytes(random);

            Assert.Equal(VectorZip.PortableLow(a, b), VectorZip.Low(a, b));
            Assert.Equal(VectorZip.PortableHigh(a, b), VectorZip.High(a, b));
            Assert.Equal(VectorZip.PortableLow(a.AsUInt16(), b.AsUInt16()), VectorZip.Low(a.AsUInt16(), b.AsUInt16()));
            Assert.Equal(VectorZip.PortableHigh(a.AsUInt16(), b.AsUInt16()), VectorZip.High(a.AsUInt16(), b.AsUInt16()));
            Assert.Equal(VectorZip.PortableLow(a.AsUInt32(), b.AsUInt32()), VectorZip.Low(a.AsUInt32(), b.AsUInt32()));
            Assert.Equal(VectorZip.PortableHigh(a.AsUInt32(), b.AsUInt32()), VectorZip.High(a.AsUInt32(), b.AsUInt32()));

            var low = VectorZip.Low(a, b);
            var high = VectorZip.High(a, b);
            for (int k = 0; k < 8; k++)
            {
                Assert.Equal(a[k], low[2 * k]);
                Assert.Equal(b[k], low[(2 * k) + 1]);
                Assert.Equal(a[k + 8], high[2 * k]);
                Assert.Equal(b[k + 8], high[(2 * k) + 1]);
            }
        }
    }

    [Fact]
    public void Shuffle_MatchesVector128ShuffleForIndicesBelow16()
    {
        var random = new Random(2);
        for (int i = 0; i < 1000; i++)
        {
            var vector = RandomBytes(random);
            var indices = RandomBytes(random) & Vector128.Create((byte)15);
            Assert.Equal(Vector128.Shuffle(vector, indices), VectorZip.Shuffle(vector, indices));
        }
    }
}

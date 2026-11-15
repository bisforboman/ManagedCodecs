using ManagedCodecs.Rdd36;

namespace ManagedCodecs.Tests.Rdd36;

/// <summary>
/// The inverse transform (<see cref="Rdd36Idct"/>): against samples measured from FFmpeg's decoder, whose arithmetic it
/// follows; its shortcuts against its plain reference on random blocks; and RDD 36's own demand on an inverse
/// transform, the accuracy qualification of Annex A.
/// </summary>
public class Rdd36IdctTests
{
    /// <summary>
    /// Blocks FFmpeg 6.1.1 decoded (coefficients times 8 at 8 * v + u, then its 64 samples), from streams written for
    /// the measurement (docs/decisions/2026-11-15-rdd36-decoder.md): among the ones that tell this arithmetic from
    /// its neighbours.
    /// </summary>
    public static TheoryData<int[], int[]> MeasuredBlocks() => new()
    {
        // W3 is 19265: with 19266 a sample differs.
        {
            [0, 0, -816, 0, 0, 0, 1206, 0, 0, 0, 0, 0, 0, -1176, 0, 0, 0, -2274, 0, 2304, 0, 0, 0, 0, -486, 0, 0, 0, 0, 0, 1926, 1614, 0, 0, 0, 0, 0, 0, 690, 1434, 0, 0, 0, 0, 0, 0, 1848, 0, 1674, 0, 0, 0, 0, 0, 0, 0, 522, 0, 0, 0, 942, 1140, 0, 0],
            [575, 116, 669, 181, 766, 854, 436, 618, 255, 639, 194, 445, 532, 540, 461, 415, 580, 767, 486, 932, 501, 580, 511, 608, 415, 609, 825, 445, 400, 439, 182, 469, 611, 375, 837, 600, 495, 199, 403, 435, 522, 506, 808, 485, 502, 588, 397, 513, 543, 137, 489, 533, 362, 691, 398, 463, 492, 441, 292, 579, 641, 710, 803, 471]
        },
        // W4 is 16384: with 16383 a sample differs.
        {
            [81, 0, 0, 0, 207, 252, 0, 234, -189, 0, 0, -117, 0, 0, 0, 261, 0, 0, 216, 0, 0, 0, 0, 0, -81, 234, 0, 0, -81, 234, 0, 0, 0, 0, 0, -90, 0, -243, 252, 0, 0, 0, 0, 0, 0, 153, 0, 0, 0, -27, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -27, 0, 0, 0],
            [539, 474, 548, 488, 493, 463, 515, 505, 524, 489, 511, 501, 530, 465, 536, 510, 507, 480, 505, 517, 546, 487, 546, 520, 508, 477, 533, 520, 543, 508, 503, 526, 518, 499, 539, 521, 528, 513, 493, 502, 549, 495, 510, 554, 503, 496, 545, 472, 557, 498, 500, 552, 499, 507, 558, 495, 531, 519, 503, 507, 532, 540, 502, 572]
        },
        // Rows are rounded: without 2^14 a sample differs.
        {
            [74, 60, 0, 0, 0, 0, 0, 0, -90, 0, 0, -112, 0, 0, 0, 126, -76, -78, 0, 0, -192, -118, 0, -38, 0, 0, 0, 0, -56, 0, -80, 0, -38, -80, 60, 0, 0, 0, 0, 138, 0, 48, 0, 0, 0, 0, 82, 0, 0, 0, 0, -152, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 172, 0],
            [485, 515, 532, 482, 505, 499, 517, 513, 504, 525, 503, 511, 518, 508, 518, 500, 523, 502, 541, 529, 505, 506, 487, 524, 539, 503, 513, 519, 528, 491, 518, 512, 538, 498, 515, 516, 523, 510, 519, 517, 515, 541, 507, 544, 512, 504, 521, 509, 530, 524, 509, 517, 508, 554, 498, 500, 502, 524, 506, 501, 511, 530, 519, 519]
        },
        // A dense block.
        {
            [96, 96, 0, 24, -120, -72, -192, -144, 48, 0, 48, -120, -144, -120, 24, 144, 96, -96, 192, 24, 144, 144, -168, 0, -168, -120, -96, -48, 120, -48, 168, 96, 192, 24, -120, 192, 24, -168, -120, 24, -24, 24, 96, -48, 144, -192, -24, 192, 24, 24, 48, -120, 192, 72, -96, 120, 96, 168, 96, -96, 0, -120, -168, 24],
            [517, 541, 507, 496, 559, 478, 528, 545, 490, 523, 519, 516, 456, 557, 538, 494, 523, 582, 534, 517, 496, 464, 528, 518, 497, 545, 517, 493, 543, 551, 495, 488, 491, 565, 518, 523, 526, 548, 490, 475, 492, 452, 508, 527, 491, 502, 491, 509, 519, 533, 488, 505, 498, 497, 502, 520, 551, 552, 458, 568, 516, 527, 562, 499]
        },
        // Samples at both clamps, 4 and 1019.
        {
            [540, -2160, -2880, 0, 1200, 3720, -500, 5100, -2100, 840, 0, 1760, -5920, 200, -5040, -1620, 6200, 840, -480, -8260, 3680, 1100, 3280, -7140, -3200, 0, 740, -2240, 3360, 3680, -4240, 5800, 0, 8540, 9760, 1680, -1000, 2500, -2400, 5500, -1440, 1400, 1300, 280, 0, -2760, -1880, -1600, 7000, 2040, 420, 1500, 3180, 920, -6200, 1640, 1960, -720, -1600, -6860, 2480, 1920, 2000, 7080],
            [1019, 1019, 904, 1019, 272, 4, 1019, 419, 4, 4, 411, 999, 4, 1019, 303, 216, 4, 502, 873, 1019, 1019, 1019, 1019, 452, 1019, 1019, 4, 4, 118, 870, 786, 4, 1019, 4, 1019, 4, 1019, 4, 1019, 4, 439, 4, 4, 1019, 1019, 172, 1019, 205, 4, 4, 1019, 1019, 721, 537, 4, 1019, 1019, 591, 1019, 1019, 520, 797, 298, 1019]
        },
    };

    [Theory]
    [MemberData(nameof(MeasuredBlocks))]
    public void GivesTheSamplesFfmpegGave(int[] coefficients, int[] samples)
    {
        var block = coefficients.Select(c => (short)c).ToArray();
        var fast = new ushort[64];
        var reference = new ushort[64];

        Rdd36Idct.Put(block, fast, 8);
        Rdd36Idct.PutReference(block, reference, 8);

        Assert.Equal(samples, reference.Select(s => (int)s));
        Assert.Equal(samples, fast.Select(s => (int)s));
    }

    [Theory]
    // A block with only its first coefficient, as FFmpeg decoded it: sample - 512 is (((x + 1) >> 1) + 8) >> 4, so
    // the steps come at 15, 48, 80 going up and at -17, -49 going down, not at the 16s of an exact rounding.
    [InlineData(0, 512)]
    [InlineData(14, 512)]
    [InlineData(15, 513)]
    [InlineData(46, 513)]
    [InlineData(48, 514)]
    [InlineData(78, 514)]
    [InlineData(80, 515)]
    [InlineData(-16, 512)]
    [InlineData(-18, 511)]
    [InlineData(-48, 511)]
    [InlineData(-50, 510)]
    [InlineData(2000, 575)]
    [InlineData(2046, 576)]
    [InlineData(4000, 637)]
    [InlineData(4094, 640)]
    [InlineData(8000, 762)]
    [InlineData(-2048, 448)]
    [InlineData(-4096, 384)]
    [InlineData(16382, 1019)] // the upper clamp: 1019, not 1023
    public void GivesTheFlatBlocksFfmpegGave(int coefficient, int sample)
    {
        var block = new short[64];
        block[0] = (short)coefficient;
        var fast = new ushort[64];
        var reference = new ushort[64];

        Rdd36Idct.Put(block, fast, 8);
        Rdd36Idct.PutReference(block, reference, 8);

        Assert.All(reference, s => Assert.Equal(sample, s));
        Assert.All(fast, s => Assert.Equal(sample, s));
    }

    [Fact]
    public void ClampsToTheLevelsBetweenTheTimingReferences()
    {
        // 7.5.1 allows either clamp; FFmpeg's is 4 to 1019 (the 10-bit levels outside BT.601's timing references).
        var block = new short[64];
        var samples = new ushort[64];
        block[0] = short.MinValue;
        Rdd36Idct.Put(block, samples, 8);
        Assert.All(samples, s => Assert.Equal(4, s));

        block[0] = short.MaxValue;
        Rdd36Idct.Put(block, samples, 8);
        Assert.All(samples, s => Assert.Equal(1019, s));
    }

    [Theory]
    [InlineData(0)] // as pictures give them: sparse, small
    [InlineData(1)] // dense, within the 12 bits RDD 36 expects of coefficients (times 8: 15 bits)
    [InlineData(2)] // any 16 bits: sums wrap, the same way in both
    [InlineData(3)] // the first row alone
    [InlineData(4)] // the first column alone: every row has only its first coefficient
    [InlineData(5)] // one row, not the first
    public void TheShortcutsGiveWhatTheReferenceGives(int kind)
    {
        var random = new Random(kind);
        var block = new short[64];
        var fast = new ushort[(7 * 11) + 8];
        var reference = new ushort[(7 * 11) + 8];
        for (int round = 0; round < 20000; round++)
        {
            Array.Clear(block);
            switch (kind)
            {
                case 0:
                    block[0] = (short)random.Next(-4000, 4001);
                    for (int i = random.Next(0, 6); i > 0; i--)
                    {
                        block[random.Next(64)] = (short)random.Next(-300, 301);
                    }

                    break;
                case 1:
                    for (int i = 0; i < 64; i++)
                    {
                        block[i] = (short)random.Next(-16384, 16384);
                    }

                    break;
                case 2:
                    for (int i = 0; i < 64; i++)
                    {
                        block[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
                    }

                    break;
                case 3:
                    for (int i = 0; i < 8; i++)
                    {
                        block[i] = (short)random.Next(-2000, 2001);
                    }

                    break;
                case 4:
                    for (int i = 0; i < 8; i++)
                    {
                        block[i * 8] = (short)(random.Next(4) == 0 ? 0 : random.Next(-2000, 2001));
                    }

                    break;
                default:
                    for (int i = 0, row = random.Next(1, 8); i < 8; i++)
                    {
                        block[(row * 8) + i] = (short)random.Next(-2000, 2001);
                    }

                    break;
            }

            Array.Fill(fast, (ushort)0xFFFF);
            Array.Fill(reference, (ushort)0xFFFF);
            Rdd36Idct.Put(block, fast, 11);
            Rdd36Idct.PutReference(block, reference, 11);

            Assert.True(reference.AsSpan().SequenceEqual(fast), $"Round {round}: the shortcuts differ from the reference.");
        }

        // A stride of 11: the three samples between rows were left alone.
        for (int y = 0; y < 7; y++)
        {
            Assert.Equal([0xFFFF, 0xFFFF, 0xFFFF], fast[((y * 11) + 8)..((y + 1) * 11)]);
        }
    }

    [Fact]
    public void TheSamplesAreTheUnroundedValuesRounded()
    {
        // What Annex A is measured on (below) is what the samples come from: (value + 2^17) >> 18, plus 512, clamped.
        var random = new Random(12);
        var block = new short[64];
        var samples = new ushort[64];
        var values = new int[64];
        for (int round = 0; round < 2000; round++)
        {
            for (int i = 0; i < 64; i++)
            {
                block[i] = (short)(random.Next(8) == 0 ? random.Next(-3000, 3001) : 0);
            }

            Rdd36Idct.PutReference(block, samples, 8);
            Rdd36Idct.Unrounded(block, values);

            for (int i = 0; i < 64; i++)
            {
                Assert.Equal(Math.Clamp(((values[i] + (1 << 17)) >> 18) + 512, 4, 1019), samples[i]);
            }
        }
    }

    [Theory]
    [InlineData(2048, 2047, 1)]
    [InlineData(2048, 2047, -1)]
    [InlineData(40, 40, 1)]
    [InlineData(40, 40, -1)]
    [InlineData(2400, 2400, 1)]
    [InlineData(2400, 2400, -1)]
    public void MeetsAnnexAsAccuracyQualification(int low, int high, int sign)
    {
        // RDD 36, Annex A, steps 1 to 7 (after IEEE Std 1180-1990, 3.2): 10,000 blocks of random integers in
        // [-low, high], divided by 8, through a reference forward DCT, rounded to quarter-integers and clipped to
        // [-2048, 2047.75]; then through a reference inverse DCT and through the one under test, both clipped to
        // [-256, 256] at full precision, and the errors between them. The random numbers are the generator of IEEE
        // 1180's appendix, as it is commonly reproduced (the standard itself was not at hand).
        const int Blocks = 10000;
        var peak = new double[64];
        var sum = new double[64];
        var squares = new double[64];
        var pixels = new double[64];
        var coefficients = new double[64];
        var reference = new double[64];
        var block = new short[64];
        var values = new int[64];
        long randx = 1;
        for (int n = 0; n < Blocks; n++)
        {
            for (int i = 0; i < 64; i++)
            {
                randx = ((randx * 1103515245) + 12345) & 0xFFFFFFFF;
                double x = (randx & 0x7FFFFFFE) / (double)0x7FFFFFFF * (low + high + 1);
                pixels[i] = sign * ((long)x - low) / 8.0;
            }

            ReferenceDct(pixels, coefficients, forward: true);
            for (int i = 0; i < 64; i++)
            {
                coefficients[i] = Math.Clamp(Math.Floor((coefficients[i] * 4) + 0.5) / 4, -2048, 2047.75);

                // The transform takes coefficients times 8: quarter-integers are whole there.
                block[i] = (short)(coefficients[i] * 8);
            }

            ReferenceDct(coefficients, reference, forward: false);
            Rdd36Idct.Unrounded(block, values);
            for (int i = 0; i < 64; i++)
            {
                double error = Math.Clamp(values[i] / (double)(1 << 19), -256, 256) - Math.Clamp(reference[i], -256, 256);
                peak[i] = Math.Max(peak[i], Math.Abs(error));
                sum[i] += error;
                squares[i] += error * error;
            }
        }

        // The criteria (1) to (5) that end Annex A.
        Assert.True(peak.Max() <= 0.15, $"Peak error {peak.Max()}.");
        Assert.True(squares.Max() / Blocks <= 0.002, $"Mean square error at a pixel {squares.Max() / Blocks}.");
        Assert.True(squares.Sum() / (64 * Blocks) <= 0.001, $"Overall mean square error {squares.Sum() / (64 * Blocks)}.");
        Assert.True(sum.Max(Math.Abs) / Blocks <= 0.0015, $"Mean error at a pixel {sum.Max(Math.Abs) / Blocks}.");
        Assert.True(Math.Abs(sum.Sum()) / (64 * Blocks) <= 0.00015, $"Overall mean error {Math.Abs(sum.Sum()) / (64 * Blocks)}.");
    }

    [Fact]
    public void RefusesBuffersThatAreTooSmall()
    {
        Assert.Throws<ArgumentException>(() => Rdd36Idct.Put(new short[63], new ushort[64], 8));
        Assert.Throws<ArgumentException>(() => Rdd36Idct.Put(new short[64], new ushort[63], 8));
        Assert.Throws<ArgumentException>(() => Rdd36Idct.Put(new short[64], new ushort[64], 7));
        Assert.Throws<ArgumentException>(() => Rdd36Idct.PutReference(new short[64], new ushort[(7 * 9) + 7], 9));
    }

    /// <summary>
    /// The separable, orthogonal 8x8 DCT of 7.4 and its inverse in double precision:
    /// f[y][x] = 1/4 * sum of C(u) C(v) F[v][u] cos((2x + 1) u pi / 16) cos((2y + 1) v pi / 16), C(0) = 1/sqrt(2).
    /// </summary>
    private static void ReferenceDct(double[] input, double[] output, bool forward)
    {
        // Separable: basis[k][n] = C(k) / 2 * cos((2n + 1) k pi / 16); F = B f B^T, f = B^T F B.
        var basis = new double[8, 8];
        for (int k = 0; k < 8; k++)
        {
            for (int n = 0; n < 8; n++)
            {
                basis[k, n] = (k == 0 ? Math.Sqrt(0.5) : 1) / 2 * Math.Cos(((2 * n) + 1) * k * Math.PI / 16);
            }
        }

        var half = new double[64];
        for (int a = 0; a < 8; a++)
        {
            for (int b = 0; b < 8; b++)
            {
                double total = 0;
                for (int c = 0; c < 8; c++)
                {
                    total += input[(a * 8) + c] * (forward ? basis[b, c] : basis[c, b]);
                }

                half[(a * 8) + b] = total;
            }
        }

        for (int a = 0; a < 8; a++)
        {
            for (int b = 0; b < 8; b++)
            {
                double total = 0;
                for (int c = 0; c < 8; c++)
                {
                    total += half[(c * 8) + b] * (forward ? basis[a, c] : basis[c, a]);
                }

                output[(a * 8) + b] = total;
            }
        }
    }
}

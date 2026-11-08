using ManagedCodecs.Containers;
using ManagedCodecs.Mp3;
#if UNITY_BUILD
using ExactMath = ManagedCodecs.H264.ExactMath; // one assembly: the shared sources are compiled once, into H.264's namespace
using Vlc = ManagedCodecs.H264.Vlc;
#endif

namespace ManagedCodecs.Tests.Mp3;

/// <summary>
/// The MP3 decoder's parts against their definitions: the tables, the IMDCT and synthesis sums with Math.Cos, and what
/// no encoder at hand writes (intensity stereo, MPEG-1's and the lower rates', and mixed blocks), by the spec's
/// equations.
/// </summary>
public class Mp3UnitTests
{
    public static TheoryData<string> HuffmanTables() =>
        new(typeof(Mp3Decoder).Assembly.GetType("ManagedCodecs.Mp3.HuffmanTables")!.GetFields().Select(f => f.Name));

    /// <summary>Every code table is a complete prefix code (its lengths' Kraft sum is 1), each codeword decoding to its value.</summary>
    [Theory]
    [MemberData(nameof(HuffmanTables))]
    public void HuffmanTableIsCompleteAndDecodes(string name)
    {
        var codes = (string[])typeof(ManagedCodecs.Mp3.HuffmanTables).GetField(name)!.GetValue(null)!;
        double kraft = codes.Where(c => c.Length > 0).Sum(c => Math.Pow(2, -c.Length));
        Assert.Equal(1.0, kraft, 12);
        var vlc = new Vlc(codes);
        for (int value = 0; value < codes.Length; value++)
        {
            if (codes[value].Length == 0)
            {
                continue;
            }

            uint peek = Convert.ToUInt32(codes[value], 2) << (vlc.MaxLength - codes[value].Length);
            Assert.Equal(value, vlc.Lookup(peek, out int length));
            Assert.Equal(codes[value].Length, length);
        }
    }

    [Fact]
    public void ExactCosinesMatchMathCos()
    {
        for (int d = 1; d <= 144; d++)
        {
            for (int m = -3 * d; m <= 3 * d; m++)
            {
                // Math.PI * m / d itself is off by up to about 1e-15 at the larger m.
                Assert.Equal(Math.Cos(Math.PI * m / d), ExactMath.CosPi(m, d), 4e-15);
                Assert.Equal(Math.Sin(Math.PI * m / d), ExactMath.SinPi(m, d), 4e-15);
            }
        }
    }

    /// <summary>CRC-16 with x^16 + x^15 + x^2 + 1 from all ones (CRC-16/CMS): its check value for "123456789".</summary>
    [Fact]
    public void Crc16MatchesItsCheckValue()
    {
        // The CRC covers a frame's bytes 2 and 3 and the side information after the CRC (bytes 6 on).
        byte[] frame = [0, 0, (byte)'1', (byte)'2', 0, 0, .. "3456789"u8];
        Assert.Equal(0xAEE7, Mp3Decoder.Crc16(frame, 7));
    }

    [Fact]
    public void LongImdctIsItsSum()
    {
        var random = new Random(1);
        var lines = Enumerable.Range(0, 18).Select(_ => random.NextDouble() - 0.5).ToArray();
        var ones = Enumerable.Repeat(1.0, 36).ToArray();
        var output = new double[36];
        Hybrid.LongImdct(lines, ones, output);
        for (int i = 0; i < 36; i++)
        {
            double sum = 0;
            for (int k = 0; k < 18; k++)
            {
                sum += lines[k] * Math.Cos(Math.PI / 72 * ((2 * i) + 1 + 18) * ((2 * k) + 1));
            }

            Assert.Equal(sum, output[i], 1e-13);
        }
    }

    [Fact]
    public void ShortImdctIsItsWindowedSums()
    {
        var random = new Random(2);
        var lines = Enumerable.Range(0, 18).Select(_ => random.NextDouble() - 0.5).ToArray();
        var output = new double[36];
        Hybrid.ShortImdct(lines, output);
        var expected = new double[36];
        for (int w = 0; w < 3; w++)
        {
            for (int i = 0; i < 12; i++)
            {
                double sum = 0;
                for (int k = 0; k < 6; k++)
                {
                    sum += lines[(6 * w) + k] * Math.Cos(Math.PI / 24 * ((2 * i) + 1 + 6) * ((2 * k) + 1));
                }

                expected[6 + (6 * w) + i] += sum * Math.Sin(Math.PI / 12 * (i + 0.5));
            }
        }

        for (int i = 0; i < 36; i++)
        {
            Assert.Equal(expected[i], output[i], 1e-13);
        }
    }

    /// <summary>Requantization's table of 2^(q / 4) gives ExactMath's values bit for bit, inside its range and out.</summary>
    [Fact]
    public void QuarterPowerTableIsExactMaths()
    {
        for (int q = -1200; q <= 400; q++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(ExactMath.QuarterPowerOfTwo(q)), BitConverter.DoubleToInt64Bits(Spectrum.QuarterPower(q)));
        }
    }

    /// <summary>The synthesis filterbank against Annex A's flow chart, step by step with Math.Cos, over 40 time slots.</summary>
    [Fact]
    public void SynthesisIsTheFlowChart()
    {
        var random = new Random(3);
        var synthesis = new Synthesis();
        var v = new double[1024];
        var output = new float[32];
        for (int slot = 0; slot < 40; slot++)
        {
            var s = Enumerable.Range(0, 32).Select(_ => random.NextDouble() - 0.5).ToArray();
            synthesis.Process(s, output);

            for (int i = 1023; i >= 64; i--)
            {
                v[i] = v[i - 64];
            }

            for (int i = 0; i < 64; i++)
            {
                v[i] = Enumerable.Range(0, 32).Sum(k => Math.Cos((16 + i) * ((2 * k) + 1) * Math.PI / 64) * s[k]);
            }

            var u = new double[512];
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 32; j++)
                {
                    u[(64 * i) + j] = v[(128 * i) + j];
                    u[(64 * i) + 32 + j] = v[(128 * i) + 96 + j];
                }
            }

            for (int j = 0; j < 32; j++)
            {
                double sum = 0;
                for (int i = 0; i < 16; i++)
                {
                    sum += u[j + (32 * i)] * Mp3Tables.SynthesisWindow[j + (32 * i)] / 65536.0;
                }

                Assert.Equal(sum, output[j], 1e-6);
            }
        }
    }

    /// <summary>
    /// The vector synthesis and long IMDCT give the definitions' bits (<see cref="Synthesis.SynthesizeReference"/>,
    /// <see cref="Hybrid.LongImdctReference"/>), on random input of every magnitude and on silence, and random windows.
    /// </summary>
    [Fact]
    public void FastFilterbanksGiveTheDefinitionsBits()
    {
        var random = new Random(17);
        double Sample() => random.Next(8) switch
        {
            0 => 0,
            1 => random.NextDouble() * 1e-30,
            _ => (random.NextDouble() - 0.5) * Math.Pow(2, random.Next(-20, 16)),
        };

        var fast = new Synthesis();
        var reference = new Synthesis();
        var output = new double[32];
        var expected = new double[32];
        for (int slot = 0; slot < 2000; slot++)
        {
            var s = slot % 50 < 5 ? new double[32] : Enumerable.Range(0, 32).Select(_ => Sample()).ToArray();
            fast.Synthesize(s, output);
            reference.SynthesizeReference(s, expected);

            // The same doubles (V may hold -0 where the definition has +0, for a row that sums to zero: == takes them
            // as equal, and the outputs, sums from +0, as the same bits).
            for (int i = 0; i < 1024; i++)
            {
                Assert.True(fast.V[i] == reference.V[i], $"Slot {slot}: v[{i}] {fast.V[i]:R}, by the definition {reference.V[i]:R}.");
            }

            for (int j = 0; j < 32; j++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected[j]), BitConverter.DoubleToInt64Bits(output[j]));
            }
        }

        var windows = Enumerable.Range(0, 3).Select(_ => Enumerable.Range(0, 36).Select(_ => random.NextDouble()).ToArray()).ToArray();
        var block = new double[36];
        var expectedBlock = new double[36];
        for (int n = 0; n < 2000; n++)
        {
            var lines = Enumerable.Range(0, 18).Select(_ => Sample()).ToArray();
            var window = windows[n % 3];
            Hybrid.LongImdct(lines, window, block);
            Hybrid.LongImdctReference(lines, window, expectedBlock);
            for (int i = 0; i < 36; i++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(expectedBlock[i]), BitConverter.DoubleToInt64Bits(block[i]));
            }
        }
    }

    /// <summary>
    /// MPEG-1 intensity stereo in long blocks (2.4.3.4.9.3): above the right channel's last nonzero band the left
    /// channel's values become L = k / (1 + k) and R = 1 / (1 + k) of them, k = tan(is_pos pi / 12); is_pos 7 is illegal
    /// (mid/side there when on); band 21 takes band 20's position (band 20 being in intensity stereo here); below the
    /// bound, mid/side or nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mpeg1IntensityStereo(bool midSide)
    {
        var header = Header(0xFB, 0x90, midSide ? 0x70 : 0x50); // MPEG-1, 128 kbit/s, 44.1 kHz, joint stereo
        var right = new Granule();
        int[] positions = new int[40];
        for (int band = 0; band < 21; band++)
        {
            positions[band] = band % 8; // 7: illegal
        }

        var bands = Mp3Tables.LongBands[0];
        var (left, rightSpectrum) = Spectra(4, bands[12]); // the right channel nonzero below band 12
        var l0 = (double[])left.Clone();
        var r0 = (double[])rightSpectrum.Clone();
        Stereo.Process(header, right, positions, new bool[40], left, rightSpectrum);

        for (int band = 0; band < 22; band++)
        {
            int position = positions[band == 21 ? 20 : band];
            for (int i = bands[band]; i < bands[band + 1]; i++)
            {
                if (band >= 12 && position != 7)
                {
                    double k = Math.Tan(position * Math.PI / 12);
                    Assert.Equal(l0[i] * (position == 6 ? 1 : k / (1 + k)), left[i], 1e-12);
                    Assert.Equal(l0[i] * (position == 6 ? 0 : 1 / (1 + k)), rightSpectrum[i], 1e-12);
                }
                else
                {
                    AssertMidSide(midSide, l0[i], r0[i], left[i], rightSpectrum[i]);
                }
            }
        }
    }

    /// <summary>
    /// The lower rates' intensity stereo (13818-3 2.4.3.2), in short blocks: each window's bound on its own; is_pos 0
    /// keeps both, an odd one scales the left channel by io^((is_pos + 1) / 2), an even one the right by io^(is_pos / 2),
    /// io 2^(-1/4) for intensity_scale 0 and 2^(-1/2) for 1; a position at its partition's largest value is illegal;
    /// short band 12 takes band 11's (band 11 being in intensity stereo in the windows that reach it here).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void LsfIntensityStereoInShortBlocks(int intensityScale)
    {
        var header = Header(0xF3, 0x80, 0x50); // MPEG-2, 64 kbit/s, 22.05 kHz, joint stereo (intensity only)
        var right = new Granule { BlockType = 2, ScalefacCompress = 300 + intensityScale };
        var shortBands = Mp3Tables.ShortBands[3];
        int[] positions = new int[40];
        bool[] illegal = new bool[40];
        for (int i = 0; i < 39; i++)
        {
            positions[i] = (i * 5) % 9;
            illegal[i] = i % 11 == 10;
        }

        // The right channel: nonzero in window 0 below band 6, zero in window 1, nonzero in window 2 below band 11.
        var random = new Random(5);
        var left = new double[576];
        var rightSpectrum = new double[576];
        int line = 0;
        for (int band = 0; band < 13; band++)
        {
            for (int w = 0; w < 3; w++)
            {
                for (int i = 0; i < shortBands[band + 1] - shortBands[band]; i++, line++)
                {
                    left[line] = random.NextDouble() - 0.5;
                    rightSpectrum[line] = (w == 0 && band < 6) || (w == 2 && band < 11) ? random.NextDouble() - 0.5 : 0;
                }
            }
        }

        var l0 = (double[])left.Clone();
        var r0 = (double[])rightSpectrum.Clone();
        Stereo.Process(header, right, positions, illegal, left, rightSpectrum);

        double io = intensityScale == 0 ? Math.Pow(2, -0.25) : Math.Sqrt(0.5);
        line = 0;
        for (int band = 0; band < 13; band++)
        {
            for (int w = 0; w < 3; w++)
            {
                int index = (3 * (band == 12 ? 11 : band)) + w;
                bool intensity = (w == 0 ? band >= 6 : w == 1 || band >= 11) && !illegal[index];
                int p = positions[index];
                for (int i = 0; i < shortBands[band + 1] - shortBands[band]; i++, line++)
                {
                    double expectedLeft = intensity && p % 2 == 1 ? l0[line] * Math.Pow(io, (p + 1) / 2) : intensity ? l0[line] : l0[line];
                    double expectedRight = !intensity ? r0[line] : p % 2 == 1 ? l0[line] : l0[line] * Math.Pow(io, p / 2);
                    Assert.Equal(expectedLeft, left[line], 1e-12);
                    Assert.Equal(expectedRight, rightSpectrum[line], 1e-12);
                }
            }
        }
    }

    /// <summary>Mid/side alone: L = (M + S) / sqrt(2), R = (M - S) / sqrt(2) over every line.</summary>
    [Fact]
    public void MidSideStereo()
    {
        var header = Header(0xFB, 0x90, 0x60);
        var (left, right) = Spectra(6, 576);
        var l0 = (double[])left.Clone();
        var r0 = (double[])right.Clone();
        Stereo.Process(header, new Granule(), new int[40], new bool[40], left, right);
        for (int i = 0; i < 576; i++)
        {
            AssertMidSide(true, l0[i], r0[i], left[i], right[i]);
        }
    }

    /// <summary>
    /// A mixed block (2.4.2.7): its lowest subbands keep long blocks (2, or 4 at 8 kHz, below line 72) with the normal
    /// window, aliasing reduced only between subbands 0 and 1; the rest are short blocks, reordered from coded order.
    /// </summary>
    [Theory]
    [InlineData(0, 2)]
    [InlineData(8, 4)]
    public void MixedBlocks(int rateIndex, int longSubbands)
    {
        bool lsf = rateIndex >= 3;
        var g = new Granule { BlockType = 2, Mixed = true };

        // One line at the top of the last long subband, which only alias reduction across its upper edge would move.
        var spectrum = new double[576];
        int at = 18 * (longSubbands - 1);
        spectrum[at + 17] = 1;
        var output = new double[576];
        Hybrid.Process(g, lsf, rateIndex, spectrum, new double[576], output);
        var expected = new double[36];
        var lines = new double[18];
        lines[17] = 1;
        Hybrid.LongImdct(lines, Enumerable.Range(0, 36).Select(i => Math.Sin(Math.PI / 36 * (i + 0.5))).ToArray(), expected);
        for (int t = 0; t < 18; t++)
        {
            double sign = (longSubbands - 1) % 2 == 1 && t % 2 == 1 ? -1 : 1;
            Assert.Equal(sign * expected[t], output[at + t], 1e-12);
        }

        // The short part's first band (3) in coded order, window by window, lands in subband order.
        var shortBands = Mp3Tables.ShortBands[rateIndex];
        var coded = new double[576];
        int start = 3 * shortBands[3];
        for (int i = start; i < 576; i++)
        {
            coded[i] = i;
        }

        var reordered = (double[])coded.Clone();
        Hybrid.Reorder(reordered, rateIndex, 3);
        int width = shortBands[4] - shortBands[3];
        for (int w = 0; w < 3; w++)
        {
            for (int i = 0; i < width; i++)
            {
                int f = shortBands[3] + i;
                Assert.Equal(start + (w * width) + i, reordered[(18 * (f / 6)) + (6 * w) + (f % 6)]);
            }
        }
    }

    /// <summary>
    /// The last band (long 21, short 12) has no scalefactor. At the lower rates, after a band the right channel codes a
    /// value in, it is the centre (position 0: both channels the left's value), not that band's position, as the
    /// ISO/IEC 11172-4 streams l3-test45 and l3-test46 need it (they miss their reference by 1e-2 otherwise); MPEG-1
    /// copies band 20's position either way, as FFmpeg does (mkv-mp3-intensity, which reaches it, compares them).
    /// </summary>
    [Fact]
    public void LastBandIsTheCentreAfterACodedBand()
    {
        // MPEG-1, long blocks: band 20 coded, band 21 takes its position 1.
        var header = Header(0xFB, 0x90, 0x50); // MPEG-1, 44.1 kHz, joint stereo (intensity only)
        var bands = Mp3Tables.LongBands[0];
        int[] positions = new int[40];
        positions[20] = 1;
        var (left, rightSpectrum) = Spectra(6, bands[21]); // nonzero up to band 20's end
        var l0 = (double[])left.Clone();
        Stereo.Process(header, new Granule(), positions, new bool[40], left, rightSpectrum);
        double k = Math.Tan(Math.PI / 12);
        for (int i = bands[21]; i < 576; i++)
        {
            Assert.Equal(l0[i] * k / (1 + k), left[i], 1e-12);
            Assert.Equal(l0[i] / (1 + k), rightSpectrum[i], 1e-12);
        }

        // The lower rates, long blocks: band 20 coded, band 21 the centre; position 2 would scale the right by io.
        header = Header(0xF3, 0x80, 0x50); // MPEG-2, 22.05 kHz, joint stereo (intensity only)
        bands = Mp3Tables.LongBands[3];
        (left, rightSpectrum) = Spectra(7, bands[21]);
        l0 = (double[])left.Clone();
        Stereo.Process(header, new Granule { ScalefacCompress = 300 }, positions, new bool[40], left, rightSpectrum);
        for (int i = bands[21]; i < 576; i++)
        {
            Assert.Equal(l0[i], left[i], 1e-12);
            Assert.Equal(l0[i], rightSpectrum[i], 1e-12);
        }

        // Short blocks: window 1 codes a value in band 11, so its band 12 is the centre; window 0 doesn't, so its band
        // 12 takes band 11's position (2: the right scaled by io^1).
        var shortBands = Mp3Tables.ShortBands[3];
        var right = new Granule { BlockType = 2, ScalefacCompress = 300 };
        positions = new int[40];
        positions[(3 * 11) + 0] = 2;
        positions[(3 * 11) + 1] = 2;
        left = [.. Enumerable.Range(0, 576).Select(i => 1.0)];
        rightSpectrum = new double[576];
        int width11 = shortBands[12] - shortBands[11];
        int width12 = 192 - shortBands[12];
        rightSpectrum[(3 * shortBands[11]) + width11] = 1; // band 11, window 1
        Stereo.Process(header, right, positions, new bool[40], left, rightSpectrum);
        int band12 = 3 * shortBands[12];
        Assert.Equal(1, left[band12 + width12], 1e-12); // window 1: the centre
        Assert.Equal(1, rightSpectrum[band12 + width12], 1e-12);
        Assert.Equal(1, left[band12], 1e-12); // window 0: band 11's position 2
        Assert.Equal(Math.Pow(2, -0.25), rightSpectrum[band12], 1e-12);
    }

    /// <summary>
    /// A frame cut short (a file's last, as in the ISO/IEC 11172-4 streams l3-compl and l3-sin1k0db) decodes as far as
    /// its granules' data is there, as FFmpeg decodes it: a LAME frame whose granules end before its last byte gives the
    /// same samples without that byte; one cut inside its granules' data is refused.
    /// </summary>
    [Fact]
    public void FrameCutShortDecodesAsFarAsItsData()
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf("mp3-stereo.mp4")))
        {
            return;
        }

        List<byte[]> packets;
        using (var demuxer = Demuxer.Open(TestSamples.PathOf("mp3-stereo.mp4")))
        {
            int stream = demuxer.Audio!.StreamIndex;
            packets = [.. demuxer.ReadPackets().Where(p => p.StreamIndex == stream).Select(p => p.Data.ToArray())];
        }

        int matched = 0;
        for (int cut = 1; cut < Math.Min(packets.Count, 20); cut++)
        {
            var whole = new Mp3Decoder();
            var shortened = new Mp3Decoder();
            for (int i = 0; i < cut; i++)
            {
                whole.Decode(packets[i]);
                shortened.Decode(packets[i]);
            }

            var expected = whole.Decode(packets[cut]);
            try
            {
                var actual = shortened.Decode(packets[cut].AsSpan(0, packets[cut].Length - 1));
                Assert.Equal(expected.Samples, actual.Samples);
                Assert.Equal(1, shortened.Statistics.TruncatedFrames);
                matched++;
            }
            catch (InvalidDataException)
            {
                // Its granules' data reached the last byte.
            }
        }

        Assert.True(matched > 0, "No frame whose granules end before its last byte.");
        Assert.Throws<InvalidDataException>(() => new Mp3Decoder().Decode(packets[1].AsSpan(0, 40)));
    }

    private static FrameHeader Header(byte second, byte third, int fourth)
    {
        Assert.True(FrameHeader.TryParse([0xFF, second, third, (byte)fourth], out var header));
        return header;
    }

    private static (double[] Left, double[] Right) Spectra(int seed, int rightEnd)
    {
        var random = new Random(seed);
        var left = Enumerable.Range(0, 576).Select(_ => random.NextDouble() - 0.5).ToArray();
        var right = Enumerable.Range(0, 576).Select(i => i < rightEnd ? random.NextDouble() - 0.5 : 0).ToArray();
        return (left, right);
    }

    private static void AssertMidSide(bool midSide, double m, double s, double left, double right)
    {
        Assert.Equal(midSide ? (m + s) / Math.Sqrt(2) : m, left, 1e-12);
        Assert.Equal(midSide ? (m - s) / Math.Sqrt(2) : s, right, 1e-12);
    }
}

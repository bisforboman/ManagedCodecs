using ManagedCodecs.Aac;
using ManagedCodecs.Aac.Sbr;
using ManagedCodecs.Containers;
#if UNITY_BUILD
using Vlc = ManagedCodecs.H264.Vlc; // one assembly: the shared Vlc is compiled once, into H.264's namespace
#endif

namespace ManagedCodecs.Tests.Aac;

/// <summary>
/// Spectral band replication as far as it is read: the codebooks, the frequency tables, the signalling in the config
/// and in the fill elements, and every frame of the HE-AAC samples (samples/heaac/, from the FDK encoder) read to the
/// last bit of its payload.
/// </summary>
public class SbrParsingTests
{
    public static TheoryData<string> Samples() =>
        new(new[] { "heaac-stereo-44k.mp4", "heaac-mono-48k.mp4", "heaac-stereo-low.mp4", "heaac-5.1.mp4", "heaac-adts.aac", "heaacv2-stereo.mp4", "heaac-header2.mp4" }
            .Where(name => TestSamples.Directory is not null && File.Exists(TestSamples.PathOf(name))));

    [Fact]
    public void Codebooks_ArePrefixFreeAndComplete()
    {
        // Each codebook codes the deltas from -LAV to LAV (Annex 4.A), the whole code space, with 0 the most likely.
        (string[] Book, int Largest)[] books =
        [
            (SbrTables.EnvelopeTime15, 60), (SbrTables.EnvelopeFrequency15, 60), (SbrTables.BalanceTime15, 24),
            (SbrTables.BalanceFrequency15, 24), (SbrTables.EnvelopeTime30, 31), (SbrTables.EnvelopeFrequency30, 31),
            (SbrTables.BalanceTime30, 12), (SbrTables.BalanceFrequency30, 12), (SbrTables.NoiseTime, 31),
            (SbrTables.NoiseBalanceTime, 12),
        ];
        foreach (var (book, largest) in books)
        {
            Assert.Equal((2 * largest) + 1, book.Length);
            _ = new Vlc(book);
            Assert.Equal(1.0, book.Sum(code => Math.Pow(2, -code.Length)), precision: 12);
            Assert.Equal(book.Min(code => code.Length), book[largest].Length);
        }
    }

    /// <summary>
    /// The start and stop bands from the formulas of 4.6.18.3.2.1 equal the PacketVideo decoder's precomputed tables
    /// (its minimum start band per rate, and its stop bands for bs_stop_freq 0 to 12).
    /// </summary>
    [Fact]
    public void StartAndStopBands_MatchTheReferenceTables()
    {
        (int Rate, int MinStart)[] starts = [(16000, 24), (22050, 17), (24000, 16), (32000, 16), (44100, 12), (48000, 11), (64000, 10), (88200, 7), (96000, 7)];
        foreach (var (rate, minStart) in starts)
        {
            int row = rate switch { 16000 => 0, 22050 => 1, 24000 => 2, 32000 => 3, <= 64000 => 4, _ => 5 };
            for (int startFrequency = 0; startFrequency < 16; startFrequency++)
            {
                Assert.Equal(minStart + SbrTables.StartOffsets[row][startFrequency], SbrFrequencies.StartBand(rate, startFrequency));
            }
        }

        (int Rate, int[] Stops)[] stops =
        [
            (48000, [21, 23, 25, 27, 29, 32, 35, 38, 41, 45, 49, 54, 59]),
            (44100, [23, 25, 27, 29, 31, 34, 37, 40, 43, 47, 51, 55, 59]),
            (32000, [32, 34, 36, 38, 40, 42, 44, 46, 49, 52, 55, 58, 61]),
            (24000, [32, 34, 36, 38, 40, 42, 44, 46, 49, 52, 55, 58, 61]),
            (22050, [35, 36, 38, 40, 42, 44, 46, 48, 50, 52, 55, 58, 61]),
            (16000, [48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 60, 62]),
        ];
        foreach (var (rate, table) in stops)
        {
            Assert.Equal(table, Enumerable.Range(0, 13).Select(stop => SbrFrequencies.StopBand(rate, stop, k0: 16)));
            Assert.Equal(64, SbrFrequencies.StopBand(rate, 13, k0: 16));
        }

        Assert.Equal(32, SbrFrequencies.StopBand(44100, 14, k0: 16));
        Assert.Equal(48, SbrFrequencies.StopBand(44100, 15, k0: 16));
    }

    /// <summary>
    /// The derived tables of 4.6.18.3.2.2 and .3 hold together for every header a 44.1 kHz stream can send: the high
    /// table is the master's from the crossover band, the low one every other border of it, the noise table a subset
    /// of the low one, and every table ends at k2.
    /// </summary>
    [Fact]
    public void DerivedTables_AreConsistent()
    {
        int made = 0;
        foreach (int start in Enumerable.Range(0, 16))
        {
            foreach (int stop in Enumerable.Range(0, 16))
            {
                foreach (int scale in Enumerable.Range(0, 4))
                {
                    foreach (bool alter in new[] { false, true })
                    {
                        foreach (int crossover in Enumerable.Range(0, 8))
                        {
                            var header = new SbrHeader(1, start, stop, crossover, scale, alter, 2, 2, 2, true, true);
                            SbrFrequencies f;
                            try
                            {
                                f = new SbrFrequencies(header, 44100);
                            }
                            catch (InvalidDataException)
                            {
                                continue; // a header the stream may not send
                            }

                            made++;
                            Assert.True(f.Master.Zip(f.Master.Skip(1)).All(p => p.First < p.Second));
                            Assert.Equal(f.Master[crossover..], f.High);
                            Assert.Equal(f.K2, f.High[^1]);
                            Assert.Equal(f.K2, f.Low[^1]);
                            Assert.Equal(f.K2, f.Noise[^1]);
                            Assert.Equal(f.High.Length / 2, f.Low.Length - 1);
                            Assert.Subset(f.High.ToHashSet(), f.Low.ToHashSet());
                            Assert.Subset(f.Low.ToHashSet(), f.Noise.ToHashSet());
                            Assert.InRange(f.NoiseBands, 1, 5);
                        }
                    }
                }
            }
        }

        Assert.True(made > 1000, $"Only {made} valid headers.");
    }

    [Fact]
    public void Config_SignalsSbrBackwardCompatibly()
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf("heaac-stereo-44k.mp4")))
        {
            return;
        }

        using var demuxer = Demuxer.Open(TestSamples.PathOf("heaac-stereo-44k.mp4"));
        var config = AudioSpecificConfig.Parse(demuxer.Audio!.CodecPrivate.Span);
        Assert.Equal((AudioSpecificConfig.AacLc, 22050, true, 44100, false), (config.ObjectType, config.SampleRate, config.SbrPresent, config.SbrSampleRate, config.PsPresent));
        var v2 = AudioSpecificConfig.Parse(Demuxer.Open(TestSamples.PathOf("heaacv2-stereo.mp4")).Audio!.CodecPrivate.Span);
        Assert.True(v2.SbrPresent && v2.PsPresent);
    }

    /// <summary>Hierarchical signalling: object type 5, the extension's rate, then the core's object type and config.</summary>
    [Fact]
    public void Config_SignalsSbrHierarchically()
    {
        // 00101 (SBR) 0111 (22.05 kHz) 0010 (stereo), 0100 (44.1 kHz), 00010 (AAC LC), then GASpecificConfig's 000.
        byte[] data = [0b0010_1011, 0b1001_0010, 0b0000_1000, 0b0000_0000];
        var config = AudioSpecificConfig.Parse(data);
        Assert.Equal((AudioSpecificConfig.AacLc, 2, true, 44100), (config.ObjectType, config.Channels, config.SbrPresent, config.SbrRate));
    }

    /// <summary>
    /// Every SBR payload of every frame is read to its end, past its last element and extension (the fill elements
    /// leave less than a byte unread), and the samples use what they are made for.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void ReadsEveryFrame(string sampleName)
    {
        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var audio = demuxer.Audio!;
        var decoder = new AacDecoder(audio) { SbrParseOnly = true };
        int packets = 0;
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == audio.StreamIndex))
        {
            decoder.Decode(packet.Data.Span);
            packets++;
        }

        var stats = decoder.Statistics;
        int elements = sampleName.Contains("5.1", StringComparison.Ordinal) ? 3 : 1; // 5.1: SCE, CPE, CPE (and LFE, without SBR)
        Assert.True(decoder.HasSbr);
        Assert.Equal(packets * elements, stats.SbrFrames);
        Assert.InRange(stats.SbrMostUnreadBits, 0, 7);
        Assert.True(stats.SbrHeaders > 0 && stats.SbrResets == elements, $"{stats.SbrResets} resets.");
        Assert.True(stats.SbrFrameClasses[SbrChannelData.FixFix] > 0);
        Assert.Equal(0, stats.SbrFrequencyScales & 0b11); // the FDK encoder uses bs_freq_scale 2 and 3 (0 and 1: a unit test)
        if (sampleName.Contains("stereo-44k", StringComparison.Ordinal))
        {
            Assert.True(stats.SbrFrameClasses.Skip(1).All(count => count > 0), $"Frame classes: {string.Join(", ", stats.SbrFrameClasses)}.");
            Assert.True(stats.SbrHarmonics > 0, "No sinusoids added.");
        }

        if (sampleName.Contains("low", StringComparison.Ordinal))
        {
            Assert.True(stats.SbrCoupledFrames > 0, "No coupled frames.");
        }

        Assert.Equal(sampleName.StartsWith("heaacv2", StringComparison.Ordinal), stats.SbrParametricStereoFrames > 0);
        if (sampleName.StartsWith("heaacv2", StringComparison.Ordinal))
        {
            // Parametric stereo in every frame, read to the end of its extension (less than a byte of padding left):
            // the FDK encoder's 20 bands with fine IID (iid_mode 4) and ICC in 20 bands (icc_mode 1), no IPD or OPD.
            Assert.Equal(packets, stats.PsFrames);
            Assert.InRange(stats.PsMostUnreadBits, 0, 7);
            Assert.True(stats.PsHeaders > 0 && stats.PsEnvelopes > 0 && stats.PsTimeDeltas > 0, $"{stats.PsHeaders} headers, {stats.PsEnvelopes} envelopes, {stats.PsTimeDeltas} time deltas.");
            Assert.Equal((1 << 4, 1 << 1, 0), (stats.PsIidModes, stats.PsIccModes, stats.PsExtensions));
        }
        else
        {
            Assert.Equal(0, stats.PsFrames);
        }
    }

    [Fact]
    public void PsCodebooks_ArePrefixFreeAndComplete()
    {
        (string[] Book, int Largest)[] books =
        [
            (PsTables.IidTime, 14), (PsTables.IidFrequency, 14), (PsTables.IidFineTime, 30), (PsTables.IidFineFrequency, 30),
            (PsTables.IccTime, 7), (PsTables.IccFrequency, 7),
        ];
        foreach (var (book, largest) in books)
        {
            Assert.Equal((2 * largest) + 1, book.Length);
            _ = new Vlc(book);
            Assert.Equal(1.0, book.Sum(code => Math.Pow(2, -code.Length)), precision: 12);
            Assert.Equal(book.Min(code => code.Length), book[largest].Length);
        }
    }
}

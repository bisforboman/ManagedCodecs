using System.Text.Json;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// Several sound tracks (docs/decisions/2026-11-16-audio-tracks.md): <see cref="IDemuxer.AudioStreams"/> against the
/// tracks ffprobe lists (name.audiotracks.json: channels, language, title, default disposition), <see cref="IDemuxer.Audio"/>
/// against the one FFmpeg's command line plays (name.audioplayed), and <see cref="IDemuxer.SelectAudio"/>. The samples
/// with several tracks are mp4-audio-tracks, mov-audio-tracks, mkv-audio-tracks and mkv-audio-nodefault; every other MP4,
/// QuickTime and Matroska sample checks the one track it has. Each track's packets are checked against FFmpeg's in the
/// MP4 and Matroska conformance tests.
/// </summary>
public class AudioTracksTests
{
    public static TheoryData<string> Samples() => [.. TestSamples.WithReference(".audiotracks.json")];

    [Theory]
    [MemberData(nameof(Samples))]
    public void ListsTheTracksFfprobeListsAndPlaysFfmpegsChoice(string sampleName)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(TestSamples.ReferenceOf(sampleName, ".audiotracks.json")));
        var expected = json.RootElement.TryGetProperty("streams", out var streams) ? streams.EnumerateArray().ToList() : [];
        string played = File.ReadAllText(TestSamples.ReferenceOf(sampleName, ".audioplayed")).Trim();

        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        Assert.Equal(expected.Count, demuxer.AudioStreams.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            var track = demuxer.AudioStreams[i];
            var tags = expected[i].TryGetProperty("tags", out var t) ? t : default;
            string? Tag(string name) => tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty(name, out var v) ? v.GetString() : null;
            Assert.Equal(
                (expected[i].GetProperty("index").GetInt32(), Tag("language"), Tag("title"), expected[i].GetProperty("disposition").GetProperty("default").GetInt32() == 1),
                (track.StreamIndex, track.Language, track.Title, track.IsDefault));

            // ffprobe's channels are its decoder's (HE-AAC's parametric stereo makes a mono track two), the demuxer's the
            // container's: compared on the samples made for the choice by channels.
            if (sampleName.Contains("audio-tracks", StringComparison.Ordinal) || sampleName.Contains("audio-nodefault", StringComparison.Ordinal))
            {
                Assert.Equal(expected[i].GetProperty("channels").GetInt32(), track.Channels);
            }
        }

        Assert.Equal(played.Length == 0 ? null : int.Parse(played, System.Globalization.CultureInfo.InvariantCulture), demuxer.Audio?.StreamIndex);
        if (demuxer.Audio is { } audio)
        {
            Assert.Contains(audio, demuxer.AudioStreams);
        }
    }

    /// <summary>
    /// Choosing a track: <see cref="IDemuxer.Audio"/> names it, a read gives its packets and the video's only (each
    /// numbered from 0), and a track no stream of the file is refused.
    /// </summary>
    [Theory]
    [InlineData("mp4-audio-tracks.mp4", 2, 1)]
    [InlineData("mkv-audio-tracks.mkv", 1, 2)]
    [InlineData("mkv-audio-nodefault.mkv", 2, 1)]
    public void GivesTheChosenTracksPackets(string sampleName, int played, int other)
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf(sampleName)))
        {
            return;
        }

        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        Assert.Equal(played, demuxer.Audio!.StreamIndex);
        var first = demuxer.ReadPackets().ToList();
        Assert.Equal([0, played], first.Select(p => p.StreamIndex).Distinct().Order());

        demuxer.SelectAudio(other);
        Assert.Equal(other, demuxer.Audio!.StreamIndex);
        var second = demuxer.ReadPackets().ToList();
        Assert.Equal([0, other], second.Select(p => p.StreamIndex).Distinct().Order());
        Assert.Equal(Enumerable.Range(0, second.Count(p => p.StreamIndex == other)).Select(i => (long)i), second.Where(p => p.StreamIndex == other).Select(p => p.Sequence));
        Assert.Equal(first.Where(p => p.StreamIndex == 0).Select(p => p.Pts), second.Where(p => p.StreamIndex == 0).Select(p => p.Pts));

        Assert.Throws<ArgumentOutOfRangeException>(() => demuxer.SelectAudio(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => demuxer.SelectAudio(3));
        Assert.Equal(other, demuxer.Audio!.StreamIndex);
    }

    /// <summary>A track chosen after a <see cref="IDemuxer.Seek"/> starts at the time sought, as the track chosen before it did.</summary>
    [Theory]
    [InlineData("mp4-audio-tracks.mp4", 1)]
    [InlineData("mkv-audio-tracks.mkv", 2)]
    public void StartsATrackChosenAfterASeekAtTheTimeSought(string sampleName, int other)
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf(sampleName)))
        {
            return;
        }

        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var video = demuxer.Video!;
        long middle = demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex).Select(p => p.Pts).Order().ElementAt((int)video.FrameCount / 2);
        demuxer.Seek(middle, video.StreamIndex);
        var before = demuxer.ReadPackets().First(p => p.StreamIndex == demuxer.Audio!.StreamIndex);

        demuxer.SelectAudio(other);
        var after = demuxer.ReadPackets().First(p => p.StreamIndex == other);
        double Seconds(Packet p, Rational timeBase) => p.Pts * timeBase.ToDouble();
        var timeBase = demuxer.AudioStreams.First(a => a.StreamIndex == other).TimeBase;

        // Both tracks start at a keyframe (every audio packet is one) at or just before the video's start.
        Assert.True(after.Sequence > 0, "The chosen track started from the beginning, not where the seek put the other.");
        Assert.True(Math.Abs(Seconds(after, timeBase) - Seconds(before, demuxer.AudioStreams.First(a => a.StreamIndex == before.StreamIndex).TimeBase)) < 0.05);
    }

    /// <summary>
    /// Matroska's elements a track leaves out take the specification's defaults, as FFmpeg 6.1 reads them (measured by
    /// patching mkv-audio-tracks): no Language is "eng", no FlagDefault is default; "und" is no language.
    /// </summary>
    [Fact]
    public void ReadsMatroskasMissingElementsAsTheirDefaults()
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf("mkv-audio-tracks.mkv")))
        {
            return;
        }

        byte[] file = File.ReadAllBytes(TestSamples.PathOf("mkv-audio-tracks.mkv"));

        // The second track's Language (22 B5 9C, "swe") and FlagDefault (88, 0) made Void elements (EC) of their size.
        int language = file.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x22, 0xB5, 0x9C, 0x83, (byte)'s', (byte)'w', (byte)'e']);
        int flag = file.AsSpan(language).IndexOf((ReadOnlySpan<byte>)[0x88, 0x81, 0x00]) + language;
        Assert.True(language > 0 && flag > language);
        byte[] patched = [.. file];
        patched[language] = 0xEC;
        patched[language + 1] = 0x85;
        patched.AsSpan(language + 2, 5).Clear();
        patched[flag] = 0xEC;
        patched[flag + 1] = 0x81;
        patched[flag + 2] = 0;
        using (var demuxer = new MatroskaDemuxer(new MemoryStream(patched)))
        {
            Assert.Equal(("eng", true), (demuxer.AudioStreams[1].Language, demuxer.AudioStreams[1].IsDefault));

            // Two tracks marked default: the one with more channels plays.
            Assert.Equal(2, demuxer.Audio!.StreamIndex);
        }

        "und"u8.CopyTo(file.AsSpan(language + 4));
        using var und = new MatroskaDemuxer(new MemoryStream(file));
        Assert.Null(und.AudioStreams[1].Language);
    }

    /// <summary>
    /// MP4's 'mdhd' language as FFmpeg 6.1 reads it, each value measured by patching it into a QuickTime file and reading
    /// ffprobe's answer: Macintosh codes below 0x400 by FFmpeg's table (34, 58 and 95 to 127 unnamed), 0x7FFF none,
    /// else three packed letters with the top bit ignored (0x400 is "a``").
    /// </summary>
    [Theory]
    [InlineData(0, "eng")]
    [InlineData(5, "sve")]
    [InlineData(11, "jpn")]
    [InlineData(18, "hr")]
    [InlineData(33, "chi")]
    [InlineData(34, null)]
    [InlineData(58, null)]
    [InlineData(94, "epo")]
    [InlineData(95, null)]
    [InlineData(127, null)]
    [InlineData(128, "wel")]
    [InlineData(138, "jav")]
    [InlineData(139, null)]
    [InlineData(0x3FF, null)]
    [InlineData(0x400, "a``")]
    [InlineData(0x15C7, "eng")]
    [InlineData(0x55C4, "und")]
    [InlineData(0x95C7, "eng")]
    [InlineData(0x7FFF, null)]
    public void ReadsMp4LanguagesAsFfmpeg(int code, string? language) =>
        Assert.Equal(language, Mp4Demuxer.LanguageOf((ushort)code));

    /// <summary>
    /// FFmpeg's command line's choice, measured with FFmpeg 6.1 on Matroska and MP4 files with a stereo and a 5.1 track
    /// in each order and every combination of default flags: of the tracks marked default (or of all, where none is), the
    /// one with the most channels, the first of equals.
    /// </summary>
    [Theory]
    [InlineData(true, 2, true, 6, 1)]
    [InlineData(true, 6, true, 2, 0)]
    [InlineData(false, 2, false, 6, 1)]
    [InlineData(false, 6, false, 2, 0)]
    [InlineData(true, 2, false, 6, 0)]
    [InlineData(false, 2, true, 6, 1)]
    [InlineData(false, 6, true, 2, 1)]
    [InlineData(true, 2, true, 2, 0)]
    [InlineData(false, 2, false, 2, 0)]
    public void ChoosesTheTrackFfmpegsCommandLinePlays(bool firstDefault, int firstChannels, bool secondDefault, int secondChannels, int chosen)
    {
        AudioStreamInfo Track(bool isDefault, int channels, int index) => new("aac", 48000, channels, 1, new Rational(1, 48000), ReadOnlyMemory<byte>.Empty, index) { IsDefault = isDefault };
        Assert.Equal(chosen, AudioTracks.DefaultOf([Track(firstDefault, firstChannels, 1), Track(secondDefault, secondChannels, 2)]));
        Assert.Equal(-1, AudioTracks.DefaultOf([]));
    }

    /// <summary>A demuxer of one sound track at most (here ADTS) lists it alone and accepts it alone, by IDemuxer's defaults.</summary>
    [Fact]
    public void ListsTheOneTrackOfADemuxerWithoutChoices()
    {
        var adts = TestSamples.WithReference(".packets.csv").FirstOrDefault(name => name.EndsWith(".aac", StringComparison.Ordinal));
        if (adts is null)
        {
            return;
        }

        using IDemuxer demuxer = Demuxer.Open(TestSamples.PathOf(adts));
        var audio = demuxer.Audio!;
        Assert.Equal([audio], demuxer.AudioStreams);
        demuxer.SelectAudio(audio.StreamIndex);
        Assert.Throws<ArgumentOutOfRangeException>(() => demuxer.SelectAudio(audio.StreamIndex + 1));
    }
}

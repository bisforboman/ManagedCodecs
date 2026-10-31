namespace ManagedCodecs.Tests;

/// <summary>
/// Reads the colour properties and sample aspect ratios <c>ffprobe</c> lists for a sample's decoded frames (name.color;
/// tools/make-samples.sh), one line a frame in output order ("sample_aspect_ratio=1:1|color_range=tv|..."), for
/// <see cref="VideoFrame.ColorRange"/>, <see cref="VideoFrame.ColorPrimaries"/>, <see cref="VideoFrame.ColorTransfer"/>,
/// <see cref="VideoFrame.ColorMatrix"/>, <see cref="VideoFrame.ChromaLocation"/> and
/// <see cref="VideoFrame.SampleAspectRatio"/>.
/// </summary>
public sealed class ColorReferences
{
    private static readonly Dictionary<string, int[]> Primaries = Names(
        ["reserved", "bt709", "unknown", "reserved", "bt470m", "bt470bg", "smpte170m", "smpte240m", "film", "bt2020", "smpte428", "smpte431", "smpte432"],
        ("ebu3213", 22));

    private static readonly Dictionary<string, int[]> Transfers = Names(
        ["reserved", "bt709", "unknown", "reserved", "bt470m", "bt470bg", "smpte170m", "smpte240m", "linear", "log100", "log316",
            "iec61966-2-4", "bt1361e", "iec61966-2-1", "bt2020-10", "bt2020-12", "smpte2084", "smpte428", "arib-std-b67"]);

    private static readonly Dictionary<string, int[]> Matrices = Names(
        ["gbr", "bt709", "unknown", "reserved", "fcc", "bt470bg", "smpte170m", "smpte240m", "ycgco", "bt2020nc", "bt2020c", "smpte2085",
            "chroma-derived-nc", "chroma-derived-c", "ictcp"]);

    private static readonly string[] Ranges = ["unknown", "tv", "pc"];

    private static readonly string[] Locations = ["unspecified", "left", "center", "topleft", "top", "bottomleft", "bottom"];

    private ColorReferences(List<Dictionary<string, string>> frames) => Frames = frames;

    /// <summary>Each frame's fields by ffprobe's key.</summary>
    public List<Dictionary<string, string>> Frames { get; }

    /// <summary>The sample's reference, or null for a sample without one (a stream FFmpeg does not decode).</summary>
    public static ColorReferences? Read(string sampleName)
    {
        var path = TestSamples.ReferenceOf(sampleName, ".color");
        if (!File.Exists(path))
        {
            return null;
        }

        var frames = File.ReadLines(path)
            .Where(line => line.Length > 0)
            .Select(line => line.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Split('=', 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0], kv => kv[1]))
            .ToList();
        return new ColorReferences(frames);
    }

    /// <summary>Asserts that a frame's colour properties and aspect ratio are those of the reference's frame <paramref name="index"/>.</summary>
    public void Check(int index, VideoFrame frame)
    {
        var expected = Frames[index];
        string actual = Describe(frame);
        Assert.True(
            Ranges[(int)frame.ColorRange] == expected["color_range"]
                && Primaries[expected["color_primaries"]].Contains((int)frame.ColorPrimaries)
                && Transfers[expected["color_transfer"]].Contains((int)frame.ColorTransfer)
                && Matrices[expected["color_space"]].Contains((int)frame.ColorMatrix)
                && Locations[(int)frame.ChromaLocation] == expected["chroma_location"]
                && SameRatio(frame.SampleAspectRatio, expected["sample_aspect_ratio"]),
            $"Frame {index}: {actual}; FFmpeg's {string.Join(", ", expected.Select(kv => $"{kv.Key}={kv.Value}"))}.");
    }

    /// <summary>ffprobe prints a ratio as given ("64:45"), and N/A for an unknown one (0:1).</summary>
    private static bool SameRatio(Rational ratio, string printed) =>
        printed == "N/A" ? ratio.Numerator == 0 : printed == $"{ratio.Numerator}:{ratio.Denominator}";

    private static string Describe(VideoFrame f) =>
        $"range {f.ColorRange}, primaries {f.ColorPrimaries}, transfer {f.ColorTransfer}, matrix {f.ColorMatrix}, chroma {f.ChromaLocation}, SAR {f.SampleAspectRatio.Numerator}:{f.SampleAspectRatio.Denominator}";

    /// <summary>ffprobe's names by value (index), each mapped to the values it stands for ("reserved" for 0 and 3).</summary>
    private static Dictionary<string, int[]> Names(string[] byValue, params (string Name, int Value)[] more) =>
        byValue.Select((name, value) => (name, value)).Concat(more)
            .GroupBy(e => e.Item1, e => e.Item2)
            .ToDictionary(g => g.Key, g => g.ToArray());
}

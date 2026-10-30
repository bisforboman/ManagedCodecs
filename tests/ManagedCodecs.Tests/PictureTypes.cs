namespace ManagedCodecs.Tests;

/// <summary>
/// Reads the picture types <c>ffprobe -show_frames</c> lists for a sample's decoded frames (name.picttypes, one
/// <c>pict_type</c> a line in output order; tools/make-samples.sh), for <see cref="VideoFrame.PictureType"/>.
/// </summary>
public static class PictureTypes
{
    /// <summary>The sample's reference, or null for a sample without one (a stream FFmpeg does not decode).</summary>
    public static List<PictureType>? Read(string sampleName)
    {
        var path = TestSamples.ReferenceOf(sampleName, ".picttypes");
        return File.Exists(path) ? [.. File.ReadLines(path).Where(line => line.Length > 0).Select(Parse)] : null;
    }

    /// <summary>A type as FFmpeg's <c>av_get_picture_type_char</c> writes it.</summary>
    public static PictureType Parse(string type) => type switch
    {
        "I" => PictureType.I,
        "P" => PictureType.P,
        "B" => PictureType.B,
        "S" => PictureType.S,
        "i" => PictureType.SI,
        "p" => PictureType.SP,
        "b" => PictureType.BI,
        "?" => PictureType.None,
        _ => throw new FormatException($"Unexpected pict_type: {type}"),
    };
}

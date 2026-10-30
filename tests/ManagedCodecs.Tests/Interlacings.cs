namespace ManagedCodecs.Tests;

/// <summary>
/// Reads the interlacing <c>ffprobe</c> lists for a sample (name.interlacing; tools/make-samples.sh): each decoded frame's
/// <c>interlaced_frame</c> and <c>top_field_first</c> in output order ("frame,1,0"), and the stream's
/// <c>field_order</c> ("stream,tt"), for <see cref="VideoFrame.IsInterlaced"/>, <see cref="VideoFrame.IsTopFieldFirst"/>
/// and <c>H264Decoder.FieldOrder</c>.
/// </summary>
public sealed record Interlacings(List<(bool Interlaced, bool TopFieldFirst)> Frames, FieldOrder StreamOrder)
{
    /// <summary>The sample's reference, or null for a sample without one (a stream FFmpeg does not decode).</summary>
    public static Interlacings? Read(string sampleName)
    {
        var path = TestSamples.ReferenceOf(sampleName, ".interlacing");
        if (!File.Exists(path))
        {
            return null;
        }

        var frames = new List<(bool, bool)>();
        var order = FieldOrder.Unknown;
        foreach (var fields in File.ReadLines(path).Select(line => line.Split(',')))
        {
            if (fields[0] == "frame")
            {
                frames.Add((fields[1] == "1", fields[2] == "1"));
            }
            else if (fields[0] == "stream")
            {
                order = ParseOrder(fields[1]);
            }
        }

        return new Interlacings(frames, order);
    }

    /// <summary>A field order as <c>ffprobe</c> prints it.</summary>
    public static FieldOrder ParseOrder(string order) => order switch
    {
        "unknown" => FieldOrder.Unknown,
        "progressive" => FieldOrder.Progressive,
        "tt" => FieldOrder.TT,
        "bb" => FieldOrder.BB,
        "tb" => FieldOrder.TB,
        "bt" => FieldOrder.BT,
        _ => throw new FormatException($"Unexpected field_order: {order}"),
    };

    /// <summary>Asserts that a frame's flags are those of the reference's frame <paramref name="index"/>.</summary>
    public void Check(int index, VideoFrame frame)
    {
        var (interlaced, topFieldFirst) = Frames[index];
        Assert.True(
            interlaced == frame.IsInterlaced && topFieldFirst == frame.IsTopFieldFirst,
            $"Frame {index}: interlaced {frame.IsInterlaced}, top field first {frame.IsTopFieldFirst}; FFmpeg's {interlaced}, {topFieldFirst}.");
    }
}

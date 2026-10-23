using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests;

/// <summary>Finds the generated media in samples/ (see samples/README.md).</summary>
public static class TestSamples
{
    public static string? Directory { get; } = Find();

    public static string PathOf(string name) => Path.Combine(Directory!, name);

    /// <summary>Container files (AVI, MP4 and raw AAC) that have a reference file with the given suffix.</summary>
    public static IEnumerable<string> WithReference(string suffix)
    {
        if (Directory is null)
        {
            yield break;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory).Order())
        {
            if (Path.GetExtension(file) is ".avi" or ".mp4" or ".aac" && File.Exists(ReferenceOf(file, suffix)))
            {
                yield return Path.GetFileName(file);
            }
        }
    }

    /// <summary>samples/name.avi with suffix ".framemd5" is samples/name.framemd5.</summary>
    public static string ReferenceOf(string file, string suffix) =>
        Path.Combine(Path.GetDirectoryName(PathOf(file))!, Path.GetFileNameWithoutExtension(file) + suffix);

    /// <summary>The video codec of a sample, or null for audio-only files.</summary>
    public static string? CodecOf(string name)
    {
        using var demuxer = Demuxer.Open(PathOf(name));
        return demuxer.Video?.Codec;
    }

    private static string? Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples");
            if (System.IO.Directory.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "ManagedCodecs.slnx")))
            {
                return candidate;
            }
        }

        return null;
    }
}


namespace ManagedCodecs.Containers;

/// <summary>Opens a container by looking at its first bytes rather than its file extension.</summary>
public static class Demuxer
{
    /// <summary>Opens the file at <paramref name="path"/> as <see cref="Open(Stream, bool)"/> does; the demuxer disposes it with itself.</summary>
    public static IDemuxer Open(string path) => Open(File.OpenRead(path));

    /// <summary>An <see cref="AviDemuxer"/>, <see cref="Mp4Demuxer"/> or <see cref="AdtsDemuxer"/>, by the stream's first bytes.</summary>
    /// <param name="stream">A seekable stream holding the file; it is read from its start.</param>
    /// <param name="leaveOpen">Whether disposing the demuxer (or a failure here) leaves <paramref name="stream"/> open.</param>
    /// <exception cref="NotSupportedException">The format is none of these.</exception>
    public static IDemuxer Open(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> head = stackalloc byte[12];
        stream.Position = 0;
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = 0;

        try
        {
            if (read >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("AVI "u8))
            {
                return new AviDemuxer(stream, leaveOpen);
            }

            // MP4 and QuickTime files start with a box; these are the usual first ones.
            if (read >= 8 && IsTopLevelBox(head[4..8]))
            {
                return new Mp4Demuxer(stream, leaveOpen);
            }

            // Raw AAC: an ADTS frame (after any ID3v2 tags) followed by another one.
            if (AdtsDemuxer.Probe(stream))
            {
                return new AdtsDemuxer(stream, leaveOpen);
            }
        }
        catch when (!leaveOpen)
        {
            stream.Dispose();
            throw;
        }

        if (!leaveOpen)
        {
            stream.Dispose();
        }

        throw new NotSupportedException("Unrecognized container format.");
    }

    private static bool IsTopLevelBox(ReadOnlySpan<byte> type) =>
        type.SequenceEqual("ftyp"u8) || type.SequenceEqual("moov"u8) || type.SequenceEqual("mdat"u8)
        || type.SequenceEqual("free"u8) || type.SequenceEqual("skip"u8) || type.SequenceEqual("wide"u8);
}

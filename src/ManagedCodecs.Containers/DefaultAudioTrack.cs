namespace ManagedCodecs.Containers;

/// <summary>
/// Which of a file's audio streams <see cref="IDemuxer.Audio"/> is until the caller chooses
/// (<see cref="IDemuxer.SelectAudio"/>), for the demuxers whose containers mark tracks default (MP4, Matroska).
/// </summary>
internal static class DefaultAudioTrack
{
    /// <summary>
    /// The position in <paramref name="streams"/> (file order) of the stream FFmpeg's command line plays, as measured
    /// with FFmpeg 6.1 (docs/decisions/2026-11-16-audio-tracks.md): of those marked default (or, where none is, of them
    /// all) the one with the most channels, the first of equals. -1 for no streams.
    /// </summary>
    public static int Of(IReadOnlyList<AudioStreamInfo> streams)
    {
        int chosen = -1;
        for (int i = 0; i < streams.Count; i++)
        {
            if (chosen < 0 || Better(streams[i], streams[chosen]))
            {
                chosen = i;
            }
        }

        return chosen;

        static bool Better(AudioStreamInfo a, AudioStreamInfo b) =>
            a.IsDefault != b.IsDefault ? a.IsDefault : a.Channels > b.Channels;
    }
}

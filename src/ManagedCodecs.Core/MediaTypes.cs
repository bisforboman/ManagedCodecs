namespace ManagedCodecs;

/// <summary>Which kinds of track a demuxer reads.</summary>
[Flags]
public enum MediaTypes
{
    /// <summary>No track.</summary>
    None = 0,

    /// <summary>The video track.</summary>
    Video = 1,

    /// <summary>The audio track.</summary>
    Audio = 2,

    /// <summary>The video and the audio track.</summary>
    All = Video | Audio,
}

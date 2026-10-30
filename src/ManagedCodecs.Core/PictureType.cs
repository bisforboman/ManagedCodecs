namespace ManagedCodecs;

/// <summary>
/// How a decoded frame was coded, as FFmpeg's <c>AVPictureType</c> (the <c>pict_type</c> that <c>ffprobe -show_frames</c>
/// prints), with the same values.
/// </summary>
public enum PictureType
{
    /// <summary>Not known: a frame no decoder made.</summary>
    None = 0,

    /// <summary>Intra: predicted from no other picture (an H.264 I slice, IDR or not; every MJPEG frame).</summary>
    I = 1,

    /// <summary>Predicted from earlier decoded pictures, one reference a block (an H.264 P slice).</summary>
    P = 2,

    /// <summary>Bi-predicted, from up to two references a block (an H.264 B slice).</summary>
    B = 3,

    /// <summary>MPEG-4 part 2's sprite (global motion compensation) picture; no decoder here makes one.</summary>
    S = 4,

    /// <summary>Switching intra (an H.264 SI slice, Extended profile).</summary>
    SI = 5,

    /// <summary>Switching predicted (an H.264 SP slice, Extended profile).</summary>
    SP = 6,

    /// <summary>VC-1's BI picture (a B picture coded as intra); no decoder here makes one.</summary>
    BI = 7,
}

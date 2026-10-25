namespace ManagedCodecs.H264;

/// <summary>
/// Which pictures of a slice's reference lists were short-term and which long-term references when the lists were
/// built (frames and their fields). Decoding the slice reads the marking here: with frame threading
/// (H264Decoder.Threads) the pictures after it are marked while it is still being decoded.
/// </summary>
internal sealed class MarkSnapshot
{
    private readonly HashSet<Picture> _shortTerm = [];
    private readonly HashSet<Picture> _longTerm = [];

    public MarkSnapshot(IEnumerable<Picture> pictures)
    {
        foreach (var picture in pictures)
        {
            var frame = picture.Frame;
            foreach (var view in (ReadOnlySpan<Picture>)[frame, frame.Field(0), frame.Field(1)])
            {
                if (view.IsShortTermReference)
                {
                    _shortTerm.Add(view);
                }

                if (view.IsLongTermReference)
                {
                    _longTerm.Add(view);
                }
            }
        }
    }

    // A frame's colour plane (separate colour planes), and a field of one, is marked as the frame or field is.
    public bool IsShortTerm(Picture picture) => _shortTerm.Contains(Marked(picture));

    public bool IsLongTerm(Picture picture) => _longTerm.Count > 0 && _longTerm.Contains(Marked(picture));

    private static Picture Marked(Picture picture) => picture.Parity < 0 ? picture.Frame : picture.Frame.Field(picture.Parity);
}

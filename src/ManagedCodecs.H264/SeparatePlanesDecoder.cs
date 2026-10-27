namespace ManagedCodecs.H264;

/// <summary>
/// Decodes a picture with separate colour planes (separate_colour_plane_flag, 7.4.2.1.1): each colour plane is coded as
/// a monochrome picture of its own (its own macroblocks, motion and slices, which colour_plane_id names), so one
/// <see cref="PictureDecoder{TPixel}"/> a plane decodes it into that plane's view of the frame
/// (<see cref="Picture.ColourPlane"/>), predicting from the same plane of each reference.
/// </summary>
/// <remarks>
/// The frame is reported done (to pictures that predict from it) once, when all three planes are deblocked: a plane's
/// rows are not the frame's.
/// </remarks>
internal sealed class SeparatePlanesDecoder : IPictureDecoder
{
    private readonly IPictureDecoder[] _planes;

    public SeparatePlanesDecoder(IPictureDecoder[] planes)
    {
        _planes = planes;
    }

    /// <inheritdoc/>
    public SequenceParameterSet Sps => _planes[0].Sps;

    /// <inheritdoc/>
    public Picture Picture { get; private set; } = null!;

    /// <inheritdoc/>
    public bool IsComplete => _planes.All(p => p.IsComplete);

    /// <inheritdoc/>
    public int RowDelay
    {
        get => _planes[0].RowDelay;
        set => Array.ForEach(_planes, p => p.RowDelay = value);
    }

    /// <inheritdoc/>
    public bool ConcealErrors
    {
        get => _planes[0].ConcealErrors;
        set => Array.ForEach(_planes, p => p.ConcealErrors = value);
    }

    /// <inheritdoc/>
    public int ConcealedMbCount => _planes.Sum(p => p.ConcealedMbCount);

    /// <inheritdoc/>
    public void Begin(Picture picture, SequenceParameterSet sps, int fieldParity = -1, bool secondField = false)
    {
        Picture = picture;
        for (int plane = 0; plane < 3; plane++)
        {
            _planes[plane].Begin(picture.ColourPlane(plane), sps, fieldParity, secondField);
        }
    }

    /// <inheritdoc/>
    public void DecodeSlice(SliceHeader header, PictureParameterSet pps, BitReader r, IReadOnlyList<Picture> refList0, IReadOnlyList<Picture> refList1, MarkSnapshot marks)
    {
        int plane = header.ColourPlaneId;
        _planes[plane].DecodeSlice(header, pps, r, PlaneOf(refList0, plane), PlaneOf(refList1, plane), marks);
    }

    /// <inheritdoc/>
    public void Deblock() => Array.ForEach(_planes, p => p.Deblock());

    /// <inheritdoc/>
    public void Conceal(Picture? fallback)
    {
        for (int plane = 0; plane < 3; plane++)
        {
            if (!_planes[plane].IsComplete)
            {
                _planes[plane].Conceal(fallback?.ColourPlane(plane));
            }
        }
    }

    // A reference frame's plane, or that plane's field of a reference field.
    private static Picture[] PlaneOf(IReadOnlyList<Picture> list, int plane) =>
        [.. list.Select(p => p.Parity < 0 ? p.ColourPlane(plane) : p.ColourPlane(plane).Field(p.Parity))];
}

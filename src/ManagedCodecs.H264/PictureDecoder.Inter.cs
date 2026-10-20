namespace ManagedCodecs.H264;

/// <summary>Inter prediction for P slices (H.264 clause 8.4).</summary>
internal sealed partial class PictureDecoder
{
    private void InitInter()
    {
    }

    /// <summary>The deblocking filter (8.7); not implemented yet.</summary>
    public void Deblock()
    {
    }

    private void ClearInter(int mbAddr)
    {
    }

    private void DecodeSkip(SliceState slice, int mbAddr) =>
        throw new NotSupportedException("P slices are not implemented yet.");

    private void DecodeInterMacroblock(SliceState slice, BitReader r, int mbAddr, int mbType) =>
        throw new NotSupportedException("P slices are not implemented yet.");
}

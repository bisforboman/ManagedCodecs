namespace ManagedCodecs.H264;

/// <summary>Inter prediction for P slices (H.264 clause 8.4).</summary>
internal sealed partial class PictureDecoder
{
    // Per luma 4x4 block: motion vector in quarter samples.
    private short[] _mvX = [];
    private short[] _mvY = [];

    // Per 8x8 block: reference index into the slice's list (-1 = none) and the picture itself,
    // which is what the deblocking filter compares.
    private sbyte[] _refIdx = [];
    private Picture?[] _refPic = [];

    private void InitInter()
    {
        _mvX = new short[_mbCount * 16];
        _mvY = new short[_mbCount * 16];
        _refIdx = new sbyte[_mbCount * 4];
        _refPic = new Picture?[_mbCount * 4];
        Array.Fill(_refIdx, (sbyte)-1);
    }

    /// <summary>Resets the motion data of an intra macroblock.</summary>
    private void ClearInter(int mbAddr)
    {
        _mvX.AsSpan(mbAddr * 16, 16).Clear();
        _mvY.AsSpan(mbAddr * 16, 16).Clear();
        _refIdx.AsSpan(mbAddr * 4, 4).Fill(-1);
        _refPic.AsSpan(mbAddr * 4, 4).Clear();
    }

    private void DecodeSkip(SliceState slice, int mbAddr) =>
        throw new NotSupportedException("P slices are not implemented yet.");

    private void DecodeInterMacroblock(SliceState slice, BitReader r, int mbAddr, int mbType) =>
        throw new NotSupportedException("P slices are not implemented yet.");
}

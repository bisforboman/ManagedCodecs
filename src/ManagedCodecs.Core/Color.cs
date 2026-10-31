namespace ManagedCodecs;

/// <summary>
/// Which values a frame's samples take (H.264's video_full_range_flag, JPEG's full range): FFmpeg's <c>AVColorRange</c>,
/// the same values, as <c>ffprobe -show_frames</c> prints them in <c>color_range</c>.
/// </summary>
public enum ColorRange
{
    /// <summary>Not said (<c>unknown</c>).</summary>
    Unspecified = 0,

    /// <summary>Limited ("studio", <c>tv</c>): luma 16 to 235 and chroma 16 to 240, scaled by the bit depth.</summary>
    Limited = 1,

    /// <summary>Full (<c>pc</c>): every value of the bit depth, as JPEG's.</summary>
    Full = 2,
}

/// <summary>
/// The colour primaries of the RGB a frame converts to (ITU-T H.273 ColourPrimaries, H.264's colour_primaries): the same
/// values as FFmpeg's <c>AVColorPrimaries</c>, which <c>ffprobe -show_frames</c> prints in <c>color_primaries</c>.
/// </summary>
public enum ColorPrimaries
{
    /// <summary>Reserved (H.273's 0).</summary>
    Reserved0 = 0,

    /// <summary>ITU-R BT.709 (HD, sRGB's primaries).</summary>
    Bt709 = 1,

    /// <summary>Not said (<c>unknown</c>).</summary>
    Unspecified = 2,

    /// <summary>Reserved (H.273's 3).</summary>
    Reserved = 3,

    /// <summary>ITU-R BT.470 System M (NTSC 1953).</summary>
    Bt470M = 4,

    /// <summary>ITU-R BT.470 System B, G and BT.601 625 lines (PAL, SECAM).</summary>
    Bt470BG = 5,

    /// <summary>SMPTE 170M and BT.601 525 lines (NTSC).</summary>
    Smpte170M = 6,

    /// <summary>SMPTE 240M.</summary>
    Smpte240M = 7,

    /// <summary>Generic film (colour filters with illuminant C).</summary>
    Film = 8,

    /// <summary>ITU-R BT.2020 and BT.2100 (UHD, HDR).</summary>
    Bt2020 = 9,

    /// <summary>SMPTE ST 428-1 (CIE 1931 XYZ).</summary>
    Smpte428 = 10,

    /// <summary>SMPTE RP 431-2 (DCI-P3).</summary>
    Smpte431 = 11,

    /// <summary>SMPTE EG 432-1 (Display P3).</summary>
    Smpte432 = 12,

    /// <summary>EBU Tech. 3213-E.</summary>
    Ebu3213 = 22,
}

/// <summary>
/// The transfer characteristics of a frame's samples (ITU-T H.273 TransferCharacteristics, H.264's
/// transfer_characteristics): the same values as FFmpeg's <c>AVColorTransferCharacteristic</c>, which
/// <c>ffprobe -show_frames</c> prints in <c>color_transfer</c>.
/// </summary>
public enum ColorTransfer
{
    /// <summary>Reserved (H.273's 0).</summary>
    Reserved0 = 0,

    /// <summary>ITU-R BT.709 (also BT.601, BT.2020 at 10 and 12 bits by the same curve).</summary>
    Bt709 = 1,

    /// <summary>Not said (<c>unknown</c>).</summary>
    Unspecified = 2,

    /// <summary>Reserved (H.273's 3).</summary>
    Reserved = 3,

    /// <summary>Gamma 2.2 (BT.470 System M, <c>bt470m</c>).</summary>
    Gamma22 = 4,

    /// <summary>Gamma 2.8 (BT.470 System B, G, <c>bt470bg</c>).</summary>
    Gamma28 = 5,

    /// <summary>SMPTE 170M (the BT.709 curve).</summary>
    Smpte170M = 6,

    /// <summary>SMPTE 240M.</summary>
    Smpte240M = 7,

    /// <summary>Linear.</summary>
    Linear = 8,

    /// <summary>Logarithmic, 100:1 range (<c>log100</c>).</summary>
    Log = 9,

    /// <summary>Logarithmic, 100 sqrt(10):1 range (<c>log316</c>).</summary>
    LogSqrt = 10,

    /// <summary>IEC 61966-2-4 (xvYCC).</summary>
    Xvycc = 11,

    /// <summary>ITU-R BT.1361 extended colour gamut.</summary>
    Bt1361 = 12,

    /// <summary>IEC 61966-2-1 (sRGB, sYCC).</summary>
    Srgb = 13,

    /// <summary>ITU-R BT.2020 for 10-bit systems (<c>bt2020-10</c>).</summary>
    Bt2020TenBit = 14,

    /// <summary>ITU-R BT.2020 for 12-bit systems (<c>bt2020-12</c>).</summary>
    Bt2020TwelveBit = 15,

    /// <summary>SMPTE ST 2084, perceptual quantization (PQ, HDR10 and BT.2100 PQ).</summary>
    Smpte2084 = 16,

    /// <summary>SMPTE ST 428-1.</summary>
    Smpte428 = 17,

    /// <summary>ARIB STD-B67, hybrid log-gamma (HLG, BT.2100 HLG).</summary>
    AribStdB67 = 18,
}

/// <summary>
/// The matrix that turns a frame's Y, Cb and Cr into R, G and B (ITU-T H.273 MatrixCoefficients, H.264's
/// matrix_coefficients): the same values as FFmpeg's <c>AVColorSpace</c>, which <c>ffprobe -show_frames</c> prints in
/// <c>color_space</c>.
/// </summary>
public enum ColorMatrix
{
    /// <summary>None: the planes are G, B and R (<c>gbr</c>; IEC 61966-2-1).</summary>
    Identity = 0,

    /// <summary>ITU-R BT.709 (HD).</summary>
    Bt709 = 1,

    /// <summary>Not said (<c>unknown</c>).</summary>
    Unspecified = 2,

    /// <summary>Reserved (H.273's 3).</summary>
    Reserved = 3,

    /// <summary>FCC Title 47.</summary>
    Fcc = 4,

    /// <summary>ITU-R BT.470 System B, G and BT.601 625 lines (PAL; JPEG's).</summary>
    Bt470BG = 5,

    /// <summary>SMPTE 170M and BT.601 525 lines (NTSC), the same matrix as <see cref="Bt470BG"/>.</summary>
    Smpte170M = 6,

    /// <summary>SMPTE 240M.</summary>
    Smpte240M = 7,

    /// <summary>YCgCo.</summary>
    YCgCo = 8,

    /// <summary>ITU-R BT.2020 non-constant luminance (<c>bt2020nc</c>).</summary>
    Bt2020Ncl = 9,

    /// <summary>ITU-R BT.2020 constant luminance (<c>bt2020c</c>).</summary>
    Bt2020Cl = 10,

    /// <summary>SMPTE ST 2085 (Y'D'zD'x).</summary>
    Smpte2085 = 11,

    /// <summary>Chromaticity-derived non-constant luminance.</summary>
    ChromaDerivedNcl = 12,

    /// <summary>Chromaticity-derived constant luminance.</summary>
    ChromaDerivedCl = 13,

    /// <summary>ITU-R BT.2100 ICtCp.</summary>
    ICtCp = 14,
}

/// <summary>
/// Where a subsampled frame's chroma samples sit against its luma samples (H.264's chroma_sample_loc_type plus one,
/// Figure E-1): FFmpeg's <c>AVChromaLocation</c>, the same values, as <c>ffprobe -show_frames</c> prints them in
/// <c>chroma_location</c>.
/// </summary>
public enum ChromaLocation
{
    /// <summary>Not said (<c>unspecified</c>).</summary>
    Unspecified = 0,

    /// <summary>Beside the left luma sample, between the rows (MPEG-2's and H.264's default for 4:2:0).</summary>
    Left = 1,

    /// <summary>Between the four luma samples (JPEG's, MPEG-1's).</summary>
    Center = 2,

    /// <summary>On the top left luma sample (BT.2020's 4:2:0 and DV's).</summary>
    TopLeft = 3,

    /// <summary>Between the top two luma samples.</summary>
    Top = 4,

    /// <summary>On the bottom left luma sample.</summary>
    BottomLeft = 5,

    /// <summary>Between the bottom two luma samples.</summary>
    Bottom = 6,
}

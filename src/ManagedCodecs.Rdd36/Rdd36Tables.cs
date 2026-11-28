namespace ManagedCodecs.Rdd36;

/// <summary>The tables of SMPTE RDD 36: the block scan and the codebooks of the coefficients' variable-length codes.</summary>
internal static class Rdd36Tables
{
    /// <summary>
    /// The progressive block scan (RDD 36, Figure 4) the other way round: for each scanned frequency index, the
    /// coefficient's place in its block, 8 * v + u.
    /// </summary>
    public static ReadOnlySpan<byte> ProgressiveScan =>
    [
        0, 1, 8, 9, 2, 3, 10, 11,
        16, 17, 24, 25, 18, 19, 26, 27,
        4, 5, 12, 20, 13, 6, 7, 14,
        21, 28, 29, 22, 15, 23, 30, 31,
        32, 33, 40, 48, 41, 34, 35, 42,
        49, 56, 57, 50, 43, 36, 37, 44,
        51, 58, 59, 52, 45, 38, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>The codebook of a dc_coeff_difference by the previous difference's size, 3 for 3 and above (Table 9).</summary>
    public static readonly Rdd36Codebook[] DcDifference =
    [
        Rdd36Codebook.ExpGolomb(0),
        Rdd36Codebook.ExpGolomb(1),
        new Rdd36Codebook(1, 2, 3),
        Rdd36Codebook.ExpGolomb(3),
    ];

    /// <summary>The codebook of a run by the previous run, 15 for 15 and above (Table 10).</summary>
    public static readonly Rdd36Codebook[] Run =
    [
        new Rdd36Codebook(2, 0, 1),
        new Rdd36Codebook(2, 0, 1),
        new Rdd36Codebook(1, 0, 1),
        new Rdd36Codebook(1, 0, 1),
        Rdd36Codebook.ExpGolomb(0),
        new Rdd36Codebook(1, 1, 2),
        new Rdd36Codebook(1, 1, 2),
        new Rdd36Codebook(1, 1, 2),
        new Rdd36Codebook(1, 1, 2),
        Rdd36Codebook.ExpGolomb(1),
        Rdd36Codebook.ExpGolomb(1),
        Rdd36Codebook.ExpGolomb(1),
        Rdd36Codebook.ExpGolomb(1),
        Rdd36Codebook.ExpGolomb(1),
        Rdd36Codebook.ExpGolomb(1),
        Rdd36Codebook.ExpGolomb(2),
    ];

    /// <summary>The codebook of an abs_level_minus_1 by the previous one, 8 for 8 and above (Table 11).</summary>
    public static readonly Rdd36Codebook[] Level =
    [
        new Rdd36Codebook(2, 0, 2),
        new Rdd36Codebook(1, 0, 1),
        new Rdd36Codebook(2, 0, 1),
        Rdd36Codebook.ExpGolomb(0),
        Rdd36Codebook.ExpGolomb(1),
        Rdd36Codebook.ExpGolomb(1),
        Rdd36Codebook.ExpGolomb(1),
        Rdd36Codebook.ExpGolomb(1),
        Rdd36Codebook.ExpGolomb(2),
    ];
}

/// <summary>
/// A Golomb-Rice/exponential-Golomb combination code (RDD 36, 7.1.1.1): symbols whose code level (the count of leading
/// zero bits) is at most <see cref="LastRiceQ"/> are Golomb-Rice codewords of order <see cref="RiceOrder"/>, the others
/// exponential-Golomb codewords of order <see cref="ExpOrder"/> after <see cref="LastRiceQ"/> + 1 zero bits.
/// </summary>
internal readonly struct Rdd36Codebook
{
    public Rdd36Codebook(byte lastRiceQ, byte riceOrder, byte expOrder)
    {
        LastRiceQ = lastRiceQ;
        RiceOrder = riceOrder;
        ExpOrder = expOrder;
    }

    public byte LastRiceQ { get; }

    public byte RiceOrder { get; }

    public byte ExpOrder { get; }

    /// <summary>
    /// The exponential-Golomb code of order <paramref name="order"/>, which the combination code with lastRiceQ 0,
    /// Rice order k and exponential order k + 1 is identical to (the note that ends 7.1.1.1).
    /// </summary>
    public static Rdd36Codebook ExpGolomb(byte order) => new(0, order, (byte)(order + 1));
}

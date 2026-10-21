namespace ManagedCodecs.H264;

/// <summary>
/// The scaling matrices of a picture (7.3.2.1.1.1, 7.4.2.1.1, Table 7-2) and the LevelScale tables
/// they give (8.5.9). Lists 0 to 5 are 4x4 (Intra Y, Cb, Cr, Inter Y, Cb, Cr), 6 and 7 are 8x8
/// (Intra Y, Inter Y); 4:4:4 has four more 8x8 lists, parsed but not used.
/// </summary>
internal sealed class ScalingMatrix
{
    // weightScale per list, in zig-zag order as the bitstream sends it.
    private readonly byte[][] _lists;

    // LevelScale4x4 per 4x4 list (qP % 6 * 16 + row-major index) and LevelScale8x8 per 8x8 list (qP % 6 * 64 + index).
    private readonly int[][] _levelScale4x4;
    private readonly int[][] _levelScale8x8;

    private ScalingMatrix(byte[][] lists)
    {
        _lists = lists;
        _levelScale4x4 = [.. Enumerable.Range(0, 6).Select(i => Transform.LevelScale4x4Table(lists[i]))];
        _levelScale8x8 = [.. Enumerable.Range(6, 2).Select(i => Transform.LevelScale8x8Table(lists[i]))];
    }

    /// <summary>Flat_4x4_16 and Flat_8x8_16: no scaling matrix in either parameter set.</summary>
    public static ScalingMatrix Flat { get; } = new([.. Enumerable.Range(0, 12).Select(i => Enumerable.Repeat((byte)16, i < 6 ? 16 : 64).ToArray())]);

    // Default scaling lists (Tables 7-3 and 7-4), in zig-zag order.
    public static ReadOnlySpan<byte> Default4x4Intra => [6, 13, 13, 20, 20, 20, 28, 28, 28, 28, 32, 32, 32, 37, 37, 42];

    public static ReadOnlySpan<byte> Default4x4Inter => [10, 14, 14, 20, 20, 20, 24, 24, 24, 24, 27, 27, 27, 30, 30, 34];

    public static ReadOnlySpan<byte> Default8x8Intra =>
    [
        6, 10, 10, 13, 11, 13, 16, 16, 16, 16, 18, 18, 18, 18, 18, 23,
        23, 23, 23, 23, 23, 25, 25, 25, 25, 25, 25, 25, 27, 27, 27, 27,
        27, 27, 27, 27, 29, 29, 29, 29, 29, 29, 29, 31, 31, 31, 31, 31,
        31, 33, 33, 33, 33, 33, 36, 36, 36, 36, 38, 38, 38, 40, 40, 42,
    ];

    public static ReadOnlySpan<byte> Default8x8Inter =>
    [
        9, 13, 13, 15, 13, 15, 17, 17, 17, 17, 19, 19, 19, 19, 19, 21,
        21, 21, 21, 21, 21, 22, 22, 22, 22, 22, 22, 22, 24, 24, 24, 24,
        24, 24, 24, 24, 25, 25, 25, 25, 25, 25, 25, 27, 27, 27, 27, 27,
        27, 28, 28, 28, 28, 28, 30, 30, 30, 30, 32, 32, 32, 33, 33, 35,
    ];

    /// <summary>LevelScale4x4 of 4x4 list <paramref name="list"/> for every qP % 6 and position.</summary>
    public ReadOnlySpan<int> LevelScale4x4(int list) => _levelScale4x4[list];

    /// <summary>LevelScale8x8 of 8x8 list <paramref name="list"/> (0 Intra Y, 1 Inter Y) for every qP % 6 and position.</summary>
    public ReadOnlySpan<int> LevelScale8x8(int list) => _levelScale8x8[list];

    /// <summary>
    /// The <paramref name="count"/> scaling_list()s of a parameter set: each list sent, or the
    /// default one where useDefaultScalingMatrixFlag says so, or null where the list is absent.
    /// </summary>
    public static byte[]?[] ParseLists(BitReader r, int count)
    {
        var lists = new byte[]?[count];
        for (int i = 0; i < count; i++)
        {
            if (r.ReadFlag())
            {
                lists[i] = ParseList(r, i);
            }
        }

        return lists;
    }

    /// <summary>
    /// The matrix from parsed lists, absent ones filled by fall-back rule A (<paramref name="sequence"/>
    /// null: the default lists) or rule B (the sequence-level lists); lists 1, 2, 4 and 5 fall back to
    /// the list before them in either set (Table 7-2).
    /// </summary>
    public static ScalingMatrix Resolve(byte[]?[] parsed, ScalingMatrix? sequence)
    {
        var lists = new byte[12][];
        for (int i = 0; i < 12; i++)
        {
            lists[i] = (i < parsed.Length ? parsed[i] : null) ?? i switch
            {
                0 or 3 or 6 or 7 => sequence?._lists[i] ?? DefaultList(i),
                > 7 => lists[i - 2], // 4:4:4's Cb and Cr 8x8 lists
                _ => lists[i - 1],
            };
        }

        return new ScalingMatrix(lists);
    }

    /// <summary>The matrix a picture uses (7.4.2.2): the picture's own lists if its PPS has them, else the sequence's.</summary>
    public static ScalingMatrix For(SequenceParameterSet sps, PictureParameterSet pps) =>
        pps.ScalingLists is { } lists ? Resolve(lists, sps.Scaling) : sps.Scaling ?? Flat;

    private static byte[] DefaultList(int i) => i switch
    {
        < 3 => Default4x4Intra.ToArray(),
        < 6 => Default4x4Inter.ToArray(),
        _ => (i % 2 == 0 ? Default8x8Intra : Default8x8Inter).ToArray(),
    };

    /// <summary>scaling_list() (7.3.2.1.1.1): delta-coded values, where a first value of 0 selects the default list.</summary>
    private static byte[] ParseList(BitReader r, int i)
    {
        var list = new byte[i < 6 ? 16 : 64];
        int lastScale = 8;
        int nextScale = 8;
        for (int j = 0; j < list.Length; j++)
        {
            if (nextScale != 0)
            {
                int delta = r.ReadSe();
                if (delta is < -128 or > 127)
                {
                    throw new InvalidDataException($"delta_scale {delta} is out of range.");
                }

                nextScale = (lastScale + delta + 256) % 256;
                if (j == 0 && nextScale == 0)
                {
                    return DefaultList(i); // useDefaultScalingMatrixFlag
                }
            }

            list[j] = (byte)(nextScale == 0 ? lastScale : nextScale);
            lastScale = list[j];
        }

        return list;
    }
}

namespace ManagedCodecs.H264;

/// <summary>
/// CAVLC code tables from ITU-T H.264 clause 9.2, written as the spec prints them: codewords
/// as bit strings (spaces only for readability), "" where no codeword exists. Cross-checked
/// by enumerating every input of h264bsd's decoder (Apache-2.0); unit tests check that each
/// table is prefix-free.
/// </summary>
internal static class CavlcTables
{
    // Table 9-5. Index: TotalCoeff * 4 + TrailingOnes.
    /// <summary>coeff_token for 0 &lt;= nC &lt; 2.</summary>
    public static readonly string[] CoeffToken0 =
    [
        "1", "", "", "", // TotalCoeff 0
        "0001 01", "01", "", "", // TotalCoeff 1
        "0000 0111", "0001 00", "001", "", // TotalCoeff 2
        "0000 0011 1", "0000 0110", "0000 101", "0001 1", // TotalCoeff 3
        "0000 0001 11", "0000 0011 0", "0000 0101", "0000 11", // TotalCoeff 4
        "0000 0000 111", "0000 0001 10", "0000 0010 1", "0000 100", // TotalCoeff 5
        "0000 0000 0111 1", "0000 0000 110", "0000 0001 01", "0000 0100", // TotalCoeff 6
        "0000 0000 0101 1", "0000 0000 0111 0", "0000 0000 101", "0000 0010 0", // TotalCoeff 7
        "0000 0000 0100 0", "0000 0000 0101 0", "0000 0000 0110 1", "0000 0001 00", // TotalCoeff 8
        "0000 0000 0011 11", "0000 0000 0011 10", "0000 0000 0100 1", "0000 0000 100", // TotalCoeff 9
        "0000 0000 0010 11", "0000 0000 0010 10", "0000 0000 0011 01", "0000 0000 0110 0", // TotalCoeff 10
        "0000 0000 0001 111", "0000 0000 0001 110", "0000 0000 0010 01", "0000 0000 0011 00", // TotalCoeff 11
        "0000 0000 0001 011", "0000 0000 0001 010", "0000 0000 0001 101", "0000 0000 0010 00", // TotalCoeff 12
        "0000 0000 0000 1111", "0000 0000 0000 001", "0000 0000 0001 001", "0000 0000 0001 100", // TotalCoeff 13
        "0000 0000 0000 1011", "0000 0000 0000 1110", "0000 0000 0000 1101", "0000 0000 0001 000", // TotalCoeff 14
        "0000 0000 0000 0111", "0000 0000 0000 1010", "0000 0000 0000 1001", "0000 0000 0000 1100", // TotalCoeff 15
        "0000 0000 0000 0100", "0000 0000 0000 0110", "0000 0000 0000 0101", "0000 0000 0000 1000", // TotalCoeff 16
    ];

    /// <summary>coeff_token for 2 &lt;= nC &lt; 4.</summary>
    public static readonly string[] CoeffToken2 =
    [
        "11", "", "", "", // TotalCoeff 0
        "0010 11", "10", "", "", // TotalCoeff 1
        "0001 11", "0011 1", "011", "", // TotalCoeff 2
        "0000 111", "0010 10", "0010 01", "0101", // TotalCoeff 3
        "0000 0111", "0001 10", "0001 01", "0100", // TotalCoeff 4
        "0000 0100", "0000 110", "0000 101", "0011 0", // TotalCoeff 5
        "0000 0011 1", "0000 0110", "0000 0101", "0010 00", // TotalCoeff 6
        "0000 0001 111", "0000 0011 0", "0000 0010 1", "0001 00", // TotalCoeff 7
        "0000 0001 011", "0000 0001 110", "0000 0001 101", "0000 100", // TotalCoeff 8
        "0000 0000 1111", "0000 0001 010", "0000 0001 001", "0000 0010 0", // TotalCoeff 9
        "0000 0000 1011", "0000 0000 1110", "0000 0000 1101", "0000 0001 100", // TotalCoeff 10
        "0000 0000 1000", "0000 0000 1010", "0000 0000 1001", "0000 0001 000", // TotalCoeff 11
        "0000 0000 0111 1", "0000 0000 0111 0", "0000 0000 0110 1", "0000 0000 1100", // TotalCoeff 12
        "0000 0000 0101 1", "0000 0000 0101 0", "0000 0000 0100 1", "0000 0000 0110 0", // TotalCoeff 13
        "0000 0000 0011 1", "0000 0000 0010 11", "0000 0000 0011 0", "0000 0000 0100 0", // TotalCoeff 14
        "0000 0000 0010 01", "0000 0000 0010 00", "0000 0000 0010 10", "0000 0000 0000 1", // TotalCoeff 15
        "0000 0000 0001 11", "0000 0000 0001 10", "0000 0000 0001 01", "0000 0000 0001 00", // TotalCoeff 16
    ];

    /// <summary>coeff_token for 4 &lt;= nC &lt; 8.</summary>
    public static readonly string[] CoeffToken4 =
    [
        "1111", "", "", "", // TotalCoeff 0
        "0011 11", "1110", "", "", // TotalCoeff 1
        "0010 11", "0111 1", "1101", "", // TotalCoeff 2
        "0010 00", "0110 0", "0111 0", "1100", // TotalCoeff 3
        "0001 111", "0101 0", "0101 1", "1011", // TotalCoeff 4
        "0001 011", "0100 0", "0100 1", "1010", // TotalCoeff 5
        "0001 001", "0011 10", "0011 01", "1001", // TotalCoeff 6
        "0001 000", "0010 10", "0010 01", "1000", // TotalCoeff 7
        "0000 1111", "0001 110", "0001 101", "0110 1", // TotalCoeff 8
        "0000 1011", "0000 1110", "0001 010", "0011 00", // TotalCoeff 9
        "0000 0111 1", "0000 1010", "0000 1101", "0001 100", // TotalCoeff 10
        "0000 0101 1", "0000 0111 0", "0000 1001", "0000 1100", // TotalCoeff 11
        "0000 0100 0", "0000 0101 0", "0000 0110 1", "0000 1000", // TotalCoeff 12
        "0000 0011 01", "0000 0011 1", "0000 0100 1", "0000 0110 0", // TotalCoeff 13
        "0000 0010 01", "0000 0011 00", "0000 0010 11", "0000 0010 10", // TotalCoeff 14
        "0000 0001 01", "0000 0010 00", "0000 0001 11", "0000 0001 10", // TotalCoeff 15
        "0000 0000 01", "0000 0001 00", "0000 0000 11", "0000 0000 10", // TotalCoeff 16
    ];

    /// <summary>coeff_token for 8 &lt;= nC (fixed length).</summary>
    public static readonly string[] CoeffToken8 =
    [
        "0000 11", "", "", "", // TotalCoeff 0
        "0000 00", "0000 01", "", "", // TotalCoeff 1
        "0001 00", "0001 01", "0001 10", "", // TotalCoeff 2
        "0010 00", "0010 01", "0010 10", "0010 11", // TotalCoeff 3
        "0011 00", "0011 01", "0011 10", "0011 11", // TotalCoeff 4
        "0100 00", "0100 01", "0100 10", "0100 11", // TotalCoeff 5
        "0101 00", "0101 01", "0101 10", "0101 11", // TotalCoeff 6
        "0110 00", "0110 01", "0110 10", "0110 11", // TotalCoeff 7
        "0111 00", "0111 01", "0111 10", "0111 11", // TotalCoeff 8
        "1000 00", "1000 01", "1000 10", "1000 11", // TotalCoeff 9
        "1001 00", "1001 01", "1001 10", "1001 11", // TotalCoeff 10
        "1010 00", "1010 01", "1010 10", "1010 11", // TotalCoeff 11
        "1011 00", "1011 01", "1011 10", "1011 11", // TotalCoeff 12
        "1100 00", "1100 01", "1100 10", "1100 11", // TotalCoeff 13
        "1101 00", "1101 01", "1101 10", "1101 11", // TotalCoeff 14
        "1110 00", "1110 01", "1110 10", "1110 11", // TotalCoeff 15
        "1111 00", "1111 01", "1111 10", "1111 11", // TotalCoeff 16
    ];

    /// <summary>coeff_token for chroma DC in 4:2:0 (nC = -1).</summary>
    public static readonly string[] CoeffTokenChromaDc =
    [
        "01", "", "", "", // TotalCoeff 0
        "0001 11", "1", "", "", // TotalCoeff 1
        "0001 00", "0001 10", "001", "", // TotalCoeff 2
        "0000 11", "0000 011", "0000 010", "0001 01", // TotalCoeff 3
        "0000 10", "0000 0011", "0000 0010", "0000 000", // TotalCoeff 4
    ];

    /// <summary>coeff_token for chroma DC in 4:2:2 (nC = -2).</summary>
    public static readonly string[] CoeffTokenChromaDc422 =
    [
        "1", "", "", "", // TotalCoeff 0
        "0001 111", "01", "", "", // TotalCoeff 1
        "0001 110", "0001 101", "001", "", // TotalCoeff 2
        "0000 0011 1", "0001 100", "0001 011", "0000 1", // TotalCoeff 3
        "0000 0011 0", "0000 0010 1", "0001 010", "0000 01", // TotalCoeff 4
        "0000 0001 11", "0000 0001 10", "0000 0010 0", "0001 001", // TotalCoeff 5
        "0000 0000 111", "0000 0000 110", "0000 0001 01", "0001 000", // TotalCoeff 6
        "0000 0000 0111", "0000 0000 0110", "0000 0000 101", "0000 0001 00", // TotalCoeff 7
        "0000 0000 0011 1", "0000 0000 0101", "0000 0000 0100", "0000 0000 100", // TotalCoeff 8
    ];

    /// <summary>total_zeros by TotalCoeff, indexed by total_zeros (Tables 9-7, 9-8).</summary>
    public static readonly string[][] TotalZeros4x4 =
    [
        [], // TotalCoeff 0 has no total_zeros
        ["1", "011", "010", "0011", "0010", "0001 1", "0001 0", "0000 11", "0000 10", "0000 011", "0000 010", "0000 0011", "0000 0010", "0000 0001 1", "0000 0001 0", "0000 0000 1"], // TotalCoeff 1
        ["111", "110", "101", "100", "011", "0101", "0100", "0011", "0010", "0001 1", "0001 0", "0000 11", "0000 10", "0000 01", "0000 00"], // TotalCoeff 2
        ["0101", "111", "110", "101", "0100", "0011", "100", "011", "0010", "0001 1", "0001 0", "0000 01", "0000 1", "0000 00"], // TotalCoeff 3
        ["0001 1", "111", "0101", "0100", "110", "101", "100", "0011", "011", "0010", "0001 0", "0000 1", "0000 0"], // TotalCoeff 4
        ["0101", "0100", "0011", "111", "110", "101", "100", "011", "0010", "0000 1", "0001", "0000 0"], // TotalCoeff 5
        ["0000 01", "0000 1", "111", "110", "101", "100", "011", "010", "0001", "001", "0000 00"], // TotalCoeff 6
        ["0000 01", "0000 1", "101", "100", "011", "11", "010", "0001", "001", "0000 00"], // TotalCoeff 7
        ["0000 01", "0001", "0000 1", "011", "11", "10", "010", "001", "0000 00"], // TotalCoeff 8
        ["0000 01", "0000 00", "0001", "11", "10", "001", "01", "0000 1"], // TotalCoeff 9
        ["0000 1", "0000 0", "001", "11", "10", "01", "0001"], // TotalCoeff 10
        ["0000", "0001", "001", "010", "1", "011"], // TotalCoeff 11
        ["0000", "0001", "01", "1", "001"], // TotalCoeff 12
        ["000", "001", "1", "01"], // TotalCoeff 13
        ["00", "01", "1"], // TotalCoeff 14
        ["0", "1"], // TotalCoeff 15
    ];

    /// <summary>total_zeros for 4:2:0 chroma DC by TotalCoeff (Table 9-9a).</summary>
    public static readonly string[][] TotalZerosChromaDc =
    [
        [], // TotalCoeff 0 has no total_zeros
        ["1", "01", "001", "000"], // TotalCoeff 1
        ["1", "01", "00"], // TotalCoeff 2
        ["1", "0"], // TotalCoeff 3
    ];

    /// <summary>total_zeros for 4:2:2 chroma DC by TotalCoeff (Table 9-9b).</summary>
    public static readonly string[][] TotalZerosChromaDc422 =
    [
        [], // TotalCoeff 0 has no total_zeros
        ["1", "010", "011", "0010", "0011", "0001", "0000 1", "0000 0"], // TotalCoeff 1
        ["000", "01", "001", "100", "101", "110", "111"], // TotalCoeff 2
        ["000", "001", "01", "10", "110", "111"], // TotalCoeff 3
        ["110", "00", "01", "10", "111"], // TotalCoeff 4
        ["00", "01", "10", "11"], // TotalCoeff 5
        ["00", "01", "1"], // TotalCoeff 6
        ["0", "1"], // TotalCoeff 7
    ];

    /// <summary>run_before by zerosLeft (1..6, then 7 for "more than 6"), indexed by run_before (Table 9-10).</summary>
    public static readonly string[][] RunBefore =
    [
        [], // zerosLeft 0 has no run_before
        ["1", "0"], // zerosLeft 1
        ["1", "01", "00"], // zerosLeft 2
        ["11", "10", "01", "00"], // zerosLeft 3
        ["11", "10", "01", "001", "000"], // zerosLeft 4
        ["11", "10", "011", "010", "001", "000"], // zerosLeft 5
        ["11", "000", "001", "011", "010", "101", "100"], // zerosLeft 6
        ["111", "110", "101", "100", "011", "010", "001", "0001", "0000 1", "0000 01", "0000 001", "0000 0001", "0000 0000 1", "0000 0000 01", "0000 0000 001"], // zerosLeft > 6
    ];

    /// <summary>coded_block_pattern by codeNum for Intra_4x4 macroblocks (Table 9-4, ChromaArrayType 1 or 2).</summary>
    public static ReadOnlySpan<byte> CodedBlockPatternIntra =>
    [
        47, 31, 15, 0, 23, 27, 29, 30, 7, 11, 13, 14, 39, 43, 45, 46,
        16, 3, 5, 10, 12, 19, 21, 26, 28, 35, 37, 42, 44, 1, 2, 4,
        8, 17, 18, 20, 24, 6, 9, 22, 25, 32, 33, 34, 36, 40, 38, 41,
    ];

    /// <summary>coded_block_pattern by codeNum for inter macroblocks (Table 9-4, ChromaArrayType 1 or 2).</summary>
    public static ReadOnlySpan<byte> CodedBlockPatternInter =>
    [
        0, 16, 1, 2, 4, 8, 32, 3, 5, 10, 12, 15, 47, 7, 11, 13,
        14, 6, 9, 31, 35, 37, 42, 44, 33, 34, 36, 40, 39, 43, 45, 46,
        17, 18, 20, 24, 19, 21, 26, 28, 23, 27, 29, 30, 22, 25, 38, 41,
    ];
}

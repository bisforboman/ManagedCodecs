using ManagedCodecs.Rdd36;

namespace ManagedCodecs.Tests.Rdd36;

/// <summary>
/// The variable-length codes of RDD 36, 7.1.1: codewords written as the document describes encoding them
/// (<see cref="Rdd36FrameWriter.BitWriter"/>) come back, each taking exactly its bits; the codewords the document's
/// rules give for small symbols, bit by bit; the signed mapping of Table 8; and endOfData().
/// </summary>
public class Rdd36BitReaderTests
{
    public static TheoryData<int, int, int> Codebooks()
    {
        // Every codebook of Tables 9 to 11, and the order-5 exponential-Golomb code of first_dc_coeff.
        var data = new TheoryData<int, int, int>();
        var books = Rdd36Tables.DcDifference.Concat(Rdd36Tables.Run).Concat(Rdd36Tables.Level).Append(Rdd36Codebook.ExpGolomb(5));
        foreach (var (lastRiceQ, riceOrder, expOrder) in books.Select(b => ((int)b.LastRiceQ, (int)b.RiceOrder, (int)b.ExpOrder)).Distinct())
        {
            data.Add(lastRiceQ, riceOrder, expOrder);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Codebooks))]
    public void ReadsBackEverySymbol(int lastRiceQ, int riceOrder, int expOrder)
    {
        var book = new Rdd36Codebook((byte)lastRiceQ, (byte)riceOrder, (byte)expOrder);
        int[] code = [lastRiceQ, riceOrder, expOrder];
        var symbols = Enumerable.Range(0, 600).Concat([1000, 4095, 4096, 65535, 65536, 1 << 20, (1 << 24) + 12345]).ToArray();
        var bits = new Rdd36FrameWriter.BitWriter();
        foreach (int symbol in symbols)
        {
            bits.Code(symbol, code);
            bits.Bits(1, 1); // a sign bit after each, as after a level: the next codeword starts where this one ends
        }

        var reader = new Rdd36BitReader(bits.ToArray());
        foreach (int symbol in symbols)
        {
            Assert.Equal(symbol, reader.ReadCode(book));
            Assert.Equal(1, reader.ReadBit());
        }

        Assert.True(reader.EndOfData);
    }

    [Theory]
    // EXP_GOLOMB_CODE(0): the binary representation of n + 1, zero-extended by its length less one.
    [InlineData(0, 0, 1, 0, "1")]
    [InlineData(0, 0, 1, 1, "010")]
    [InlineData(0, 0, 1, 2, "011")]
    [InlineData(0, 0, 1, 3, "00100")]
    [InlineData(0, 0, 1, 6, "00111")]
    [InlineData(0, 0, 1, 7, "0001000")]
    // EXP_GOLOMB_CODE(3): n + 8.
    [InlineData(0, 3, 4, 0, "1000")]
    [InlineData(0, 3, 4, 7, "1111")]
    [InlineData(0, 3, 4, 8, "010000")]
    // RICE_EXP_COMBO_CODE(2, 0, 1): Rice order 0 up to q = 2, then three zeros and EXP_GOLOMB_CODE(1) of n - 3.
    [InlineData(2, 0, 1, 0, "1")]
    [InlineData(2, 0, 1, 1, "01")]
    [InlineData(2, 0, 1, 2, "001")]
    [InlineData(2, 0, 1, 3, "00010")]
    [InlineData(2, 0, 1, 4, "00011")]
    [InlineData(2, 0, 1, 5, "0000100")]
    // RICE_EXP_COMBO_CODE(1, 2, 3): Rice order 2 (q zeros, a one, two bits) up to n = 7, then two zeros and
    // EXP_GOLOMB_CODE(3) of n - 8.
    [InlineData(1, 2, 3, 0, "100")]
    [InlineData(1, 2, 3, 3, "111")]
    [InlineData(1, 2, 3, 4, "0100")]
    [InlineData(1, 2, 3, 7, "0111")]
    [InlineData(1, 2, 3, 8, "001000")]
    [InlineData(1, 2, 3, 15, "001111")]
    [InlineData(1, 2, 3, 16, "00010000")]
    // RICE_EXP_COMBO_CODE(1, 1, 2).
    [InlineData(1, 1, 2, 3, "011")]
    [InlineData(1, 1, 2, 4, "00100")]
    public void ReadsTheDocumentsCodewords(int lastRiceQ, int riceOrder, int expOrder, int symbol, string codeword)
    {
        var bits = new Rdd36FrameWriter.BitWriter();
        foreach (char bit in codeword)
        {
            bits.Bits(bit == '1' ? 1u : 0u, 1);
        }

        bits.Bits(1, 1);
        var reader = new Rdd36BitReader(bits.ToArray());

        Assert.Equal(symbol, reader.ReadCode(new Rdd36Codebook((byte)lastRiceQ, (byte)riceOrder, (byte)expOrder)));
        Assert.Equal(1, reader.ReadBit()); // exactly the codeword's bits were taken
    }

    [Fact]
    public void ExponentialGolombIsTheCombinationCodeTheNoteNames()
    {
        // 7.1.1.1's note: lastRiceQ 0, kRice k and kExp k + 1 is the exponential-Golomb code of order k.
        for (byte order = 0; order < 6; order++)
        {
            var book = Rdd36Codebook.ExpGolomb(order);
            Assert.Equal((0, order, order + 1), (book.LastRiceQ, book.RiceOrder, book.ExpOrder));
            for (int symbol = 0; symbol < 200; symbol++)
            {
                var bits = new Rdd36FrameWriter.BitWriter();
                bits.ExpGolomb(symbol, order);
                Assert.Equal(symbol, new Rdd36BitReader(bits.ToArray()).ReadCode(book));
            }
        }
    }

    [Theory]
    [InlineData(0, 0)] // Table 8
    [InlineData(1, -1)]
    [InlineData(2, 1)]
    [InlineData(3, -2)]
    [InlineData(4, 2)]
    [InlineData(5, -3)]
    [InlineData(6, 3)]
    public void MapsSymbolsToSignedIntegers(int symbol, int n)
    {
        Assert.Equal(n, Rdd36BitReader.ToSigned(symbol));
        Assert.Equal(symbol, Rdd36FrameWriter.SymbolOf(n));
    }

    [Theory]
    [InlineData(new byte[0], 0, true)] // nothing left
    [InlineData(new byte[] { 0 }, 0, true)]
    [InlineData(new byte[] { 0, 0, 0 }, 0, true)] // 24 zero bits
    [InlineData(new byte[] { 0x80, 0, 0, 0 }, 1, true)] // 31 zero bits: the most that end the data
    [InlineData(new byte[] { 0, 0, 0, 0 }, 0, false)] // 32 bits remain
    [InlineData(new byte[] { 0, 0, 0, 1 }, 1, false)] // a one among 31
    [InlineData(new byte[] { 0x80, 0, 0, 0, 0 }, 1, false)] // 39 bits remain
    [InlineData(new byte[] { 0x01 }, 7, false)]
    [InlineData(new byte[] { 0x01 }, 8, true)]
    public void EndsTheDataAsTheSyntaxFunctionSays(byte[] data, int bitsRead, bool end)
    {
        var reader = new Rdd36BitReader(data);
        for (int i = 0; i < bitsRead; i++)
        {
            reader.ReadBit();
        }

        Assert.Equal(end, reader.EndOfData);
    }

    [Fact]
    public void RejectsCodewordsThatRunPastTheData()
    {
        Assert.Throws<InvalidDataException>(() => new Rdd36BitReader([]).ReadBit());
        Assert.Throws<InvalidDataException>(() => new Rdd36BitReader([]).ReadCode(Rdd36Codebook.ExpGolomb(0)));

        // "0001" starts a codeword of seven bits; four are there.
        Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new Rdd36BitReader([0b1111_0001]);
            reader.ReadBit();
            reader.ReadBit();
            reader.ReadBit();
            reader.ReadBit();
            reader.ReadCode(Rdd36Codebook.ExpGolomb(0));
        });

        // Order 5: "1" and five bits, of which three are there.
        Assert.Throws<InvalidDataException>(() => new Rdd36BitReader([0b1010_0000]).ReadBit() + ReadAfter(4, [0b0000_1010], Rdd36Codebook.ExpGolomb(5)));
    }

    [Fact]
    public void RejectsCodewordsLongerThanAnyCoefficients()
    {
        // 40 zero bits before the one: a symbol of 40 bits and more.
        Assert.Throws<InvalidDataException>(() => new Rdd36BitReader([0, 0, 0, 0, 0, 0x80, 0, 0, 0, 0, 0]).ReadCode(Rdd36Codebook.ExpGolomb(0)));

        // Zeros alone.
        Assert.Throws<InvalidDataException>(() => new Rdd36BitReader(new byte[32]).ReadCode(new Rdd36Codebook(2, 0, 1)));

        // The longest it takes: a suffix of 30 bits.
        var bits = new Rdd36FrameWriter.BitWriter();
        bits.ExpGolomb((1 << 30) - 1 + 12345, 0);
        Assert.Equal((1 << 30) - 1 + 12345, new Rdd36BitReader(bits.ToArray()).ReadCode(Rdd36Codebook.ExpGolomb(0)));
    }

    private static int ReadAfter(int bitsRead, byte[] data, Rdd36Codebook book)
    {
        var reader = new Rdd36BitReader(data);
        for (int i = 0; i < bitsRead; i++)
        {
            reader.ReadBit();
        }

        return reader.ReadCode(book);
    }
}

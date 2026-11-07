using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Tests.Mjpeg;

/// <summary>
/// The table lookups of <see cref="HuffmanTable.DecodeAc"/> and <see cref="HuffmanTable.DecodeDc"/> (a whole
/// coefficient, a code only, or MAXCODE for a longer code, on the reader's state in locals) against T.81's procedure
/// bit by bit (<see cref="HuffmanTable.Decode"/>, F.16) and its magnitude bits (F.2.2.1).
/// </summary>
public class HuffmanTableTests
{
    public static TheoryData<int> Seeds => [.. Enumerable.Range(0, 40)];

    [Fact]
    public void AnnexKTables_MatchTheReference()
    {
        var random = new Random(1);
        for (int i = 0; i < 20; i++)
        {
            byte[] data = RandomScan(random);
            Check(new HuffmanTable(JpegTables.DcLuminanceBits, JpegTables.DcLuminanceValues), data, ac: false);
            Check(new HuffmanTable(JpegTables.DcChrominanceBits, JpegTables.DcChrominanceValues), data, ac: false);
            Check(new HuffmanTable(JpegTables.AcLuminanceBits, JpegTables.AcLuminanceValues), data, ac: true);
            Check(new HuffmanTable(JpegTables.AcChrominanceBits, JpegTables.AcChrominanceValues), data, ac: true);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomTables_MatchTheReference(int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < 10; i++)
        {
            bool ac = random.Next(2) == 0;
            var table = RandomTable(random, ac);
            Check(table, RandomScan(random), ac);
        }
    }

    private static void Check(HuffmanTable table, byte[] data, bool ac)
    {
        var fast = new JpegBitReader(data);
        var reference = new JpegBitReader(data);
        for (int i = 0; i < 2000; i++)
        {
            int expected;
            try
            {
                int symbol = table.Decode(ref reference);
                int size = ac ? symbol & 0x0F : symbol;
                if (!ac && size > 11)
                {
                    throw new InvalidDataException("Invalid DC coefficient size.");
                }

                int value = size == 0 ? 0 : HuffmanTable.Extend(reference.ReadBits(size), size);
                expected = ac ? (value << 8) | (symbol >> 4) : value;
            }
            catch (InvalidDataException)
            {
                expected = int.MinValue;
            }

            int actual;
            ulong buffer = fast.Buffer;
            int count = fast.Count;
            try
            {
                actual = ac ? table.DecodeAc(ref fast, ref buffer, ref count) : table.DecodeDc(ref fast, ref buffer, ref count);
            }
            catch (InvalidDataException)
            {
                actual = int.MinValue;
            }

            Assert.Equal(expected, actual);
            if (expected == int.MinValue)
            {
                return;
            }

            fast.Restore(buffer, count);
            Assert.Equal(reference.Overread, fast.Overread);
        }
    }

    /// <summary>Random entropy-coded data, its 0xFF bytes stuffed (0xFF00), so the readers meet no marker.</summary>
    private static byte[] RandomScan(Random random)
    {
        var data = new List<byte>();
        int length = random.Next(1, 3000);
        for (int i = 0; i < length; i++)
        {
            // Skewed toward ones (the long codes are mostly ones) about half the time.
            byte b = random.Next(2) == 0 ? (byte)random.Next(256) : (byte)(random.Next(256) | random.Next(256));
            data.Add(b);
            if (b == 0xFF)
            {
                data.Add(0x00);
            }
        }

        return [.. data];
    }

    /// <summary>A random canonical table, complete or not: random counts within what each length leaves free.</summary>
    private static HuffmanTable RandomTable(Random random, bool ac)
    {
        var bits = new byte[16];
        int remaining = ac ? random.Next(1, 163) : random.Next(1, 17);
        int code = 0;
        for (int length = 1; length <= 16 && remaining > 0; length++)
        {
            int free = (1 << length) - code;

            // Keep a code free for the longer lengths most of the time, so tables reach long codes.
            int most = Math.Min(remaining, random.Next(4) == 0 ? free : free - 1);
            int count = most <= 0 ? 0 : random.Next(Math.Min(most, 1 + (most * random.Next(1, 4) / 4)) + 1);
            bits[length - 1] = (byte)count;
            remaining -= count;
            code = (code + count) << 1;
        }

        int total = bits.Sum(b => b);
        byte[] values = ac
            ? [.. Enumerable.Range(0, 256).OrderBy(_ => random.Next()).Take(total).Select(v => (byte)v)]
            : [.. Enumerable.Range(0, total).Select(_ => (byte)random.Next(random.Next(4) == 0 ? 16 : 12))];
        return new HuffmanTable(bits, values);
    }
}

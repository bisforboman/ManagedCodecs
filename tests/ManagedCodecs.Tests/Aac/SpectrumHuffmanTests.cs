using ManagedCodecs.Aac;

namespace ManagedCodecs.Tests.Aac;

/// <summary>
/// The spectrum's packed Huffman reader (one table lookup a codeword, one read for its signs, through a 64-bit cache
/// that reads past the end as zeros) against the reader by the definition (<see cref="Huffman.ReadSpectrumByDefinition"/>,
/// a bit at a time for the signs and escapes, throwing at the end): the same values, the same position after, and the
/// same failure, on random bits for every codebook, across refills, past the end, and with long escapes.
/// </summary>
public class SpectrumHuffmanTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PackedReader_MatchesDefinition(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 3000; trial++)
        {
            int book = random.Next(1, 12);
            int step = book <= 4 ? 4 : 2;

            // Mostly long enough for the bands, sometimes truncated; bytes biased to ones reach book 11's long escapes.
            int length = random.Next(4) == 0 ? random.Next(0, 24) : random.Next(24, 400);
            byte[] data = new byte[length];
            random.NextBytes(data);
            if (random.Next(3) == 0)
            {
                for (int i = 0; i < length; i++)
                {
                    data[i] |= (byte)(random.Next(256) | random.Next(256));
                }
            }

            int skip = random.Next(0, 9);
            int bands = random.Next(1, 6);
            var widths = Enumerable.Range(0, bands).Select(_ => step * random.Next(1, 9)).ToArray();

            var reference = new AacBitReader(data);
            reference.Skip(Math.Min(skip, length * 8));
            int[] expected = new int[widths.Sum()];
            string? expectedError = null;
            try
            {
                Span<int> values = stackalloc int[4];
                for (int k = 0; k < expected.Length; k += step)
                {
                    Huffman.ReadSpectrumByDefinition(reference, book, values);
                    values[..step].CopyTo(expected.AsSpan(k));
                }
            }
            catch (InvalidDataException e)
            {
                expectedError = e.Message;
            }

            var reader = new AacBitReader(data);
            reader.Skip(Math.Min(skip, length * 8));
            int[] actual = new int[expected.Length];
            string? actualError = null;
            try
            {
                var cursor = reader.Begin();
                int start = 0;
                foreach (int width in widths)
                {
                    Huffman.ReadSpectrum(reader, ref cursor, book, actual, start, start + width);
                    start += width;
                }

                reader.Commit(cursor);
            }
            catch (InvalidDataException e)
            {
                actualError = e.Message;
            }

            Assert.Equal(expectedError, actualError);
            if (expectedError is null)
            {
                Assert.Equal(expected, actual);
                Assert.Equal(reference.Position, reader.Position);
            }
        }
    }

    /// <summary>
    /// The cursor against the reader's own reads, which load the bytes every time: every peek of 1 to 32 bits agrees,
    /// across refills and past the end (zeros), and the position committed is the reader's.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Cursor_MatchesReader(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 200; trial++)
        {
            int length = random.Next(0, 40);
            byte[] data = new byte[length];
            random.NextBytes(data);
            var reference = new AacBitReader(data);
            var reader = new AacBitReader(data);
            int skip = random.Next(0, Math.Min(9, (length * 8) + 1));
            reference.Skip(skip);
            reader.Skip(skip);

            var cursor = reader.Begin();
            int position = skip;
            while (position < (length * 8) + 64)
            {
                int count = random.Next(1, 33);
                uint expected = position + count <= length * 8 ? reference.Peek(count) : PeekPastEnd(data, position, count);
                Assert.Equal(expected, cursor.Peek(count));
                int consumed = random.Next(0, count + 1);
                cursor.Skip(consumed);
                position += consumed;
                if (position <= length * 8)
                {
                    reference.Skip(consumed);
                }

                Assert.Equal(position, cursor.Position);
            }

            bool threw = false;
            try
            {
                reader.Commit(cursor);
            }
            catch (InvalidDataException)
            {
                threw = true;
            }

            Assert.True(threw, "A cursor past the end must not commit.");
        }
    }

    /// <summary>The bits from <paramref name="position"/> on, those past the data zero.</summary>
    private static uint PeekPastEnd(byte[] data, int position, int count)
    {
        uint value = 0;
        for (int i = position; i < position + count; i++)
        {
            int bit = i < data.Length * 8 ? (data[i >> 3] >> (7 - (i & 7))) & 1 : 0;
            value = (value << 1) | (uint)bit;
        }

        return value;
    }
}

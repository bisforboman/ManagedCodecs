using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// <see cref="BitReader.Cursor"/> reads through a 64-bit cache refilled once fewer than 32 of its bits are left; the
/// reader's own reads load the bytes every time. The two must agree on every read, across refills and past the end.
/// </summary>
public class BitReaderCursorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Cursor_MatchesReader(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 200; trial++)
        {
            int length = random.Next(0, 40);
            byte[] data = new byte[length + 8];
            random.NextBytes(data.AsSpan(0, length));

            // The padding after the RBSP holds garbage that both must read as zeros.
            random.NextBytes(data.AsSpan(length));
            var reference = new BitReader(data, length);
            var reader = new BitReader(data, length);
            reference.ReadBits(random.Next(0, 9));
            reader.ReadBits((int)reference.Position);

            var cursor = reader.Begin();
            while (reference.Position < (length * 8L) + 64)
            {
                int count = random.Next(1, 33);
                if (random.Next(2) == 0)
                {
                    Assert.Equal(reference.ReadBits(count), cursor.ReadBits(count));
                }
                else
                {
                    Assert.Equal(reference.Peek(count), cursor.Peek(count));
                    int skip = random.Next(0, count + 1);
                    reference.Skip(skip);
                    cursor.Skip(skip);
                }

                Assert.Equal(reference.Position, cursor.Position);
            }

            reader.Commit(cursor);
            Assert.Equal(reference.Position, reader.Position);
        }
    }
}

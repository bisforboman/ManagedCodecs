using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// The CABAC decoding engine against an encoder written from H.264 clause 9.3.4: random bins
/// through random contexts, bypass and terminate bins must come back exactly, and after a
/// terminate bin of 1 the reader must stand where the encoder's flush ended (what I_PCM needs).
/// </summary>
public class CabacTests
{
    [Fact]
    public void Decoder_ReproducesEncodedBins()
    {
        var random = new Random(5);
        for (int trial = 0; trial < 300; trial++)
        {
            int table = random.Next(4);
            int qp = random.Next(52);
            var bins = new List<(int Kind, int Ctx, int Bin)>();
            int count = random.Next(1, 3000);
            for (int i = 0; i < count; i++)
            {
                int kind = random.Next(10) switch { < 7 => 0, < 9 => 1, _ => 2 }; // decision, bypass, terminate 0
                int ctx = random.Next(CabacTables.ContextCount);

                // Skewed bins, so contexts move toward their extreme states too.
                int bin = random.Next(100) < (ctx % 3 == 0 ? 95 : 50) ? 1 : 0;
                bins.Add((kind, ctx, kind == 2 ? 0 : bin));
            }

            var encoder = new CabacEncoder(table, qp);
            foreach (var (kind, ctx, bin) in bins)
            {
                switch (kind)
                {
                    case 0: encoder.Decision(ctx, bin); break;
                    case 1: encoder.Bypass(bin); break;
                    default: encoder.Terminate(0); break;
                }
            }

            encoder.Terminate(1);
            byte[] data = encoder.Writer.ToBytes();
            int bitCount = encoder.Writer.Count;

            var buffer = new byte[data.Length + 8];
            data.CopyTo(buffer, 0);
            var reader = new BitReader(buffer, data.Length);
            var decoder = new Cabac();
            decoder.InitContexts(table, qp);
            decoder.InitEngine(reader);
            for (int i = 0; i < bins.Count; i++)
            {
                var (kind, ctx, bin) = bins[i];
                int decoded = kind switch { 0 => decoder.Decision(ctx), 1 => decoder.Bypass(), _ => decoder.Terminate() };
                Assert.True(decoded == bin, $"Trial {trial}, bin {i} (kind {kind}): {decoded} instead of {bin}.");
            }

            Assert.Equal(1, decoder.Terminate());
            Assert.Equal(bitCount, reader.Position);
        }
    }
}

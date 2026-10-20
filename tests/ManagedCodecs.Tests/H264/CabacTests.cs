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

            var encoder = new Encoder(table, qp);
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
            var (data, bitCount) = encoder.Finish();

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

    /// <summary>The arithmetic encoder of 9.3.4, with the same context initialization as the decoder.</summary>
    private sealed class Encoder
    {
        private readonly byte[] _states = new byte[CabacTables.ContextCount];
        private readonly byte[] _mps = new byte[CabacTables.ContextCount];
        private readonly List<int> _bits = [];
        private int _low;
        private int _range = 510;
        private bool _firstBit = true;
        private int _outstanding;

        public Encoder(int table, int qp)
        {
            var values = CabacTables.InitValues;
            for (int ctx = 0; ctx < CabacTables.ContextCount; ctx++)
            {
                int pre = Math.Clamp(((values[(ctx * 8) + (table * 2)] * qp) >> 4) + values[(ctx * 8) + (table * 2) + 1], 1, 126);
                _states[ctx] = (byte)(pre <= 63 ? 63 - pre : pre - 64);
                _mps[ctx] = (byte)(pre <= 63 ? 0 : 1);
            }
        }

        // 9.3.4.2
        public void Decision(int ctx, int bin)
        {
            int state = _states[ctx];
            int lps = CabacTables.RangeTabLps[(state * 4) + ((_range >> 6) & 3)];
            _range -= lps;
            if (bin != _mps[ctx])
            {
                _low += _range;
                _range = lps;
                if (state == 0)
                {
                    _mps[ctx] = (byte)(1 - _mps[ctx]);
                }

                _states[ctx] = CabacTables.TransIdxLps[state];
            }
            else
            {
                _states[ctx] = CabacTables.TransIdxMps[state];
            }

            Renormalize();
        }

        // 9.3.4.4
        public void Bypass(int bin)
        {
            _low <<= 1;
            if (bin != 0)
            {
                _low += _range;
            }

            if (_low >= 1024)
            {
                PutBit(1);
                _low -= 1024;
            }
            else if (_low < 512)
            {
                PutBit(0);
            }
            else
            {
                _low -= 512;
                _outstanding++;
            }
        }

        // 9.3.4.5
        public void Terminate(int bin)
        {
            _range -= 2;
            if (bin != 0)
            {
                _low += _range;
                Flush();
            }
            else
            {
                Renormalize();
            }
        }

        public (byte[] Data, int BitCount) Finish()
        {
            var data = new byte[(_bits.Count + 7) / 8];
            for (int i = 0; i < _bits.Count; i++)
            {
                data[i / 8] |= (byte)(_bits[i] << (7 - (i % 8)));
            }

            return (data, _bits.Count);
        }

        private void Flush()
        {
            _range = 2;
            Renormalize();
            PutBit((_low >> 9) & 1);
            int last = ((_low >> 7) & 3) | 1;
            _bits.Add(last >> 1);
            _bits.Add(last & 1);
        }

        // 9.3.4.3
        private void Renormalize()
        {
            while (_range < 256)
            {
                if (_low < 256)
                {
                    PutBit(0);
                }
                else if (_low >= 512)
                {
                    _low -= 512;
                    PutBit(1);
                }
                else
                {
                    _low -= 256;
                    _outstanding++;
                }

                _range <<= 1;
                _low <<= 1;
            }
        }

        private void PutBit(int bit)
        {
            if (_firstBit)
            {
                _firstBit = false;
            }
            else
            {
                _bits.Add(bit);
            }

            for (; _outstanding > 0; _outstanding--)
            {
                _bits.Add(1 - bit);
            }
        }
    }
}

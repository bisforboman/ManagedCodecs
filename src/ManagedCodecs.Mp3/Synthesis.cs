namespace ManagedCodecs.Mp3;

/// <summary>
/// The polyphase synthesis filterbank (ISO/IEC 11172-3 2.4.3.4.10, Annex A, Figure A.2): 32 subband samples in, 32
/// output samples out, through the matrixing N[i][k] = cos((16 + i)(2k + 1) pi / 64), a FIFO V of 1024 values and the
/// window D (<see cref="Mp3Tables.SynthesisWindow"/>).
/// </summary>
/// <remarks>
/// The matrixing is a 32-point DCT-II of the subband samples, X[m] = sum over k of S[k] cos((2k + 1) m pi / 64), read
/// out by its symmetries (<see cref="Matrix"/>), computed by Lee's fast DCT (<see cref="FastDct"/>): about 80
/// multiplications where the definition takes 2048. <see cref="MatrixByDefinition"/> is the reference a test compares it
/// with on random input. V is a ring, so a time slot moves no values.
/// </remarks>
internal sealed class Synthesis
{
    private static readonly double[] Window = [.. Mp3Tables.SynthesisWindow.Select(d => d / 65536.0)];

    // N[i][k] for the definition, i * 32 + k.
    private static readonly double[] DefinitionMatrix = [.. Enumerable.Range(0, 64 * 32).Select(n => ExactMath.CosPi((16 + (n / 32)) * ((2 * (n % 32)) + 1), 64))];

    // The FIFO V, a ring: V[i] is at (_offset + i) & 1023.
    private readonly double[] _v = new double[1024];
    private int _offset;

    /// <summary>
    /// Synthesizes one time slot: <paramref name="subbands"/>[sb] is subband sb's sample; writes 32 samples to
    /// <paramref name="output"/>.
    /// </summary>
    public void Process(ReadOnlySpan<double> subbands, Span<float> output)
    {
        // Shift V by 64 (the ring's start moves back) and matrix the new samples into its start.
        _offset = (_offset - 64) & 1023;
        Span<double> v = stackalloc double[64];
        Matrix(subbands, v);
        for (int i = 0; i < 64; i++)
        {
            _v[(_offset + i) & 1023] = v[i];
        }

        // U from V (u[64 i + j] = v[128 i + j], u[64 i + 32 + j] = v[128 i + 96 + j]), windowed, and summed in 16s.
        for (int j = 0; j < 32; j++)
        {
            double sum = 0;
            for (int i = 0; i < 8; i++)
            {
                sum += _v[(_offset + (128 * i) + j) & 1023] * Window[(64 * i) + j];
                sum += _v[(_offset + (128 * i) + 96 + j) & 1023] * Window[(64 * i) + 32 + j];
            }

            output[j] = (float)sum;
        }
    }

    /// <summary>
    /// The matrixing, V[i] = sum over k of cos((16 + i)(2k + 1) pi / 64) S[k] for i below 64, from the 32-point DCT-II X
    /// of S: with m = 16 + i, cos((2k + 1) m pi / 64) changes sign from m to 64 - m and from m to m + 64, and is 0 at
    /// m = 32. So V[i] is X[16 + i] for i below 16, 0 at 16, -X[48 - i] up to 47 and -X[i - 48] from 48.
    /// </summary>
    public static void Matrix(ReadOnlySpan<double> subbands, Span<double> v)
    {
        Span<double> x = stackalloc double[32];
        Span<double> scratch = stackalloc double[32];
        subbands[..32].CopyTo(x);
        FastDct.Dct(x, scratch);
        for (int i = 0; i < 16; i++)
        {
            v[i] = x[16 + i];
        }

        v[16] = 0;
        for (int i = 17; i < 48; i++)
        {
            v[i] = -x[48 - i];
        }

        for (int i = 48; i < 64; i++)
        {
            v[i] = -x[i - 48];
        }
    }

    /// <summary>The matrixing by its definition (2.4.3.4.10, Annex A): the reference <see cref="Matrix"/> is tested against.</summary>
    public static void MatrixByDefinition(ReadOnlySpan<double> subbands, Span<double> v)
    {
        for (int i = 0; i < 64; i++)
        {
            double sum = 0;
            for (int k = 0; k < 32; k++)
            {
                sum += DefinitionMatrix[(32 * i) + k] * subbands[k];
            }

            v[i] = sum;
        }
    }
}

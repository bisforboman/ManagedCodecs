namespace ManagedCodecs.Mp3;

/// <summary>
/// The polyphase synthesis filterbank (ISO/IEC 11172-3 2.4.3.4.10, Annex A, Figure A.2): 32 subband samples in, 32
/// output samples out, through the matrixing N[i][k] = cos((16 + i)(2k + 1) pi / 64), a FIFO V of 1024 values and the
/// window D (<see cref="Mp3Tables.SynthesisWindow"/>), by the definition.
/// </summary>
internal sealed class Synthesis
{
    private static readonly double[] Matrix = BuildMatrix();

    private static readonly double[] Window = [.. Mp3Tables.SynthesisWindow.Select(d => d / 65536.0)];

    private readonly double[] _v = new double[1024];

    /// <summary>
    /// Synthesizes one time slot: <paramref name="subbands"/>[sb] is subband sb's sample; writes 32 samples to
    /// <paramref name="output"/>.
    /// </summary>
    public void Process(ReadOnlySpan<double> subbands, Span<float> output)
    {
        // Shift V by 64 and matrix the new samples into its start.
        Array.Copy(_v, 0, _v, 64, 1024 - 64);
        for (int i = 0; i < 64; i++)
        {
            double sum = 0;
            for (int k = 0; k < 32; k++)
            {
                sum += Matrix[(32 * i) + k] * subbands[k];
            }

            _v[i] = sum;
        }

        // U from V (u[64 i + j] = v[128 i + j], u[64 i + 32 + j] = v[128 i + 96 + j]), windowed, and summed in 16s.
        for (int j = 0; j < 32; j++)
        {
            double sum = 0;
            for (int i = 0; i < 8; i++)
            {
                sum += _v[(128 * i) + j] * Window[(64 * i) + j];
                sum += _v[(128 * i) + 96 + j] * Window[(64 * i) + 32 + j];
            }

            output[j] = (float)sum;
        }
    }

    private static double[] BuildMatrix()
    {
        var matrix = new double[64 * 32];
        for (int i = 0; i < 64; i++)
        {
            for (int k = 0; k < 32; k++)
            {
                matrix[(32 * i) + k] = ExactMath.CosPi((16 + i) * ((2 * k) + 1), 64);
            }
        }

        return matrix;
    }
}

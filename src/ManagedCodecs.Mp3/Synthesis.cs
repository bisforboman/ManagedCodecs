#if NET
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
#endif

namespace ManagedCodecs.Mp3;

/// <summary>
/// The polyphase synthesis filterbank (ISO/IEC 11172-3 2.4.3.4.10, Annex A, Figure A.2): 32 subband samples in, 32
/// output samples out, through the matrixing N[i][k] = cos((16 + i)(2k + 1) pi / 64), a FIFO V of 1024 values and the
/// window D (<see cref="Mp3Tables.SynthesisWindow"/>).
/// </summary>
/// <remarks>
/// <see cref="SynthesizeReference"/> is the definition. <see cref="Synthesize"/> gives the same bits: it computes only the 32
/// rows of N the others follow from (row 32 - i is row i negated, row 96 - i is row i, row 16 is zero: exact in
/// <see cref="ExactMath.CosPi"/>, so their sums are exact negations and copies), and its vectors run across outputs, each
/// lane summing in the definition's order, with no fused multiply-add. A test compares the two on random input.
/// </remarks>
internal sealed class Synthesis
{
    private static readonly double[] Matrix = BuildMatrix();

    private static readonly double[] Window = [.. Mp3Tables.SynthesisWindow.Select(d => d / 65536.0)];

#if NET
    // The rows Process computes (0 to 15, 33 to 48), by column: [32 k + n] is N[Rows(n)][k].
    private static readonly double[] Columns = BuildColumns();
#endif

    private readonly double[] _v = new double[1024];

    /// <summary>
    /// Synthesizes one time slot: <paramref name="subbands"/>[sb] is subband sb's sample; writes 32 samples to
    /// <paramref name="output"/>.
    /// </summary>
    public void Process(ReadOnlySpan<double> subbands, Span<float> output)
    {
        Span<double> sums = stackalloc double[32];
        Synthesize(subbands, sums);
        for (int j = 0; j < 32; j++)
        {
            output[j] = (float)sums[j];
        }
    }

    /// <summary>The FIFO V (for tests).</summary>
    internal ReadOnlySpan<double> V => _v;

    /// <summary><see cref="Process"/> before the outputs are rounded to float.</summary>
    internal void Synthesize(ReadOnlySpan<double> subbands, Span<double> output)
    {
#if NET
        if (Vector128.IsHardwareAccelerated && subbands.Length >= 32 && output.Length >= 32)
        {
            SynthesizeVectors(subbands, output);
            return;
        }
#endif

        SynthesizeReference(subbands, output);
    }

    /// <summary><see cref="Synthesize"/> by the definition, row by row and sample by sample.</summary>
    internal void SynthesizeReference(ReadOnlySpan<double> subbands, Span<double> output)
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

            output[j] = sum;
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

#if NET
    // Row n of the 32 computed: 0 to 15, then 33 to 48.
    private static int Row(int n) => n < 16 ? n : n + 17;

    private static double[] BuildColumns()
    {
        var columns = new double[32 * 32];
        for (int k = 0; k < 32; k++)
        {
            for (int n = 0; n < 32; n++)
            {
                columns[(32 * k) + n] = Matrix[(32 * Row(n)) + k];
            }
        }

        return columns;
    }

    private void SynthesizeVectors(ReadOnlySpan<double> subbands, Span<double> output)
    {
        Array.Copy(_v, 0, _v, 64, 1024 - 64);
        ref double v = ref MemoryMarshal.GetArrayDataReference(_v);
        ref double columns = ref MemoryMarshal.GetArrayDataReference(Columns);
        ref double window = ref MemoryMarshal.GetArrayDataReference(Window);
        Span<double> rows = stackalloc double[32];
        ref double row = ref MemoryMarshal.GetReference(rows);

        // The 32 rows, 8 at a time: each lane sums its row over k in order, as the definition does.
        for (int n = 0; n < 32; n += 8)
        {
            var a0 = Vector128<double>.Zero;
            var a1 = Vector128<double>.Zero;
            var a2 = Vector128<double>.Zero;
            var a3 = Vector128<double>.Zero;
            for (int k = 0; k < 32; k++)
            {
                var s = Vector128.Create(subbands[k]);
                ref double c = ref Unsafe.Add(ref columns, (32 * k) + n);
                a0 += Vector128.LoadUnsafe(ref c) * s;
                a1 += Vector128.LoadUnsafe(ref c, 2) * s;
                a2 += Vector128.LoadUnsafe(ref c, 4) * s;
                a3 += Vector128.LoadUnsafe(ref c, 6) * s;
            }

            a0.StoreUnsafe(ref row, (nuint)n);
            a1.StoreUnsafe(ref row, (nuint)n + 2);
            a2.StoreUnsafe(ref row, (nuint)n + 4);
            a3.StoreUnsafe(ref row, (nuint)n + 6);
        }

        // V's start from them: v[i] and v[32 - i] = -v[i] for i 0 to 15, v[16] = 0; v[i] and v[96 - i] = v[i] for i 33
        // to 48. (A row summing to zero can come out -0 where the definition has +0: the window's sums, which start at
        // +0, give the same output.)
        for (int i = 0; i < 16; i++)
        {
            _v[i] = rows[i];
            _v[32 - i] = -rows[i];
            _v[33 + i] = rows[16 + i];
            _v[63 - i] = rows[16 + i];
        }

        _v[16] = 0;

        // The window, 8 outputs at a time, each lane summing its 16 terms in the definition's order.
        ref double sum = ref MemoryMarshal.GetReference(output);
        for (int j = 0; j < 32; j += 8)
        {
            var s0 = Vector128<double>.Zero;
            var s1 = Vector128<double>.Zero;
            var s2 = Vector128<double>.Zero;
            var s3 = Vector128<double>.Zero;
            for (int i = 0; i < 8; i++)
            {
                ref double u = ref Unsafe.Add(ref v, (128 * i) + j);
                ref double d = ref Unsafe.Add(ref window, (64 * i) + j);
                s0 += Vector128.LoadUnsafe(ref u) * Vector128.LoadUnsafe(ref d);
                s1 += Vector128.LoadUnsafe(ref u, 2) * Vector128.LoadUnsafe(ref d, 2);
                s2 += Vector128.LoadUnsafe(ref u, 4) * Vector128.LoadUnsafe(ref d, 4);
                s3 += Vector128.LoadUnsafe(ref u, 6) * Vector128.LoadUnsafe(ref d, 6);
                s0 += Vector128.LoadUnsafe(ref u, 96) * Vector128.LoadUnsafe(ref d, 32);
                s1 += Vector128.LoadUnsafe(ref u, 98) * Vector128.LoadUnsafe(ref d, 34);
                s2 += Vector128.LoadUnsafe(ref u, 100) * Vector128.LoadUnsafe(ref d, 36);
                s3 += Vector128.LoadUnsafe(ref u, 102) * Vector128.LoadUnsafe(ref d, 38);
            }

            s0.StoreUnsafe(ref sum, (nuint)j);
            s1.StoreUnsafe(ref sum, (nuint)j + 2);
            s2.StoreUnsafe(ref sum, (nuint)j + 4);
            s3.StoreUnsafe(ref sum, (nuint)j + 6);
        }
    }
#endif
}

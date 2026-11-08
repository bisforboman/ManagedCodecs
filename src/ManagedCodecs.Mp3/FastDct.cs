namespace ManagedCodecs.Mp3;

/// <summary>
/// The DCT-II, X[m] = sum over n of x[n] cos((2n + 1) m pi / 2N), by Lee's recursion (B. G. Lee, "A new algorithm to
/// compute the discrete cosine transform", 1984) down to an odd size, which is summed by its definition: N 32 for the
/// synthesis filterbank's matrixing, 18 for the long IMDCT. The even outputs are the DCT of a[n] = x[n] + x[N - 1 - n],
/// the odd ones X[2k + 1] = Y[k] + Y[k + 1] for Y the DCT of b[n] = (x[n] - x[N - 1 - n]) / (2 cos((2n + 1) pi / 2N))
/// (Y[N / 2] = 0). Every factor comes from <see cref="ExactMath"/>, the same bits on every runtime.
/// </summary>
internal static class FastDct
{
    private const int MaxSize = 32;

    // Lee's factors by size n: 1 / (2 cos((2i + 1) pi / 2n)) for i below n / 2.
    private static readonly double[]?[] Factors = BuildFactors();

    // An odd size's cosines by size n: cos((2k + 1) m pi / 2n) at [n m + k].
    private static readonly double[]?[] OddCosines = BuildOddCosines();

    /// <summary>
    /// The DCT-II of <paramref name="x"/> in place: its length 32, 16, 8, 4, 2, 1, 18 or 9. <paramref name="scratch"/>
    /// holds as many values.
    /// </summary>
    public static void Dct(Span<double> x, Span<double> scratch)
    {
        int n = x.Length;
        if ((n & 1) != 0)
        {
            Odd(x, scratch);
            return;
        }

        int half = n / 2;
        double[] factors = Factors[n]!;
        var a = scratch[..half];
        var b = scratch[half..n];
        for (int i = 0; i < half; i++)
        {
            double first = x[i];
            double last = x[n - 1 - i];
            a[i] = first + last;
            b[i] = (first - last) * factors[i];
        }

        // x's values are read: its halves are the scratch of the two half-size DCTs.
        Dct(a, x[..half]);
        Dct(b, x[half..]);
        for (int i = 0; i < half; i++)
        {
            x[2 * i] = a[i];
        }

        for (int i = 0; i < half - 1; i++)
        {
            x[(2 * i) + 1] = b[i] + b[i + 1];
        }

        x[n - 1] = b[half - 1];
    }

    // An odd size (9, or 1) by its definition.
    private static void Odd(Span<double> x, Span<double> scratch)
    {
        int n = x.Length;
        if (n == 1)
        {
            return;
        }

        double[] cosines = OddCosines[n]!;
        x.CopyTo(scratch);
        for (int m = 0; m < n; m++)
        {
            double sum = 0;
            for (int k = 0; k < n; k++)
            {
                sum += scratch[k] * cosines[(n * m) + k];
            }

            x[m] = sum;
        }
    }

    private static double[]?[] BuildFactors()
    {
        var factors = new double[]?[MaxSize + 1];
        foreach (int n in (int[])[2, 4, 8, 16, 32, 18])
        {
            factors[n] = [.. Enumerable.Range(0, n / 2).Select(i => 1 / (2 * ExactMath.CosPi((2 * i) + 1, 2 * n)))];
        }

        return factors;
    }

    private static double[]?[] BuildOddCosines()
    {
        var cosines = new double[]?[MaxSize + 1];
        const int n = 9;
        cosines[n] = [.. Enumerable.Range(0, n * n).Select(i => ExactMath.CosPi(((2 * (i % n)) + 1) * (i / n), 2 * n))];
        return cosines;
    }
}

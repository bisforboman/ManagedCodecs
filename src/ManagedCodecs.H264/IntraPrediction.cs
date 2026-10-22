using System.Numerics;
using System.Runtime.CompilerServices;

namespace ManagedCodecs.H264;

/// <summary>Which neighbouring samples of a block may be used for intra prediction.</summary>
internal readonly record struct IntraNeighbors(bool Left, bool Top, bool TopRight, bool TopLeft);

/// <summary>
/// Intra prediction (H.264 clause 8.3), writing the prediction into the picture plane at the
/// block's position. Neighbours are read from the same plane: the row above and the column
/// to the left of the block, as reconstructed before deblocking.
/// </summary>
internal static class IntraPrediction
{
    /// <summary>Intra_4x4 prediction (8.3.1.2) for the block at <paramref name="offset"/>.</summary>
    [SkipLocalsInit]
    public static void Predict4x4<TPixel>(int mode, TPixel[] plane, int offset, int stride, IntraNeighbors n, int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        // The edge layout of PredictBlock: p[-1, y] for y = 3..0 at e[4..7], p[-1, -1] at e[8],
        // p[x, -1] for x = 0..7 at e[9..16].
        Span<int> e = stackalloc int[25];
        e.Clear();
        if (n.Top)
        {
            for (int x = 0; x < 4; x++)
            {
                e[9 + x] = int.CreateTruncating(plane[offset - stride + x]);
            }

            // Missing top-right samples are replaced by p[3, -1] (8.3.1.2).
            for (int x = 4; x < 8; x++)
            {
                e[9 + x] = n.TopRight ? int.CreateTruncating(plane[offset - stride + x]) : e[12];
            }
        }

        if (n.Left)
        {
            for (int y = 0; y < 4; y++)
            {
                e[7 - y] = int.CreateTruncating(plane[offset + (y * stride) - 1]);
            }
        }

        if (n.TopLeft)
        {
            e[8] = int.CreateTruncating(plane[offset - stride - 1]);
        }

        PredictBlock(4, mode, e, plane, offset, stride, n, bitDepth);
    }

    /// <summary>
    /// Intra_8x8 prediction (8.3.2.2) for the block at <paramref name="offset"/>: the neighbouring
    /// samples are low-pass filtered first (8.3.2.2.1), then predicted from as Intra_4x4 does.
    /// </summary>
    [SkipLocalsInit]
    public static void Predict8x8<TPixel>(int mode, TPixel[] plane, int offset, int stride, IntraNeighbors n, int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        // p[-1, y] for y = 7..-1 then p[x, -1] for x = 0..15: p[-1, y] is e[7 - y], p[x, -1] is e[9 + x].
        Span<int> p = stackalloc int[25];
        Span<int> e = stackalloc int[25];
        p.Clear();
        e.Clear();
        if (n.Top)
        {
            for (int x = 0; x < 8; x++)
            {
                p[9 + x] = int.CreateTruncating(plane[offset - stride + x]);
            }

            // Missing top-right samples are replaced by p[7, -1] (8.3.2.2).
            for (int x = 8; x < 16; x++)
            {
                p[9 + x] = n.TopRight ? int.CreateTruncating(plane[offset - stride + x]) : p[16];
            }
        }

        if (n.Left)
        {
            for (int y = 0; y < 8; y++)
            {
                p[7 - y] = int.CreateTruncating(plane[offset + (y * stride) - 1]);
            }
        }

        if (n.TopLeft)
        {
            p[8] = int.CreateTruncating(plane[offset - stride - 1]);
        }

        // Reference sample filtering (8.3.2.2.1).
        if (n.Top)
        {
            e[9] = n.TopLeft ? (p[8] + (2 * p[9]) + p[10] + 2) >> 2 : ((3 * p[9]) + p[10] + 2) >> 2;
            for (int i = 10; i < 24; i++)
            {
                e[i] = (p[i - 1] + (2 * p[i]) + p[i + 1] + 2) >> 2;
            }

            e[24] = (p[23] + (3 * p[24]) + 2) >> 2;
        }

        if (n.TopLeft)
        {
            e[8] = (n.Top, n.Left) switch
            {
                (true, true) => (p[9] + (2 * p[8]) + p[7] + 2) >> 2,
                (true, false) => ((3 * p[8]) + p[9] + 2) >> 2,
                (false, true) => ((3 * p[8]) + p[7] + 2) >> 2,
                _ => p[8],
            };
        }

        if (n.Left)
        {
            e[7] = n.TopLeft ? (p[8] + (2 * p[7]) + p[6] + 2) >> 2 : ((3 * p[7]) + p[6] + 2) >> 2;
            for (int i = 1; i < 7; i++)
            {
                e[i] = (p[i - 1] + (2 * p[i]) + p[i + 1] + 2) >> 2;
            }

            e[0] = (p[1] + (3 * p[0]) + 2) >> 2;
        }

        PredictBlock(8, mode, e, plane, offset, stride, n, bitDepth);
    }

    /// <summary>
    /// Intra_4x4 or Intra_8x8 prediction of an <paramref name="size"/> x <paramref name="size"/>
    /// block from its neighbouring samples (8.3.1.2.1 to .9, 8.3.2.2.2 to .10, the same equations at
    /// either size): p[-1, y] at e[7 - y] for y = -1..size - 1 and p[x, -1] at e[9 + x] for
    /// x = 0..2 * size - 1. The mode is chosen once for the whole block.
    /// </summary>
    private static void PredictBlock<TPixel>(int size, int mode, ReadOnlySpan<int> e, TPixel[] plane, int offset, int stride, IntraNeighbors n, int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        int last = size - 1;
        switch (mode)
        {
            case 0: // Vertical
                Require(n.Top, mode);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        plane[offset + (y * stride) + x] = TPixel.CreateTruncating(T(e, x));
                    }
                }

                break;

            case 1: // Horizontal
                Require(n.Left, mode);
                for (int y = 0; y < size; y++)
                {
                    plane.AsSpan(offset + (y * stride), size).Fill(TPixel.CreateTruncating(L(e, y)));
                }

                break;

            case 2: // DC
                {
                    int sumTop = 0;
                    int sumLeft = 0;
                    for (int i = 0; i < size; i++)
                    {
                        sumTop += T(e, i);
                        sumLeft += L(e, i);
                    }

                    int log2 = size == 4 ? 2 : 3;
                    int dc = (n.Left, n.Top) switch
                    {
                        (true, true) => (sumTop + sumLeft + size) >> (log2 + 1),
                        (true, false) => (sumLeft + (size >> 1)) >> log2,
                        (false, true) => (sumTop + (size >> 1)) >> log2,
                        _ => 1 << (bitDepth - 1),
                    };

                    for (int y = 0; y < size; y++)
                    {
                        plane.AsSpan(offset + (y * stride), size).Fill(TPixel.CreateTruncating(dc));
                    }

                    break;
                }

            case 3: // Diagonal_Down_Left
                Require(n.Top, mode);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        plane[offset + (y * stride) + x] = TPixel.CreateTruncating(x == last && y == last
                            ? (T(e, (2 * size) - 2) + (3 * T(e, (2 * size) - 1)) + 2) >> 2
                            : Filter3(T(e, x + y), T(e, x + y + 1), T(e, x + y + 2)));
                    }
                }

                break;

            case 4: // Diagonal_Down_Right
                Require(n.Top && n.Left && n.TopLeft, mode);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        plane[offset + (y * stride) + x] = TPixel.CreateTruncating(x > y ? Filter3(T(e, x - y - 2), T(e, x - y - 1), T(e, x - y))
                            : x < y ? Filter3(L(e, y - x - 2), L(e, y - x - 1), L(e, y - x))
                            : Filter3(T(e, 0), T(e, -1), L(e, 0)));
                    }
                }

                break;

            case 5 or 6: // Vertical_Right, Horizontal_Down
                Require(n.Top && n.Left && n.TopLeft, mode);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        plane[offset + (y * stride) + x] = TPixel.CreateTruncating(mode == 5 ? VerticalRight(e, x, y) : HorizontalDown(e, x, y));
                    }
                }

                break;

            case 7: // Vertical_Left
                Require(n.Top, mode);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        plane[offset + (y * stride) + x] = TPixel.CreateTruncating((y & 1) == 0
                            ? (T(e, x + (y >> 1)) + T(e, x + (y >> 1) + 1) + 1) >> 1
                            : Filter3(T(e, x + (y >> 1)), T(e, x + (y >> 1) + 1), T(e, x + (y >> 1) + 2)));
                    }
                }

                break;

            case 8: // Horizontal_Up
                Require(n.Left, mode);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        plane[offset + (y * stride) + x] = TPixel.CreateTruncating(HorizontalUp(e, x, y, last));
                    }
                }

                break;

            default:
                throw new InvalidDataException($"Invalid intra NxN prediction mode {mode}.");
        }
    }

    // Neighbours in PredictBlock's layout: p[x, -1] for x >= -1 and p[-1, y] for y >= -1.
    private static int T(ReadOnlySpan<int> e, int x) => e[9 + x];

    private static int L(ReadOnlySpan<int> e, int y) => e[7 - y];

    private static int Filter3(int a, int b, int c) => (a + (2 * b) + c + 2) >> 2;

    private static int VerticalRight(ReadOnlySpan<int> e, int x, int y)
    {
        int z = (2 * x) - y;
        return z switch
        {
            >= 0 when (z & 1) == 0 => (T(e, x - (y >> 1) - 1) + T(e, x - (y >> 1)) + 1) >> 1,
            >= 0 => Filter3(T(e, x - (y >> 1) - 2), T(e, x - (y >> 1) - 1), T(e, x - (y >> 1))),
            -1 => Filter3(L(e, 0), L(e, -1), T(e, 0)),
            _ => Filter3(L(e, y - (2 * x) - 1), L(e, y - (2 * x) - 2), L(e, y - (2 * x) - 3)),
        };
    }

    private static int HorizontalDown(ReadOnlySpan<int> e, int x, int y)
    {
        int z = (2 * y) - x;
        return z switch
        {
            >= 0 when (z & 1) == 0 => (L(e, y - (x >> 1) - 1) + L(e, y - (x >> 1)) + 1) >> 1,
            >= 0 => Filter3(L(e, y - (x >> 1) - 2), L(e, y - (x >> 1) - 1), L(e, y - (x >> 1))),
            -1 => Filter3(L(e, 0), L(e, -1), T(e, 0)),
            _ => Filter3(T(e, x - (2 * y) - 1), T(e, x - (2 * y) - 2), T(e, x - (2 * y) - 3)),
        };
    }

    /// <summary>Horizontal_Up; <paramref name="last"/> is the block size minus 1, so zHU runs to 2 * last + last.</summary>
    private static int HorizontalUp(ReadOnlySpan<int> e, int x, int y, int last)
    {
        int z = x + (2 * y);
        int edge = (2 * last) - 1; // 5 for 4x4, 13 for 8x8
        return z switch
        {
            _ when z < edge && (z & 1) == 0 => (L(e, y + (x >> 1)) + L(e, y + (x >> 1) + 1) + 1) >> 1,
            _ when z < edge => Filter3(L(e, y + (x >> 1)), L(e, y + (x >> 1) + 1), L(e, y + (x >> 1) + 2)),
            _ when z == edge => (L(e, last - 1) + (3 * L(e, last)) + 2) >> 2,
            _ => L(e, last),
        };
    }

    /// <summary>Intra_16x16 prediction (8.3.3).</summary>
    public static void Predict16x16<TPixel>(int mode, TPixel[] plane, int offset, int stride, IntraNeighbors n, int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel> =>
        PredictBlock(mode, plane, offset, stride, n, width: 16, height: 16, bitDepth);

    /// <summary>Chroma prediction (8.3.4) for one component: 8x8 (4:2:0) or 8x16 (4:2:2, <paramref name="height"/> 16).</summary>
    /// <param name="leftBlocks">
    /// Per row of 4x4 blocks (bit k: rows 4k to 4k + 3), whether its left neighbouring samples may
    /// be used, where that differs from <see cref="IntraNeighbors.Left"/> (MBAFF with constrained
    /// intra prediction): only DC prediction treats the rows apart. -1: as Left says, for all.
    /// </param>
    public static void PredictChroma<TPixel>(int mode, TPixel[] plane, int offset, int stride, IntraNeighbors n, int bitDepth, int leftBlocks = -1, int height = 8)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        // Chroma mode numbers differ from 16x16: 0 DC, 1 Horizontal, 2 Vertical, 3 Plane.
        if (mode == 0)
        {
            PredictChromaDc(plane, offset, stride, n, leftBlocks < 0 ? (n.Left ? 15 : 0) : leftBlocks, height, bitDepth);
            return;
        }

        int lumaStyleMode = mode switch
        {
            1 => 1,
            2 => 0,
            3 => 3,
            _ => throw new InvalidDataException($"Invalid chroma prediction mode {mode}."),
        };
        PredictBlock(lumaStyleMode, plane, offset, stride, n, width: 8, height, bitDepth);
    }

    /// <summary>Vertical (0), Horizontal (1), DC (2, luma only) and Plane (3) for 16x16, 8x8 or 8x16.</summary>
    private static void PredictBlock<TPixel>(int mode, TPixel[] plane, int offset, int stride, IntraNeighbors n, int width, int height, int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        int Top(int x) => int.CreateTruncating(plane[offset - stride + x]);
        int Left(int y) => int.CreateTruncating(plane[offset + (y * stride) - 1]);
        int Corner() => int.CreateTruncating(plane[offset - stride - 1]);

        switch (mode)
        {
            case 0: // Vertical
                Require(n.Top, mode);
                for (int y = 0; y < height; y++)
                {
                    plane.AsSpan(offset - stride, width).CopyTo(plane.AsSpan(offset + (y * stride), width));
                }

                break;

            case 1: // Horizontal
                Require(n.Left, mode);
                for (int y = 0; y < height; y++)
                {
                    plane.AsSpan(offset + (y * stride), width).Fill(TPixel.CreateTruncating(Left(y)));
                }

                break;

            case 2: // DC (16x16 only)
                {
                    int sumTop = 0;
                    int sumLeft = 0;
                    for (int i = 0; i < 16; i++)
                    {
                        sumTop += n.Top ? Top(i) : 0;
                        sumLeft += n.Left ? Left(i) : 0;
                    }

                    int value = (n.Left, n.Top) switch
                    {
                        (true, true) => (sumTop + sumLeft + 16) >> 5,
                        (true, false) => (sumLeft + 8) >> 4,
                        (false, true) => (sumTop + 8) >> 4,
                        _ => 1 << (bitDepth - 1),
                    };

                    for (int y = 0; y < 16; y++)
                    {
                        plane.AsSpan(offset + (y * stride), 16).Fill(TPixel.CreateTruncating(value));
                    }

                    break;
                }

            case 3: // Plane
                {
                    // 16 samples a side weigh the gradient by 5, 8 by 34 (8.3.3.4; 8.3.4.4 with xCF and yCF).
                    Require(n.Top && n.Left && n.TopLeft, mode);
                    int halfW = width / 2;
                    int halfH = height / 2;
                    int TopOrCorner(int x) => x < 0 ? Corner() : Top(x);
                    int LeftOrCorner(int y) => y < 0 ? Corner() : Left(y);

                    int h = 0;
                    int v = 0;
                    for (int i = 0; i < halfW; i++)
                    {
                        h += (i + 1) * (Top(halfW + i) - TopOrCorner(halfW - 2 - i));
                    }

                    for (int i = 0; i < halfH; i++)
                    {
                        v += (i + 1) * (Left(halfH + i) - LeftOrCorner(halfH - 2 - i));
                    }

                    int a = 16 * (Left(height - 1) + Top(width - 1));
                    int b = (((width == 8 ? 34 : 5) * h) + 32) >> 6;
                    int c = (((height == 8 ? 34 : 5) * v) + 32) >> 6;

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int value = (a + (b * (x - halfW + 1)) + (c * (y - halfH + 1)) + 16) >> 5;
                            plane[offset + (y * stride) + x] = TPixel.CreateTruncating(Math.Clamp(value, 0, Samples.Max<TPixel>(bitDepth)));
                        }
                    }

                    break;
                }

            default:
                throw new InvalidDataException($"Invalid Intra16x16 prediction mode {mode}.");
        }
    }

    /// <summary>Chroma DC (8.3.4.1-3): each 4x4 block gets its own average with its own fallbacks.</summary>
    private static void PredictChromaDc<TPixel>(TPixel[] plane, int offset, int stride, IntraNeighbors n, int leftBlocks, int height, int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        for (int yO = 0; yO < height; yO += 4)
        {
            bool left = ((leftBlocks >> (yO / 4)) & 1) != 0;
            for (int xO = 0; xO < 8; xO += 4)
            {
                int sumTop = 0;
                int sumLeft = 0;
                for (int i = 0; i < 4; i++)
                {
                    sumTop += n.Top ? int.CreateTruncating(plane[offset - stride + xO + i]) : 0;
                    sumLeft += left ? int.CreateTruncating(plane[offset + ((yO + i) * stride) - 1]) : 0;
                }

                int value;
                if ((xO == 0 && yO == 0) || (xO > 0 && yO > 0))
                {
                    value = (left, n.Top) switch
                    {
                        (true, true) => (sumTop + sumLeft + 4) >> 3,
                        (true, false) => (sumLeft + 2) >> 2,
                        (false, true) => (sumTop + 2) >> 2,
                        _ => 1 << (bitDepth - 1),
                    };
                }
                else if (xO > 0)
                {
                    value = n.Top ? (sumTop + 2) >> 2 : left ? (sumLeft + 2) >> 2 : 1 << (bitDepth - 1);
                }
                else
                {
                    value = left ? (sumLeft + 2) >> 2 : n.Top ? (sumTop + 2) >> 2 : 1 << (bitDepth - 1);
                }

                for (int y = 0; y < 4; y++)
                {
                    plane.AsSpan(offset + ((yO + y) * stride) + xO, 4).Fill(TPixel.CreateTruncating(value));
                }
            }
        }
    }

    private static void Require(bool available, int mode)
    {
        if (!available)
        {
            throw new InvalidDataException($"Intra prediction mode {mode} uses unavailable neighbours.");
        }
    }
}

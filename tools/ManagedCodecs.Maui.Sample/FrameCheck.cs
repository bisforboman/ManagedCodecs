namespace ManagedCodecs.MauiSample;

/// <summary>
/// The sample's check of itself (<c>--frames</c>): what the platform drew of the view, compared with the decoded frame.
/// The view is drawn at its own size, not the frame's, and scaled with the platform's filter, so pixels can't be
/// compared one for one as mclive-wpf does: the two are cut into a grid of cells and each cell's mean red, green and
/// blue compared, which still fails on red and blue swapped, on rows of the wrong length (a picture sheared) and on a
/// picture missing or cut.
/// </summary>
internal static class FrameCheck
{
    private const int Cells = 8;

    // A cell's mean may differ by this much: scaling filters blur across the cells' edges.
    private const double Tolerance = 10;

    /// <summary>
    /// The first cell drawn unlike the decoded frame (<paramref name="decoded"/>, red, green, blue, alpha), or null; and
    /// how many cells had a mean red unlike their mean blue (a check that can tell the two apart).
    /// </summary>
    public static string? Compare(byte[] decoded, int width, int height, DrawnPicture drawn, ref long cellsRedUnlikeBlue)
    {
        for (int cy = 0; cy < Cells; cy++)
        {
            for (int cx = 0; cx < Cells; cx++)
            {
                var expected = Mean(decoded, width, height, cx, cy);
                var actual = Mean(drawn.Rgba, drawn.Width, drawn.Height, cx, cy);
                if (Math.Abs(expected.R - expected.B) > 2 * Tolerance)
                {
                    cellsRedUnlikeBlue++;
                }

                if (Math.Abs(expected.R - actual.R) > Tolerance || Math.Abs(expected.G - actual.G) > Tolerance || Math.Abs(expected.B - actual.B) > Tolerance)
                {
                    return $"drawn wrong in cell ({cx}, {cy}) of {Cells}x{Cells}: decoded mean R {expected.R:F0} G {expected.G:F0} B {expected.B:F0}, "
                        + $"drawn R {actual.R:F0} G {actual.G:F0} B {actual.B:F0} (drawn {drawn.Width}x{drawn.Height}, decoded {width}x{height})";
                }
            }
        }

        return null;
    }

    // A cell's mean colour, its outermost pixels left out (the filter mixes them with the next cell's).
    private static (double R, double G, double B) Mean(byte[] rgba, int width, int height, int cx, int cy)
    {
        int x0 = (cx * width / Cells) + 1;
        int x1 = ((cx + 1) * width / Cells) - 1;
        int y0 = (cy * height / Cells) + 1;
        int y1 = ((cy + 1) * height / Cells) - 1;
        double r = 0;
        double g = 0;
        double b = 0;
        long n = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = ((y * width) + x) * 4;
                r += rgba[i];
                g += rgba[i + 1];
                b += rgba[i + 2];
                n++;
            }
        }

        return n == 0 ? default : (r / n, g / n, b / n);
    }
}

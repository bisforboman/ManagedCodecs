namespace ManagedCodecs.Mp3;

/// <summary>
/// Joint stereo (ISO/IEC 11172-3 2.4.3.4.9; 13818-3 2.4.3.2): mid/side, and intensity stereo above the right channel's
/// last nonzero band, in the order the lines are coded.
/// </summary>
internal static class Stereo
{
    private static readonly double InverseSqrt2 = Math.Sqrt(0.5);

    // MPEG-1's intensity ratios for is_pos 0 to 6, k = tan(is_pos pi / 12): the left channel's k / (1 + k) and the right's
    // 1 / (1 + k), as sin / (sin + cos) and cos / (sin + cos), which is_pos 6 (k infinite) needs.
    private static readonly double[] Mpeg1Left = [.. Enumerable.Range(0, 7).Select(p => ExactMath.SinPi(p, 12) / (ExactMath.SinPi(p, 12) + ExactMath.CosPi(p, 12)))];
    private static readonly double[] Mpeg1Right = [.. Enumerable.Range(0, 7).Select(p => ExactMath.CosPi(p, 12) / (ExactMath.SinPi(p, 12) + ExactMath.CosPi(p, 12)))];

    /// <summary>
    /// Applies the frame's joint stereo to a granule's two spectra. The bands follow the right channel's granule
    /// (<paramref name="right"/>); its scalefactors are the intensity positions. MPEG-1's position 7 is illegal, and at
    /// the lower rates one at its partition's largest value (<paramref name="illegal"/>): such a band takes mid/side, if
    /// on, as the bands below the intensity bound do. The last long band (21) and short band (12) have no scalefactor:
    /// they take the band below's position; at the lower rates only where that band is in intensity stereo too, and
    /// the centre (position 0: both channels alike) where the right channel codes a value in it, as the ISO/IEC 11172-4
    /// streams l3-test45 and l3-test46 need it. MPEG-1 copies the position either way, as FFmpeg does (the centre there
    /// is up to 1.6e-2 off FFmpeg's output; no ISO stream that main's decoder takes settles it).
    /// </summary>
    public static void Process(FrameHeader header, Granule right, int[] positions, bool[] illegal, double[] left, double[] rightSpectrum)
    {
        if (!header.Intensity)
        {
            if (header.MidSide)
            {
                MidSide(left, rightSpectrum, 0, 576);
            }

            return;
        }

        bool lsf = header.Lsf;
        int intensityScale = right.ScalefacCompress & 1;
        var longBands = Mp3Tables.LongBands[header.RateIndex];
        var shortBands = Mp3Tables.ShortBands[header.RateIndex];
        int longEnd = right.ShortBlocks ? (right.Mixed ? Spectrum.MixedLongBands(lsf) : 0) : 22;
        int shortStart = right.ShortBlocks ? (right.Mixed ? 3 : 0) : 13;

        // Short bands from the top, each window's on its own until the right channel is nonzero in it.
        Span<bool> nonzeroShort = stackalloc bool[3];
        nonzeroShort.Clear();
        int line = 576;
        for (int band = 12; band >= shortStart; band--)
        {
            int width = shortBands[band + 1] - shortBands[band];
            int sfBand = band == 12 ? 11 : band;
            for (int w = 2; w >= 0; w--)
            {
                line -= width;
                if (!nonzeroShort[w] && IsZero(rightSpectrum, line, width))
                {
                    int index = longEnd + (3 * (sfBand - shortStart)) + w;
                    int below = shortBands[12] - shortBands[11];
                    bool centre = lsf && band == 12 && !IsZero(rightSpectrum, (3 * shortBands[11]) + (w * below), below);
                    if (centre
                        ? Intensity(lsf, CentrePosition, false, intensityScale, left, rightSpectrum, line, width)
                        : Intensity(lsf, positions[index], illegal[index], intensityScale, left, rightSpectrum, line, width))
                    {
                        continue;
                    }
                }
                else
                {
                    nonzeroShort[w] = true;
                }

                if (header.MidSide)
                {
                    MidSide(left, rightSpectrum, line, line + width);
                }
            }
        }

        bool nonzero = nonzeroShort[0] || nonzeroShort[1] || nonzeroShort[2];
        for (int band = longEnd - 1; band >= 0; band--)
        {
            int width = longBands[band + 1] - longBands[band];
            line -= width;
            if (!nonzero && IsZero(rightSpectrum, line, width))
            {
                int index = band == 21 ? 20 : band;
                bool centre = lsf && band == 21 && !IsZero(rightSpectrum, longBands[20], longBands[21] - longBands[20]);
                if (centre
                    ? Intensity(lsf, CentrePosition, false, intensityScale, left, rightSpectrum, line, width)
                    : Intensity(lsf, positions[index], illegal[index], intensityScale, left, rightSpectrum, line, width))
                {
                    continue;
                }
            }
            else
            {
                nonzero = true;
            }

            if (header.MidSide)
            {
                MidSide(left, rightSpectrum, line, line + width);
            }
        }
    }

    // The position that gives both channels the same share at the lower rates (io^0).
    private const int CentrePosition = 0;

    // Intensity stereo over lines [start, start + width) from the left channel's values; false for an illegal position.
    private static bool Intensity(bool lsf, int position, bool illegal, int intensityScale, double[] left, double[] right, int start, int width)
    {
        double l;
        double r;
        if (!lsf)
        {
            if (position >= 7)
            {
                return false;
            }

            l = Mpeg1Left[position];
            r = Mpeg1Right[position];
        }
        else
        {
            if (illegal)
            {
                return false;
            }

            // 13818-3: io = 2^(-1/4) (intensity_scale 0) or 2^(-1/2); an odd position scales the left channel by
            // io^((pos + 1) / 2), an even one the right by io^(pos / 2).
            int step = intensityScale == 0 ? 1 : 2;
            l = (position & 1) != 0 ? ExactMath.QuarterPowerOfTwo(-step * ((position + 1) / 2)) : 1;
            r = (position & 1) != 0 ? 1 : ExactMath.QuarterPowerOfTwo(-step * (position / 2));
        }

        for (int i = start; i < start + width; i++)
        {
            double value = left[i];
            left[i] = value * l;
            right[i] = value * r;
        }

        return true;
    }

    private static void MidSide(double[] left, double[] right, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            double m = left[i];
            double s = right[i];
            left[i] = (m + s) * InverseSqrt2;
            right[i] = (m - s) * InverseSqrt2;
        }
    }

    private static bool IsZero(double[] values, int start, int count)
    {
        for (int i = start; i < start + count; i++)
        {
            if (values[i] != 0)
            {
                return false;
            }
        }

        return true;
    }
}

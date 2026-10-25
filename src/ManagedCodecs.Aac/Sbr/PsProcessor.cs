using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace ManagedCodecs.Aac.Sbr;

/// <summary>
/// Parametric stereo (ISO/IEC 14496-3 8.6.4), the baseline decoder with 10 or 20 parameter bands and mixing procedure
/// Ra: one frame of a mono channel's QMF samples (the input of its synthesis bank) becomes a left and a right one.
/// </summary>
/// <remarks>
/// <para>
/// The three lowest QMF bands are split further by the hybrid filterbank (8.6.4.3: 13-tap filters, band 0 into 8
/// complex sub-subbands of which 4 and 5 are added to 3 and 2, bands 1 and 2 into 2 real ones), whose 6-slot delay is
/// met by the 6 slots of those bands the SBR low band holds beyond the frame. A decorrelated signal comes from the mono
/// one (8.6.4.5): through a fractional delay and three all-pass links up to QMF band 22 (fading above band 3), by plain
/// delays of 14 and 1 slots above, ducked where a transient's peak runs above its smoothed energy. Each of 22 groups
/// of (sub-)subbands takes the mixing matrix of its parameter band (8.6.4.6), from the IID's scale factors and the
/// ICC's angle, interpolated over each envelope from the previous one's. The phase parameters (IPD, OPD) are not
/// applied, as in the spec's baseline decoder.
/// </para>
/// <para>
/// Every cos and sin on data-dependent angles goes through <see cref="PsMath"/>, so the output is the same bits on
/// every runtime.
/// </para>
/// </remarks>
internal sealed class PsProcessor
{
    public const int Slots = 32;

    // The SBR low band's look-ahead past the frame, which the hybrid filters reach into.
    public const int LookAhead = 6;

    private const int HybridBands = 12;
    private const int Bins = 20;
    private const int SubQmfGroups = 10;
    private const int Groups = 22;
    private const int AllPassBands = 23;
    private const int DecayCutoff = 3;
    private const double DecaySlope = 0.05;
    private const double PeakDecay = 0.765928338364649;
    private const double EnergySmoothing = 0.25;
    private const double TransientImpact = 1.5;

    // 8.6.4.3: the prototype filters of the 8-band (complex) and 2-band (real) hybrid filters.
    private static readonly double[] Prototype8 =
    [
        0.00746082949812, 0.02270420949825, 0.04546865930473, 0.07266113929591, 0.09885108575264, 0.11793710567217, 0.125,
        0.11793710567217, 0.09885108575264, 0.07266113929591, 0.04546865930473, 0.02270420949825, 0.00746082949812,
    ];

    private static readonly double[] Prototype2 =
    [
        0, 0.01899487526049, 0, -0.07293139167538, 0, 0.30596630545168, 0.5, 0.30596630545168, 0, -0.07293139167538, 0,
        0.01899487526049, 0,
    ];

    // The 8-band filter's modulation, exp(-i 2 pi / 8 (q + 0.5)(n - 6)) by sub-subband q and tap n: of pi/256 times
    // -32 (2q + 1)(n - 6).
    private static readonly double[] ModulationCos = [.. Enumerable.Range(0, 8 * 13).Select(i => QmfTrig.CosPi256(-32 * ((2 * (i / 13)) + 1) * ((i % 13) - 6)))];
    private static readonly double[] ModulationSin = [.. Enumerable.Range(0, 8 * 13).Select(i => QmfTrig.SinPi256(-32 * ((2 * (i / 13)) + 1) * ((i % 13) - 6)))];

    // The groups (8.6.4.6, 20 bands): the first 10 single hybrid sub-subbands (12 of them: band 0's 0 to 7 with 4 and 5
    // unused, band 1's 8 and 9, band 2's 10 and 11), the other 12 ranges of QMF bands; and each group's parameter band.
    private static readonly int[] GroupBorders = [6, 7, 0, 1, 2, 3, 9, 8, 10, 11, 3, 4, 5, 6, 7, 8, 9, 11, 14, 18, 23, 35, 64];
    private static readonly int[] GroupBins = [1, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19];

    // The centre frequency of each hybrid sub-subband, in QMF bands, for the decorrelator's phase factors.
    private static readonly double[] HybridCentres = [0.125, 0.375, 0.625, 0.875, 0, 0, -0.375, -0.125, 1.75, 1.25, 2.25, 2.75];

    // The decorrelator's all-pass links (8.6.4.5): their delays in slots, decay factors and fractional delays.
    private static readonly int[] LinkDelays = [3, 4, 5];
    private static readonly double[] LinkDecays = [0.65143905753106, 0.56471812200776, 0.48954165955695];
    private static readonly double[] LinkFractions = [0.43, 0.75, 0.347];
    private const double Fraction = 0.39;
    private const int AllPassDelay = 2;

    // exp(-i pi q f) for the main fractional delay (q = 0.39) and each link's, by hybrid sub-subband and by QMF band.
    internal static readonly (double[] Re, double[] Im) HybridPhase = Phases(HybridCentres, Fraction);
    internal static readonly (double[] Re, double[] Im) QmfPhase = Phases(QmfCentres(), Fraction);
    private static readonly (double[] Re, double[] Im)[] HybridLinkPhase = [.. LinkFractions.Select(q => Phases(HybridCentres, q))];
    internal static readonly (double[] Re, double[] Im)[] QmfLinkPhase = [.. LinkFractions.Select(q => Phases(QmfCentres(), q))];

    // The IID's scale factors (8.6.4.6.2), sqrt(2 / (1 + c^2)) with c = 10^(IID / 20) for IID index -7 to 7 (coarse)
    // or -15 to 15 (fine), and the ICC's angle alpha = acos(rho) / 2 for index 0 to 7; computed from the spec's IID and
    // rho quantization (in Python), equal to libxaac's float tables (Apache-2.0) to their precision.
    private static readonly double[] ScaleFactors =
    [
        1.4119827837595895, 1.4031381526835982, 1.3868767040496033, 1.348399724926484, 1.291249371100283,
        1.1960374166799286, 1.1073724036232284, 1.0, 0.8796171665524234, 0.7546485923273181, 0.5767799074457465,
        0.42640143271122083, 0.27671828230984213, 0.1766446276671306, 0.07940162697653257,
    ];

    private static readonly double[] FineScaleFactors =
    [
        1.4142064913583157, 1.414191202223636, 1.4141428569978354, 1.413990008594382, 1.4135069854804392,
        1.4119827837595895, 1.4097730226235472, 1.4053947948854497, 1.3967796049840246, 1.380053099678267,
        1.348399724926484, 1.3139201736763084, 1.2643100814965436, 1.1960374166799286, 1.1073724036232284, 1.0,
        0.8796171665524234, 0.7546485923273181, 0.6336560721923238, 0.5230810426754348, 0.42640143271122083,
        0.30895540465964955, 0.22137464873077037, 0.15768788954413976, 0.11198225164224765, 0.07940162697653257,
        0.04469901562676742, 0.02514469318284149, 0.014141428569978354, 0.007952581547309726, 0.004472113594487508,
    ];

    private static readonly double[] Alphas =
    [
        0.0, 0.1784276371622683, 0.2856673302030948, 0.4630723606909662, 0.597163152138217, 0.7853981633974483,
        1.1003085943613728, 1.5707963267948966,
    ];

    // The three hybrid-filtered QMF bands from 6 slots before the frame to 6 after (44 slots, the first 12 the previous
    // frame's last), real and imaginary.
    // A frame's parameters as 20 bands per envelope (one more for variable borders' fill), and the transient ducking
    // factors per slot and parameter band.
    private readonly int[][] _iid = [.. Enumerable.Range(0, PsData.MaxEnvelopes + 1).Select(_ => new int[Bins])];
    private readonly int[][] _icc = [.. Enumerable.Range(0, PsData.MaxEnvelopes + 1).Select(_ => new int[Bins])];
    private readonly double[][] _ratio = Buffers(Slots, Bins);

    private readonly double[][] _bandRe = Buffers(3, Slots + 12);
    private readonly double[][] _bandIm = Buffers(3, Slots + 12);

    // The hybrid sub-subbands of the mono signal, then of the left; of the decorrelated one, then of the right; by slot.
    private readonly double[][] _leftRe = Buffers(Slots, HybridBands);
    private readonly double[][] _leftIm = Buffers(Slots, HybridBands);
    private readonly double[][] _rightRe = Buffers(Slots, HybridBands);
    private readonly double[][] _rightIm = Buffers(Slots, HybridBands);

    // The decorrelator's state: per (sub-)subband, the 2-slot fractional delay and each link's delay line (sharing
    // their indices), and the plain delays of the QMF bands from 23 up (14 or 1 slots, an index each).
    private readonly double[][] _hybridDelayRe = Buffers(AllPassDelay, HybridBands);
    private readonly double[][] _hybridDelayIm = Buffers(AllPassDelay, HybridBands);
    private readonly double[][] _qmfDelayRe = Buffers(14, 64);
    private readonly double[][] _qmfDelayIm = Buffers(14, 64);
    private readonly double[][][] _hybridLinkRe = [.. LinkDelays.Select(d => Buffers(d, HybridBands))];
    private readonly double[][][] _hybridLinkIm = [.. LinkDelays.Select(d => Buffers(d, HybridBands))];
    private readonly double[][][] _qmfLinkRe = [.. LinkDelays.Select(d => Buffers(d, 64))];
    private readonly double[][][] _qmfLinkIm = [.. LinkDelays.Select(d => Buffers(d, 64))];
    private readonly int[] _qmfDelayIndex = new int[64];
    private readonly int[] _linkIndex = new int[3];
    private int _delayIndex;

    // The transient detector's smoothed peak, peak excess and energy, per parameter band.
    private readonly double[] _peakDecay = new double[Bins];
    private readonly double[] _peakDifference = new double[Bins];
    private readonly double[] _energy = new double[Bins];

    // The mixing matrix (h11, h12, h21, h22) the last envelope ended on, per parameter band; and its IID and ICC indices,
    // which a frame without parameters keeps.
    private readonly double[][] _previousH = [new double[Bins], new double[Bins], new double[Bins], new double[Bins]];
    private readonly int[] _lastIid = new int[Bins];
    private readonly int[] _lastIcc = new int[Bins];
    private bool _lastFineIid;

    public PsProcessor()
    {
        Array.Fill(_previousH[0], 1.0);
        Array.Fill(_previousH[1], 1.0);
    }

    /// <summary>
    /// Turns one frame of the mono QMF samples (<paramref name="re"/>, <paramref name="im"/>: 32 slots, bands 0 to 2 with
    /// 6 more after) into the left (in place) and the right (<paramref name="rightRe"/>, <paramref name="rightIm"/>),
    /// with this frame's parameters when <paramref name="hasData"/>, else the last ones; QMF bands from
    /// <paramref name="top"/> up are empty.
    /// </summary>
    public void Apply(PsData data, bool hasData, double[][] re, double[][] im, double[][] rightRe, double[][] rightIm, int top, AacStatistics statistics)
    {
        Span<int> starts = stackalloc int[PsData.MaxEnvelopes + 2];
        var (iid, icc) = (_iid, _icc);
        int envelopes = Envelopes(data, hasData, starts, iid, icc);
        bool fine = hasData && data.EnableIid ? data.FineIid : _lastFineIid;
        if (hasData && data.EnableIcc && data.IccMode >= 3)
        {
            throw new NotSupportedException("Parametric stereo mixing procedure Rb (icc_mode 3 to 5) is not supported.");
        }

        for (int sb = top; sb < 64; sb++)
        {
            ClearDelays(sb);
        }

        HybridAnalysis(re, im);
        Decorrelate(re, im, rightRe, rightIm, statistics);
        Mix(envelopes, starts, iid, icc, fine, re, im, rightRe, rightIm);
        HybridSynthesis(_leftRe, _leftIm, re, im);
        HybridSynthesis(_rightRe, _rightIm, rightRe, rightIm);
        iid[envelopes - 1].CopyTo(_lastIid, 0);
        icc[envelopes - 1].CopyTo(_lastIcc, 0);
        _lastFineIid = fine;
        statistics.PsAppliedFrames++;
    }

    // The frame's envelopes as 20-band parameters and the slots they start at (8.6.4.4): fixed borders split the frame
    // evenly; variable ones end where sent (plus 1), an envelope with the last one's values filling up the frame, made
    // to increase. No parameters (none sent, or none this frame) keep the last values over the whole frame.
    private int Envelopes(PsData data, bool hasData, Span<int> starts, int[][] iid, int[][] icc)
    {
        int count = hasData ? data.NumEnvelopes : 0;
        if (count == 0)
        {
            _lastIid.CopyTo(iid[0], 0);
            _lastIcc.CopyTo(icc[0], 0);
            starts[0] = 0;
            starts[1] = Slots;
            return 1;
        }

        for (int e = 0; e < count; e++)
        {
            for (int b = 0; b < Bins; b++)
            {
                iid[e][b] = data.EnableIid ? data.Iid[e][data.IidBands == 10 ? b / 2 : b] : 0;
                icc[e][b] = data.EnableIcc ? data.Icc[e][data.IccBands == 10 ? b / 2 : b] : 0;
            }
        }

        starts[0] = 0;
        if (!data.VariableBorders)
        {
            for (int e = 1; e <= count; e++)
            {
                starts[e] = Slots * e / count;
            }

            return count;
        }

        for (int e = 0; e < count; e++)
        {
            starts[e + 1] = data.Borders[e] + 1;
        }

        if (starts[count] < Slots)
        {
            iid[count - 1].CopyTo(iid[count], 0);
            icc[count - 1].CopyTo(icc[count], 0);
            count++;
            starts[count] = Slots;
        }

        for (int e = 1; e < count; e++)
        {
            starts[e] = Math.Clamp(starts[e], starts[e - 1] + 1, Slots - count + e);
        }

        return count;
    }

    // 8.6.4.3: the 13-tap filters on the three lowest bands, slot i from their slots i - 6 to i + 6.
    private void HybridAnalysis(double[][] re, double[][] im)
    {
        for (int band = 0; band < 3; band++)
        {
            var bandRe = _bandRe[band];
            var bandIm = _bandIm[band];
            bandRe.AsSpan(Slots, 12).CopyTo(bandRe);
            bandIm.AsSpan(Slots, 12).CopyTo(bandIm);
            for (int n = 0; n < Slots; n++)
            {
                bandRe[12 + n] = re[n + LookAhead][band];
                bandIm[12 + n] = im[n + LookAhead][band];
            }
        }

        Span<double> out8Re = stackalloc double[8];
        Span<double> out8Im = stackalloc double[8];
        for (int i = 0; i < Slots; i++)
        {
            var bandRe = _bandRe[0];
            var bandIm = _bandIm[0];
            for (int q = 0; q < 8; q++)
            {
                double sumRe = 0;
                double sumIm = 0;
                for (int n = 0; n < 13; n++)
                {
                    double xr = bandRe[i + n];
                    double xi = bandIm[i + n];
                    double c = ModulationCos[(13 * q) + n];
                    double s = ModulationSin[(13 * q) + n];
                    sumRe += Prototype8[n] * ((xr * c) - (xi * s));
                    sumIm += Prototype8[n] * ((xi * c) + (xr * s));
                }

                out8Re[q] = sumRe;
                out8Im[q] = sumIm;
            }

            var leftRe = _leftRe[i];
            var leftIm = _leftIm[i];
            out8Re.CopyTo(leftRe);
            out8Im.CopyTo(leftIm);
            leftRe[3] += leftRe[4];
            leftIm[3] += leftIm[4];
            leftRe[2] += leftRe[5];
            leftIm[2] += leftIm[5];
            leftRe[4] = leftIm[4] = leftRe[5] = leftIm[5] = 0;

            for (int band = 1; band < 3; band++)
            {
                double lowRe = 0;
                double lowIm = 0;
                double highRe = 0;
                double highIm = 0;
                for (int n = 0; n < 13; n++)
                {
                    double p = Prototype2[n];
                    double xr = _bandRe[band][i + n] * p;
                    double xi = _bandIm[band][i + n] * p;
                    lowRe += xr;
                    lowIm += xi;
                    highRe += (n & 1) == 0 ? xr : -xr;
                    highIm += (n & 1) == 0 ? xi : -xi;
                }

                int k = 8 + (2 * (band - 1));
                (leftRe[k], leftIm[k], leftRe[k + 1], leftIm[k + 1]) = (lowRe, lowIm, highRe, highIm);
            }
        }
    }

    // 8.6.4.5: the decorrelated signal, into _rightRe/_rightIm (hybrid) and rightRe/rightIm (QMF bands from 3).
    private void Decorrelate(double[][] re, double[][] im, double[][] rightRe, double[][] rightIm, AacStatistics statistics)
    {
        // The transient detector: each parameter band's power by slot, against its smoothed peak.
        var ratio = _ratio;
        Span<double> power = stackalloc double[Bins];
        for (int i = 0; i < Slots; i++)
        {
            power.Clear();
            for (int gr = 0; gr < Groups; gr++)
            {
                int bin = GroupBins[gr];
                if (gr < SubQmfGroups)
                {
                    int sb = GroupBorders[gr];
                    power[bin] += (_leftRe[i][sb] * _leftRe[i][sb]) + (_leftIm[i][sb] * _leftIm[i][sb]);
                }
                else
                {
                    for (int sb = GroupBorders[gr]; sb < GroupBorders[gr + 1]; sb++)
                    {
                        power[bin] += (re[i][sb] * re[i][sb]) + (im[i][sb] * im[i][sb]);
                    }
                }
            }

            for (int bin = 0; bin < Bins; bin++)
            {
                _peakDecay[bin] = Math.Max(_peakDecay[bin] * PeakDecay, power[bin]);
                _peakDifference[bin] += EnergySmoothing * (_peakDecay[bin] - power[bin] - _peakDifference[bin]);
                _energy[bin] += EnergySmoothing * (power[bin] - _energy[bin]);
                bool transient = TransientImpact * _peakDifference[bin] > _energy[bin];
                ratio[i][bin] = transient ? _energy[bin] / (TransientImpact * _peakDifference[bin]) : 1;
                statistics.PsDuckedSlots += transient ? 1 : 0;
            }
        }

        // Slot by slot: the all-pass delay's position and each link's are the same for every subband (each starts from
        // the stored ones and moves one a slot), so a slot's subbands go through in order along contiguous rows. Every
        // (slot, subband) is computed as it would be subband by subband, so the output is the same to the bit.
        Span<int> linkIndex = stackalloc int[3];
        _linkIndex.CopyTo(linkIndex);
        Span<double> allPassRe = stackalloc double[AllPassBands];
        Span<double> allPassIm = stackalloc double[AllPassBands];
        int d = _delayIndex;
        for (int i = 0; i < Slots; i++)
        {
            var hybridDelayRe = _hybridDelayRe[d];
            var hybridDelayIm = _hybridDelayIm[d];
            for (int gr = 0; gr < SubQmfGroups; gr++)
            {
                int sb = GroupBorders[gr];
                var (outRe, outIm) = AllPass(_leftRe[i][sb], _leftIm[i][sb], sb, 1, hybridDelayRe, hybridDelayIm, HybridPhase, _hybridLinkRe, _hybridLinkIm, HybridLinkPhase, linkIndex);
                double duck = ratio[i][GroupBins[gr]];
                _rightRe[i][sb] = duck * outRe;
                _rightIm[i][sb] = duck * outIm;
            }

            var inRe = re[i];
            var inIm = im[i];
            var outRow = rightRe[i];
            var outRowIm = rightIm[i];
            AllPassRow(inRe, inIm, GroupBorders[SubQmfGroups], AllPassBands, _qmfDelayRe[d], _qmfDelayIm[d], _qmfLinkRe, _qmfLinkIm, linkIndex, allPassRe, allPassIm);
            for (int gr = SubQmfGroups; gr < Groups; gr++)
            {
                double duck = ratio[i][GroupBins[gr]];
                for (int sb = GroupBorders[gr]; sb < GroupBorders[gr + 1]; sb++)
                {
                    double outRe;
                    double outIm;
                    if (sb >= AllPassBands)
                    {
                        int index = _qmfDelayIndex[sb];
                        outRe = _qmfDelayRe[index][sb];
                        outIm = _qmfDelayIm[index][sb];
                        _qmfDelayRe[index][sb] = inRe[sb];
                        _qmfDelayIm[index][sb] = inIm[sb];
                        _qmfDelayIndex[sb] = index + 1 >= (sb < 35 ? 14 : 1) ? 0 : index + 1;
                    }
                    else
                    {
                        (outRe, outIm) = (allPassRe[sb], allPassIm[sb]);
                    }

                    outRow[sb] = duck * outRe;
                    outRowIm[sb] = duck * outIm;
                }
            }

            d = d + 1 >= AllPassDelay ? 0 : d + 1;
            for (int link = 0; link < 3; link++)
            {
                linkIndex[link] = linkIndex[link] + 1 >= LinkDelays[link] ? 0 : linkIndex[link] + 1;
            }
        }

        _delayIndex = d;
        linkIndex.CopyTo(_linkIndex);
    }

    // The decay of the all-pass links by QMF subband: 1 up to DecayCutoff, then falling by DecaySlope a subband.
    private static readonly double[] QmfDecay = [.. Enumerable.Range(0, 64).Select(sb => sb <= DecayCutoff ? 1 : Math.Max(0, 1 + (DecayCutoff * DecaySlope) - (DecaySlope * sb)))];

    /// <summary>
    /// The QMF subbands <paramref name="first"/> .. <paramref name="end"/> - 1 of one slot through the all-pass filter
    /// (<see cref="AllPass"/>), into <paramref name="outRe"/> and <paramref name="outIm"/> at the same indices: four
    /// subbands at a time where Vector256 is accelerated, each lane with AllPass's operations in its order (no fused
    /// multiply-adds), so the output is the same to the bit (<c>PsAllPassRow_MatchesScalar</c>); the rest one by one.
    /// </summary>
    internal static void AllPassRow(ReadOnlySpan<double> inRe, ReadOnlySpan<double> inIm, int first, int end, double[] delayRe, double[] delayIm, double[][][] linkRe, double[][][] linkIm, ReadOnlySpan<int> linkIndex, Span<double> outRe, Span<double> outIm)
    {
        int sb = first;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; sb + 4 <= end; sb += 4)
            {
                var delayedRe = Vector256.Create<double>(delayRe.AsSpan(sb));
                var delayedIm = Vector256.Create<double>(delayIm.AsSpan(sb));
                Vector256.Create<double>(inRe[sb..]).CopyTo(delayRe.AsSpan(sb));
                Vector256.Create<double>(inIm[sb..]).CopyTo(delayIm.AsSpan(sb));
                var phaseRe = Vector256.Create<double>(QmfPhase.Re.AsSpan(sb));
                var phaseIm = Vector256.Create<double>(QmfPhase.Im.AsSpan(sb));
                var r = (delayedRe * phaseRe) - (delayedIm * phaseIm);
                var m = (delayedRe * phaseIm) + (delayedIm * phaseRe);
                var decay = Vector256.Create<double>(QmfDecay.AsSpan(sb));
                for (int link = 0; link < 3; link++)
                {
                    var bufRe = linkRe[link][linkIndex[link]].AsSpan(sb);
                    var bufIm = linkIm[link][linkIndex[link]].AsSpan(sb);
                    var lp = QmfLinkPhase[link];
                    var lpRe = Vector256.Create<double>(lp.Re.AsSpan(sb));
                    var lpIm = Vector256.Create<double>(lp.Im.AsSpan(sb));
                    var a = decay * Vector256.Create(LinkDecays[link]);
                    var storedRe = Vector256.Create<double>(bufRe);
                    var storedIm = Vector256.Create<double>(bufIm);
                    var nextRe = (storedRe * lpRe) - (storedIm * lpIm) - (a * r);
                    var nextIm = (storedRe * lpIm) + (storedIm * lpRe) - (a * m);
                    (r + (a * nextRe)).CopyTo(bufRe);
                    (m + (a * nextIm)).CopyTo(bufIm);
                    r = nextRe;
                    m = nextIm;
                }

                r.CopyTo(outRe[sb..]);
                m.CopyTo(outIm[sb..]);
            }
        }

        for (; sb < end; sb++)
        {
            (outRe[sb], outIm[sb]) = AllPass(inRe[sb], inIm[sb], sb, QmfDecay[sb], delayRe, delayIm, QmfPhase, linkRe, linkIm, QmfLinkPhase, linkIndex);
        }
    }

    /// <summary>
    /// One subband's sample through the all-pass filter: the fractional delay (the delay line's row at this slot, and
    /// its phase), then the three links (their rows at <paramref name="linkIndex"/>), each decayed by
    /// <paramref name="decay"/> times its own factor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (double Re, double Im) AllPass(double inRe, double inIm, int sb, double decay, double[] delayRe, double[] delayIm, (double[] Re, double[] Im) phase, double[][][] linkRe, double[][][] linkIm, (double[] Re, double[] Im)[] linkPhase, ReadOnlySpan<int> linkIndex)
    {
        double delayedRe = delayRe[sb];
        double delayedIm = delayIm[sb];
        delayRe[sb] = inRe;
        delayIm[sb] = inIm;
        double r = (delayedRe * phase.Re[sb]) - (delayedIm * phase.Im[sb]);
        double m = (delayedRe * phase.Im[sb]) + (delayedIm * phase.Re[sb]);
        for (int link = 0; link < 3; link++)
        {
            var bufRe = linkRe[link][linkIndex[link]];
            var bufIm = linkIm[link][linkIndex[link]];
            var lp = linkPhase[link];
            double a = decay * LinkDecays[link];
            double storedRe = bufRe[sb];
            double storedIm = bufIm[sb];
            double nextRe = (storedRe * lp.Re[sb]) - (storedIm * lp.Im[sb]) - (a * r);
            double nextIm = (storedRe * lp.Im[sb]) + (storedIm * lp.Re[sb]) - (a * m);
            bufRe[sb] = r + (a * nextRe);
            bufIm[sb] = m + (a * nextIm);
            r = nextRe;
            m = nextIm;
        }

        return (r, m);
    }

    // 8.6.4.6: per envelope, each parameter band's matrix (Ra); per group, the matrix stepped linearly from the previous
    // envelope's to it over the envelope, applied to the mono and the decorrelated signal: left = h11 M + h21 D,
    // right = h12 M + h22 D.
    private void Mix(int envelopes, ReadOnlySpan<int> starts, int[][] iid, int[][] icc, bool fine, double[][] re, double[][] im, double[][] rightRe, double[][] rightIm)
    {
        var scale = fine ? FineScaleFactors : ScaleFactors;
        int steps = fine ? 15 : 7;
        Span<double> h = stackalloc double[4 * Bins];
        for (int e = 0; e < envelopes; e++)
        {
            for (int bin = 0; bin < Bins; bin++)
            {
                double right = scale[steps + iid[e][bin]];
                double left = scale[steps - iid[e][bin]];
                double alpha = Alphas[icc[e][bin]];
                double beta = alpha * (right - left) / Math.Sqrt(2);
                h[bin] = left * PsMath.Cos(beta + alpha);
                h[Bins + bin] = right * PsMath.Cos(beta - alpha);
                h[(2 * Bins) + bin] = left * PsMath.Sin(beta + alpha);
                h[(3 * Bins) + bin] = right * PsMath.Sin(beta - alpha);
            }

            int length = starts[e + 1] - starts[e];
            for (int gr = 0; gr < Groups; gr++)
            {
                int bin = GroupBins[gr];
                bool hybrid = gr < SubQmfGroups;
                int first = GroupBorders[gr];
                int last = hybrid ? first + 1 : GroupBorders[gr + 1];
                double h11 = _previousH[0][bin];
                double h12 = _previousH[1][bin];
                double h21 = _previousH[2][bin];
                double h22 = _previousH[3][bin];
                double d11 = (h[bin] - h11) / length;
                double d12 = (h[Bins + bin] - h12) / length;
                double d21 = (h[(2 * Bins) + bin] - h21) / length;
                double d22 = (h[(3 * Bins) + bin] - h22) / length;
                for (int i = starts[e]; i < starts[e + 1]; i++)
                {
                    h11 += d11;
                    h12 += d12;
                    h21 += d21;
                    h22 += d22;
                    var mRe = hybrid ? _leftRe[i] : re[i];
                    var mIm = hybrid ? _leftIm[i] : im[i];
                    var dRe = hybrid ? _rightRe[i] : rightRe[i];
                    var dIm = hybrid ? _rightIm[i] : rightIm[i];
                    for (int sb = first; sb < last; sb++)
                    {
                        (mRe[sb], dRe[sb]) = ((h11 * mRe[sb]) + (h21 * dRe[sb]), (h12 * mRe[sb]) + (h22 * dRe[sb]));
                        (mIm[sb], dIm[sb]) = ((h11 * mIm[sb]) + (h21 * dIm[sb]), (h12 * mIm[sb]) + (h22 * dIm[sb]));
                    }
                }
            }

            for (int bin = 0; bin < Bins; bin++)
            {
                _previousH[0][bin] = h[bin];
                _previousH[1][bin] = h[Bins + bin];
                _previousH[2][bin] = h[(2 * Bins) + bin];
                _previousH[3][bin] = h[(3 * Bins) + bin];
            }
        }
    }

    // The sub-subbands added back into their QMF bands.
    private static void HybridSynthesis(double[][] hybridRe, double[][] hybridIm, double[][] re, double[][] im)
    {
        for (int i = 0; i < Slots; i++)
        {
            var hr = hybridRe[i];
            var hi = hybridIm[i];
            re[i][0] = hr[0] + hr[1] + hr[2] + hr[3] + hr[6] + hr[7];
            im[i][0] = hi[0] + hi[1] + hi[2] + hi[3] + hi[6] + hi[7];
            re[i][1] = hr[8] + hr[9];
            im[i][1] = hi[8] + hi[9];
            re[i][2] = hr[10] + hr[11];
            im[i][2] = hi[10] + hi[11];
        }
    }

    private void ClearDelays(int sb)
    {
        for (int k = 0; k < 14; k++)
        {
            _qmfDelayRe[k][sb] = _qmfDelayIm[k][sb] = 0;
        }

        for (int link = 0; link < 3; link++)
        {
            for (int k = 0; k < LinkDelays[link]; k++)
            {
                _qmfLinkRe[link][k][sb] = _qmfLinkIm[link][k][sb] = 0;
            }
        }
    }

    private static double[] QmfCentres() => [.. Enumerable.Range(0, 64).Select(k => k + 0.5)];

    private static (double[] Re, double[] Im) Phases(double[] centres, double q) =>
        ([.. centres.Select(f => PsMath.Cos(-Math.PI * q * f))], [.. centres.Select(f => PsMath.Sin(-Math.PI * q * f))]);

    private static double[][] Buffers(int count, int length) => [.. Enumerable.Range(0, count).Select(_ => new double[length])];
}

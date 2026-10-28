using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace ManagedCodecs.Aac.Sbr;

/// <summary>Parametric stereo (ISO/IEC 14496-3 8.6.4): 10, 20 or 34 parameter bands and mixing procedure Ra or Rb: one frame of a mono channel's QMF samples (the input of its synthesis bank) becomes a left and a right one.</summary>
/// <remarks>
/// <para>
/// The three lowest QMF bands are split further by the hybrid filterbank (8.6.4.3: 13-tap filters, band 0 into 8
/// complex sub-subbands of which 4 and 5 are added to 3 and 2, bands 1 and 2 into 2 real ones), whose 6-slot delay is
/// met by the 6 slots of those bands the SBR low band holds beyond the frame. A decorrelated signal comes from the mono
/// one (8.6.4.5): through a fractional delay and three all-pass links up to QMF band 22 (fading above band 3), by plain
/// delays of 14 and 1 slots above, ducked where a transient's peak runs above its smoothed energy. Each of 22 groups
/// of (sub-)subbands takes the mixing matrix of its parameter band (8.6.4.6), from the IID's scale factors and the
/// ICC's angle (Ra) or from the IID and the ICC's rho (Rb), interpolated over each envelope from the previous one's.
/// </para>
/// <para>
/// In 34 bands (a <see cref="Layout"/> of its own) the five lowest QMF bands split instead (8.6.4.3: band 0 into 12, band
/// 1 into 8, bands 2 to 4 into 4 complex sub-subbands), 50 groups share 34 parameter bands, and the all-pass decay starts
/// at band 5. As in FFmpeg, a frame is processed in 34 bands when IID or ICC is sent in 34 (the other mapped up by the
/// spec's index maps), and a switch between 20 and 34 clears the decorrelator and moves the last matrices to the new
/// bands.
/// </para>
/// <para>
/// The phase parameters, when a frame sends them, turn the lowest 11 parameter bands' matrices complex as FFmpeg does
/// (the spec's text was out of reach): each band's OPD and IPD are smoothed over the envelopes (the last three, weighted
/// 1/4, 1/2 and 1, as unit vectors summed and normalized), h11 and h21 turn by the OPD and h12 and h22 by the OPD less
/// the IPD, the imaginary parts are interpolated as the real ones, from the previous envelope's negated in the two
/// sub-subbands of negative frequency, and the left and right become complex products of the mono and decorrelated
/// signals.
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

    // The largest layout's (34 bands') counts, which the buffers take.
    private const int MaxHybridBands = 32;
    private const int MaxBins = 34;
    private const int MaxPhaseBins = 17;
    public const int HybridQmfBands = 5;
    private const int AllPassBands = 23;
    private const int ShortDelayBands = 35;
    private const int LongDelay = 14;
    private const double DecaySlope = 0.05;
    private const double PeakDecay = 0.765928338364649;
    private const double EnergySmoothing = 0.25;
    private const double TransientImpact = 1.5;

    // 8.6.4.3: the prototype filters of 20 bands' 8-band (complex) and 2-band (real) hybrid filters.
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

    // 34 bands' prototypes (8.6.4.3): of the 12-band filter on QMF band 0, the 8-band one on band 1 and the 4-band one
    // on bands 2 to 4; equal to libxaac's (Apache-2.0) and FFmpeg's.
    private static readonly double[] Prototype12 =
    [
        0.04081179924692, 0.03812810994926, 0.05144908135699, 0.06399831151592, 0.07428313801106, 0.08100347892914,
        0.08333333333333, 0.08100347892914, 0.07428313801106, 0.06399831151592, 0.05144908135699, 0.03812810994926,
        0.04081179924692,
    ];

    private static readonly double[] Prototype8Fine =
    [
        0.01565675600122, 0.03752716391991, 0.05417891378782, 0.08417044116767, 0.10307344158036, 0.12222452249753, 0.125,
        0.12222452249753, 0.10307344158036, 0.08417044116767, 0.05417891378782, 0.03752716391991, 0.01565675600122,
    ];

    private static readonly double[] Prototype4 =
    [
        -0.05908211155639, -0.04871498374946, 0, 0.07778723915851, 0.16486303567403, 0.23279856662996, 0.25,
        0.23279856662996, 0.16486303567403, 0.07778723915851, 0, -0.04871498374946, -0.05908211155639,
    ];

    // The 34-band filters: each prototype modulated by exp(-i 2 pi / N (q + 0.5)(n - 6)), by sub-subband q and tap n,
    // through PsMath (the angles are multiples of pi / 12, which QmfTrig's pi / 256 steps don't hold).
    private static readonly (double[] Re, double[] Im) Filter12 = Filters(Prototype12, 12);
    private static readonly (double[] Re, double[] Im) Filter8 = Filters(Prototype8Fine, 8);
    private static readonly (double[] Re, double[] Im) Filter4 = Filters(Prototype4, 4);

    // The 8-band filter's modulation, exp(-i 2 pi / 8 (q + 0.5)(n - 6)) by sub-subband q and tap n: of pi/256 times
    // -32 (2q + 1)(n - 6).
    private static readonly double[] ModulationCos = [.. Enumerable.Range(0, 8 * 13).Select(i => QmfTrig.CosPi256(-32 * ((2 * (i / 13)) + 1) * ((i % 13) - 6)))];
    private static readonly double[] ModulationSin = [.. Enumerable.Range(0, 8 * 13).Select(i => QmfTrig.SinPi256(-32 * ((2 * (i / 13)) + 1) * ((i % 13) - 6)))];

    // The decorrelator's all-pass links (8.6.4.5): their delays in slots, decay factors and fractional delays.
    private static readonly int[] LinkDelays = [3, 4, 5];
    private static readonly double[] LinkDecays = [0.65143905753106, 0.56471812200776, 0.48954165955695];
    private static readonly double[] LinkFractions = [0.43, 0.75, 0.347];
    private const double Fraction = 0.39;
    private const int AllPassDelay = 2;

    // exp(-i pi q f) for the main fractional delay (q = 0.39) and each link's, by QMF band (centred at band + 0.5 in
    // either layout).
    internal static readonly (double[] Re, double[] Im) QmfPhase = Phases(QmfCentres(), Fraction);
    internal static readonly (double[] Re, double[] Im)[] QmfLinkPhase = [.. LinkFractions.Select(q => Phases(QmfCentres(), q))];

    /// <summary>
    /// The 20-band layout (8.6.4.6): the first 10 groups single hybrid sub-subbands (12 of them: band 0's 0 to 7 with 4
    /// and 5 unused, band 1's 8 and 9, band 2's 10 and 11), the other 12 ranges of QMF bands from 3; each group's
    /// parameter band; the sub-subbands' centre frequencies in QMF bands, for the decorrelator's phase factors; the
    /// all-pass decay's cutoff (QMF band 3); and the groups whose phases start negated (0 and 1, of negative frequency).
    /// </summary>
    internal static readonly Layout Layout20 = new(
        bins: 20,
        hybridBands: 12,
        qmfStart: 3,
        subQmfGroups: 10,
        groupBorders: [6, 7, 0, 1, 2, 3, 9, 8, 10, 11, 3, 4, 5, 6, 7, 8, 9, 11, 14, 18, 23, 35, 64],
        groupBins: [1, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19],
        centres: [0.125, 0.375, 0.625, 0.875, 0, 0, -0.375, -0.125, 1.75, 1.25, 2.25, 2.75],
        decayCutoff: 3,
        phaseBins: 11,
        negated: (0, 1));

    /// <summary>
    /// The 34-band layout: the first 32 groups single hybrid sub-subbands (band 0's 12, band 1's 8, bands 2 to 4's 4
    /// each, in the filters' order), the other 18 ranges of QMF bands from 5; each group's parameter band; the
    /// sub-subbands' centre frequencies (FFmpeg's, in 24ths of a QMF band, whose phase factors equal libxaac's 34-band
    /// tables, Apache-2.0); the decay's cutoff (QMF band 5); and the groups whose phases start negated (9 to 13, as
    /// FFmpeg negates them: libxaac's 9 to 11 leaves heaacv2-34-ipd 5e-3 off on average).
    /// </summary>
    internal static readonly Layout Layout34 = new(
        bins: 34,
        hybridBands: 32,
        qmfStart: 5,
        subQmfGroups: 32,
        groupBorders:
        [
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
            30, 31, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 21, 24, 27, 30, 33, 37, 41, 64,
        ],
        groupBins:
        [
            0, 1, 2, 3, 4, 5, 6, 6, 7, 2, 1, 0, 10, 10, 4, 5, 6, 7, 8, 9, 10, 11, 12, 9, 14, 11, 12, 13, 14, 15, 16, 13, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33,
        ],
        centres:
        [
            .. new[]
            {
                2, 6, 10, 14, 18, 22, 26, 30, 34, -10, -6, -2, 51, 57, 15, 21, 27, 33, 39, 45, 54, 66, 78, 42, 102, 66, 78,
                90, 102, 114, 126, 90,
            }.Select(c => c / 24.0),
        ],
        decayCutoff: 5,
        phaseBins: 17,
        negated: (9, 13));

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

    // The ICC's dequantized rho for index 0 to 7 (8.6.4.6.2), for mixing procedure Rb.
    private static readonly double[] Rhos = [1, 0.937, 0.84118, 0.60092, 0.36764, 0, -0.589, -1];

    // The OPD or IPD smoothed over three envelopes (8 x 8 x 8, by the oldest index, the previous and this envelope's): the
    // unit vectors of their angles (k pi / 4) weighted 1/4, 1/2 and 1, summed and normalized.
    private static readonly (double[] Re, double[] Im) SmoothedPhases = SmoothPhases();

    private static readonly double[] Alphas =
    [
        0.0, 0.1784276371622683, 0.2856673302030948, 0.4630723606909662, 0.597163152138217, 0.7853981633974483,
        1.1003085943613728, 1.5707963267948966,
    ];

    // A frame's parameters as 20 bands per envelope (one more for variable borders' fill), and the transient ducking
    // factors per slot and parameter band.
    private readonly int[][] _iid = [.. Enumerable.Range(0, PsData.MaxEnvelopes + 1).Select(_ => new int[MaxBins])];
    private readonly int[][] _icc = [.. Enumerable.Range(0, PsData.MaxEnvelopes + 1).Select(_ => new int[MaxBins])];
    private readonly int[][] _ipd = [.. Enumerable.Range(0, PsData.MaxEnvelopes + 1).Select(_ => new int[MaxPhaseBins])];
    private readonly int[][] _opd = [.. Enumerable.Range(0, PsData.MaxEnvelopes + 1).Select(_ => new int[MaxPhaseBins])];
    private readonly double[][] _ratio = Buffers(Slots, MaxBins);

    // The layout the frame is processed in: 34 bands when the last frame with data sent IID or ICC in 34 (FFmpeg's rule:
    // either makes the whole frame 34 bands), and the one before it, whose difference switches the layout.
    private Layout _layout = Layout20;
    private bool _is34;
    private bool _was34;

    // The five lowest QMF bands (whichever layout splits them) from 6 slots before the frame to 6 after (44 slots, the
    // first 12 the previous frame's last), real and imaginary.
    private readonly double[][] _bandRe = Buffers(HybridQmfBands, Slots + 12);
    private readonly double[][] _bandIm = Buffers(HybridQmfBands, Slots + 12);

    // The hybrid sub-subbands of the mono signal, then of the left; of the decorrelated one, then of the right; by slot.
    private readonly double[][] _leftRe = Buffers(Slots, MaxHybridBands);
    private readonly double[][] _leftIm = Buffers(Slots, MaxHybridBands);
    private readonly double[][] _rightRe = Buffers(Slots, MaxHybridBands);
    private readonly double[][] _rightIm = Buffers(Slots, MaxHybridBands);

    // The decorrelator's state: per (sub-)subband, the 2-slot fractional delay and each link's delay line (sharing
    // their indices), and the plain delays of the QMF bands from 23 up: 14 slots below band 35, one from it up, each
    // band's index moving one a slot from the same start, so one index serves all the 14-slot ones.
    private readonly double[][] _hybridDelayRe = Buffers(AllPassDelay, MaxHybridBands);
    private readonly double[][] _hybridDelayIm = Buffers(AllPassDelay, MaxHybridBands);
    private readonly double[][] _qmfDelayRe = Buffers(LongDelay, 64);
    private readonly double[][] _qmfDelayIm = Buffers(LongDelay, 64);
    private readonly double[][][] _hybridLinkRe = [.. LinkDelays.Select(d => Buffers(d, MaxHybridBands))];
    private readonly double[][][] _hybridLinkIm = [.. LinkDelays.Select(d => Buffers(d, MaxHybridBands))];
    private readonly double[][][] _qmfLinkRe = [.. LinkDelays.Select(d => Buffers(d, 64))];
    private readonly double[][][] _qmfLinkIm = [.. LinkDelays.Select(d => Buffers(d, 64))];
    private int _qmfDelayIndex;
    private readonly int[] _linkIndex = new int[3];
    private int _delayIndex;

    // The transient detector's smoothed peak, peak excess and energy, per parameter band.
    private readonly double[] _peakDecay = new double[MaxBins];
    private readonly double[] _peakDifference = new double[MaxBins];
    private readonly double[] _energy = new double[MaxBins];

    // The mixing matrix (h11, h12, h21, h22) the last envelope ended on, per parameter band; and its IID and ICC indices,
    // which a frame without parameters keeps.
    private readonly double[][] _previousH = Buffers(4, MaxBins);
    private readonly int[] _lastIid = new int[MaxBins];
    private readonly int[] _lastIcc = new int[MaxBins];
    private bool _lastFineIid;

    // The imaginary parts of the matrix the last envelope with phase parameters ended on, its IPD and OPD indices, and
    // the two envelopes' indices before the next one (8 times the older plus the newer), which the smoothing takes.
    private readonly double[][] _previousHIm = Buffers(4, MaxBins);

    // The imaginary parts per envelope slot (1 to the envelope count), as FFmpeg keeps them: written only for the phase
    // bands, so the bands above hold what that slot last held (in 20 bands after 34, the 34-band values of bands 11 to
    // 16, which FFmpeg interpolates towards: without them the heaacv2-34-ipd sample was up to 0.2 off).
    private readonly double[][][] _imSlots = [.. Enumerable.Range(0, 4).Select(_ => Buffers(PsData.MaxEnvelopes + 2, MaxBins))];
    private readonly int[] _lastIpd = new int[MaxPhaseBins];
    private readonly int[] _lastOpd = new int[MaxPhaseBins];
    private readonly int[] _ipdHistory = new int[MaxPhaseBins];
    private readonly int[] _opdHistory = new int[MaxPhaseBins];
    private bool _lastIpdOpd;

    // Mixing procedure Rb (icc_mode 3 to 5), from the last frame whose header enabled ICC.
    private bool _lastRb;

    public PsProcessor()
    {
        Array.Fill(_previousH[0], 1.0);
        Array.Fill(_previousH[1], 1.0);
    }

    /// <summary>Whether the decorrelator holds nothing: its transient detector, fractional delays, links and plain delays (for tests).</summary>
    internal bool DecorrelatorIsClear =>
        _peakDecay.All(v => v == 0) && _peakDifference.All(v => v == 0) && _energy.All(v => v == 0)
        && new[] { _hybridDelayRe, _hybridDelayIm, _qmfDelayRe, _qmfDelayIm }.All(rows => rows.All(row => row.All(v => v == 0)))
        && new[] { _hybridLinkRe, _hybridLinkIm, _qmfLinkRe, _qmfLinkIm }.All(links => links.All(rows => rows.All(row => row.All(v => v == 0))));

    /// <summary>
    /// Turns one frame of the mono QMF samples (<paramref name="re"/>, <paramref name="im"/>: 32 slots, bands 0 to 2 with
    /// 6 more after) into the left (in place) and the right (<paramref name="rightRe"/>, <paramref name="rightIm"/>),
    /// with this frame's parameters when <paramref name="hasData"/>, else the last ones; QMF bands from
    /// <paramref name="top"/> up are empty.
    /// </summary>
    public void Apply(PsData data, bool hasData, double[][] re, double[][] im, double[][] rightRe, double[][] rightIm, int top, AacStatistics statistics)
    {
        if (hasData)
        {
            _was34 = _is34;
            if (data.EnableIid || data.EnableIcc)
            {
                _is34 = (data.EnableIid && data.IidBands == 34) || (data.EnableIcc && data.IccBands == 34);
            }
        }

        if (_is34 != _was34)
        {
            SwitchLayout(_is34);
            statistics.PsLayoutSwitches++;
        }

        _layout = _is34 ? Layout34 : Layout20;
        statistics.Ps34Frames += _is34 ? 1 : 0;
        Span<int> starts = stackalloc int[PsData.MaxEnvelopes + 2];
        var (iid, icc) = (_iid, _icc);
        int envelopes = Envelopes(data, hasData, starts, iid, icc);
        bool fine = hasData && data.EnableIid ? data.FineIid : _lastFineIid;
        bool rb = hasData && data.EnableIcc ? data.IccMode >= 3 : _lastRb;
        bool phases = hasData ? data.EnableIpdOpd : _lastIpdOpd;
        if (rb)
        {
            statistics.PsRbFrames++;
        }

        for (int sb = top; sb < 64; sb++)
        {
            ClearDelays(sb);
        }

        HybridAnalysis(re, im);
        Decorrelate(re, im, rightRe, rightIm, statistics);
        Mix(envelopes, starts, iid, icc, fine, rb, phases, re, im, rightRe, rightIm);
        HybridSynthesis(_layout, _leftRe, _leftIm, re, im);
        HybridSynthesis(_layout, _rightRe, _rightIm, rightRe, rightIm);
        iid[envelopes - 1].CopyTo(_lastIid, 0);
        icc[envelopes - 1].CopyTo(_lastIcc, 0);
        _lastFineIid = fine;
        _lastRb = rb;
        _ipd[envelopes - 1].CopyTo(_lastIpd, 0);
        _opd[envelopes - 1].CopyTo(_lastOpd, 0);
        _lastIpdOpd = phases;
        statistics.PsAppliedFrames++;
    }

    // The frame's envelopes as 20-band parameters and the slots they start at (8.6.4.4): fixed borders split the frame
    // evenly; variable ones end where sent (plus 1), an envelope with the last one's values filling up the frame, made
    // to increase. No parameters (none sent, or none this frame) keep the last values over the whole frame.
    private int Envelopes(PsData data, bool hasData, Span<int> starts, int[][] iid, int[][] icc)
    {
        var layout = _layout;
        int count = hasData ? data.NumEnvelopes : 0;
        if (count == 0)
        {
            _lastIid.CopyTo(iid[0], 0);
            _lastIcc.CopyTo(icc[0], 0);
            _lastIpd.CopyTo(_ipd[0], 0);
            _lastOpd.CopyTo(_opd[0], 0);
            starts[0] = 0;
            starts[1] = Slots;
            return 1;
        }

        for (int e = 0; e < count; e++)
        {
            Map(data.EnableIid ? data.Iid[e] : null, data.IidBands, iid[e], layout.Bins);
            Map(data.EnableIcc ? data.Icc[e] : null, data.IccBands, icc[e], layout.Bins);
            Map(data.Ipd[e], data.PhaseBands, _ipd[e], layout.PhaseBins);
            Map(data.Opd[e], data.PhaseBands, _opd[e], layout.PhaseBins);
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
            _ipd[count - 1].CopyTo(_ipd[count], 0);
            _opd[count - 1].CopyTo(_opd[count], 0);
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
        // All five bands' history, whichever layout splits them, so a switch of layout finds it in place.
        for (int band = 0; band < HybridQmfBands; band++)
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

        if (_layout == Layout34)
        {
            for (int i = 0; i < Slots; i++)
            {
                Filter(_bandRe[0], _bandIm[0], i, Filter12, 12, _leftRe[i], _leftIm[i], 0);
                Filter(_bandRe[1], _bandIm[1], i, Filter8, 8, _leftRe[i], _leftIm[i], 12);
                for (int band = 2; band < HybridQmfBands; band++)
                {
                    Filter(_bandRe[band], _bandIm[band], i, Filter4, 4, _leftRe[i], _leftIm[i], 20 + (4 * (band - 2)));
                }
            }

            return;
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
        var layout = _layout;

        // The transient detector: each parameter band's power by slot, against its smoothed peak.
        var ratio = _ratio;
        Span<double> power = stackalloc double[MaxBins];
        int ducked = 0;
        for (int i = 0; i < Slots; i++)
        {
            power.Clear();
            var hybridRe = _leftRe[i];
            var hybridIm = _leftIm[i];
            var rowRe = re[i];
            var rowIm = im[i];
            for (int gr = 0; gr < layout.Groups; gr++)
            {
                // Each band's sum in subband order, as it was added to power[bin] one by one.
                int bin = layout.GroupBins[gr];
                double sum = power[bin];
                if (gr < layout.SubQmfGroups)
                {
                    int sb = layout.GroupBorders[gr];
                    sum += (hybridRe[sb] * hybridRe[sb]) + (hybridIm[sb] * hybridIm[sb]);
                }
                else
                {
                    for (int sb = layout.GroupBorders[gr]; sb < layout.GroupBorders[gr + 1]; sb++)
                    {
                        sum += (rowRe[sb] * rowRe[sb]) + (rowIm[sb] * rowIm[sb]);
                    }
                }

                power[bin] = sum;
            }

            var slotRatio = ratio[i];
            for (int bin = 0; bin < layout.Bins; bin++)
            {
                double p = power[bin];
                double peak = Math.Max(_peakDecay[bin] * PeakDecay, p);
                double difference = _peakDifference[bin] + (EnergySmoothing * (peak - p - _peakDifference[bin]));
                double energy = _energy[bin] + (EnergySmoothing * (p - _energy[bin]));
                (_peakDecay[bin], _peakDifference[bin], _energy[bin]) = (peak, difference, energy);
                bool transient = TransientImpact * difference > energy;
                slotRatio[bin] = transient ? energy / (TransientImpact * difference) : 1;
                ducked += transient ? 1 : 0;
            }
        }

        statistics.PsDuckedSlots += ducked;

        // Slot by slot: the all-pass delay's position and each link's are the same for every subband (each starts from
        // the stored ones and moves one a slot), so a slot's subbands go through in order along contiguous rows. Every
        // (slot, subband) is computed as it would be subband by subband, so the output is the same to the bit.
        Span<int> linkIndex = stackalloc int[3];
        _linkIndex.CopyTo(linkIndex);
        Span<double> delayedRe = stackalloc double[64];
        Span<double> delayedIm = stackalloc double[64];
        int d = _delayIndex;
        for (int i = 0; i < Slots; i++)
        {
            var hybridDelayRe = _hybridDelayRe[d];
            var hybridDelayIm = _hybridDelayIm[d];
            for (int gr = 0; gr < layout.SubQmfGroups; gr++)
            {
                int sb = layout.GroupBorders[gr];
                var (outRe, outIm) = AllPass(_leftRe[i][sb], _leftIm[i][sb], sb, 1, hybridDelayRe, hybridDelayIm, layout.HybridPhase, _hybridLinkRe, _hybridLinkIm, layout.HybridLinkPhase, linkIndex);
                double duck = ratio[i][layout.GroupBins[gr]];
                _rightRe[i][sb] = duck * outRe;
                _rightIm[i][sb] = duck * outIm;
            }

            var inRe = re[i];
            var inIm = im[i];
            var outRow = rightRe[i];
            var outRowIm = rightIm[i];
            AllPassRow(inRe, inIm, layout.GroupBorders[layout.SubQmfGroups], AllPassBands, layout.QmfDecay, _qmfDelayRe[d], _qmfDelayIm[d], _qmfLinkRe, _qmfLinkIm, linkIndex, delayedRe, delayedIm);

            // The plain delays: what went in 14 slots ago below band 35, one slot ago from it up.
            Delay(inRe, inIm, AllPassBands, ShortDelayBands, _qmfDelayRe[_qmfDelayIndex], _qmfDelayIm[_qmfDelayIndex], delayedRe, delayedIm);
            Delay(inRe, inIm, ShortDelayBands, 64, _qmfDelayRe[0], _qmfDelayIm[0], delayedRe, delayedIm);
            _qmfDelayIndex = _qmfDelayIndex + 1 >= LongDelay ? 0 : _qmfDelayIndex + 1;
            for (int gr = layout.SubQmfGroups; gr < layout.Groups; gr++)
            {
                double duck = ratio[i][layout.GroupBins[gr]];
                for (int sb = layout.GroupBorders[gr]; sb < layout.GroupBorders[gr + 1]; sb++)
                {
                    outRow[sb] = duck * delayedRe[sb];
                    outRowIm[sb] = duck * delayedIm[sb];
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

    // Subbands first .. end - 1 of a plain delay line's row: out what it held, in what comes now.
    private static void Delay(ReadOnlySpan<double> inRe, ReadOnlySpan<double> inIm, int first, int end, double[] rowRe, double[] rowIm, Span<double> outRe, Span<double> outIm)
    {
        rowRe.AsSpan(first..end).CopyTo(outRe[first..end]);
        rowIm.AsSpan(first..end).CopyTo(outIm[first..end]);
        inRe[first..end].CopyTo(rowRe.AsSpan(first..end));
        inIm[first..end].CopyTo(rowIm.AsSpan(first..end));
    }

    /// <summary>The decay of the all-pass links by QMF subband: 1 up to <paramref name="cutoff"/>, then falling by DecaySlope a subband.</summary>
    internal static double[] QmfDecays(int cutoff) => [.. Enumerable.Range(0, 64).Select(sb => sb <= cutoff ? 1 : Math.Max(0, 1 + (cutoff * DecaySlope) - (DecaySlope * sb)))];

    /// <summary>
    /// The QMF subbands <paramref name="first"/> .. <paramref name="end"/> - 1 of one slot through the all-pass filter
    /// (<see cref="AllPass"/>), into <paramref name="outRe"/> and <paramref name="outIm"/> at the same indices: four
    /// subbands at a time where Vector256 is accelerated, each lane with AllPass's operations in its order (no fused
    /// multiply-adds), so the output is the same to the bit (<c>PsAllPassRow_MatchesScalar</c>); the rest one by one.
    /// </summary>
    internal static void AllPassRow(ReadOnlySpan<double> inRe, ReadOnlySpan<double> inIm, int first, int end, double[] decay, double[] delayRe, double[] delayIm, double[][][] linkRe, double[][][] linkIm, ReadOnlySpan<int> linkIndex, Span<double> outRe, Span<double> outIm)
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
                var decays = Vector256.Create<double>(decay.AsSpan(sb));
                for (int link = 0; link < 3; link++)
                {
                    var bufRe = linkRe[link][linkIndex[link]].AsSpan(sb);
                    var bufIm = linkIm[link][linkIndex[link]].AsSpan(sb);
                    var lp = QmfLinkPhase[link];
                    var lpRe = Vector256.Create<double>(lp.Re.AsSpan(sb));
                    var lpIm = Vector256.Create<double>(lp.Im.AsSpan(sb));
                    var a = decays * Vector256.Create(LinkDecays[link]);
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
            (outRe[sb], outIm[sb]) = AllPass(inRe[sb], inIm[sb], sb, decay[sb], delayRe, delayIm, QmfPhase, linkRe, linkIm, QmfLinkPhase, linkIndex);
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

    // 8.6.4.6: per envelope, each parameter band's matrix (Ra, or Rb with <paramref name="rb"/>); per group, the matrix stepped linearly from the previous
    // envelope's to it over the envelope, applied to the mono and the decorrelated signal: left = h11 M + h21 D,
    // right = h12 M + h22 D.
    private void Mix(int envelopes, ReadOnlySpan<int> starts, int[][] iid, int[][] icc, bool fine, bool rb, bool phases, double[][] re, double[][] im, double[][] rightRe, double[][] rightIm)
    {
        var layout = _layout;
        var scale = fine ? FineScaleFactors : ScaleFactors;
        int steps = fine ? 15 : 7;
        Span<double> h = stackalloc double[4 * MaxBins];
        Span<double> hIm = stackalloc double[4 * MaxBins];
        for (int e = 0; e < envelopes; e++)
        {
            for (int bin = 0; bin < layout.Bins; bin++)
            {
                double right = scale[steps + iid[e][bin]];
                if (rb)
                {
                    MatrixRb(right, Rhos[icc[e][bin]], out h[bin], out h[MaxBins + bin], out h[(2 * MaxBins) + bin], out h[(3 * MaxBins) + bin]);
                }
                else
                {
                    double left = scale[steps - iid[e][bin]];
                    double alpha = Alphas[icc[e][bin]];
                    double beta = alpha * (right - left) / Math.Sqrt(2);
                    h[bin] = left * PsMath.Cos(beta + alpha);
                    h[MaxBins + bin] = right * PsMath.Cos(beta - alpha);
                    h[(2 * MaxBins) + bin] = left * PsMath.Sin(beta + alpha);
                    h[(3 * MaxBins) + bin] = right * PsMath.Sin(beta - alpha);
                }

                if (phases && bin >= layout.PhaseBins)
                {
                    // What this slot last held, as FFmpeg interpolates it.
                    for (int m = 0; m < 4; m++)
                    {
                        hIm[(m * MaxBins) + bin] = _imSlots[m][e + 1][bin];
                    }
                }
                else if (phases)
                {
                    // h11 and h21 turned by the smoothed OPD, h12 and h22 by the OPD less the IPD.
                    int opd = (_opdHistory[bin] * 8) + _opd[e][bin];
                    int ipd = (_ipdHistory[bin] * 8) + _ipd[e][bin];
                    _opdHistory[bin] = opd & 63;
                    _ipdHistory[bin] = ipd & 63;
                    double opdRe = SmoothedPhases.Re[opd];
                    double opdIm = SmoothedPhases.Im[opd];
                    double ipdRe = SmoothedPhases.Re[ipd];
                    double ipdIm = SmoothedPhases.Im[ipd];
                    double turnRe = (opdRe * ipdRe) + (opdIm * ipdIm);
                    double turnIm = (opdIm * ipdRe) - (opdRe * ipdIm);
                    for (int m = 0; m < 4; m++)
                    {
                        int i = (m * MaxBins) + bin;
                        (double cos, double sin) = m == 0 || m == 2 ? (opdRe, opdIm) : (turnRe, turnIm);
                        hIm[i] = h[i] * sin;
                        h[i] *= cos;
                        _imSlots[m][e + 1][bin] = hIm[i];
                    }
                }
                else
                {
                    hIm[bin] = hIm[MaxBins + bin] = hIm[(2 * MaxBins) + bin] = hIm[(3 * MaxBins) + bin] = 0;
                }
            }

            int length = starts[e + 1] - starts[e];
            for (int gr = 0; gr < layout.Groups; gr++)
            {
                int bin = layout.GroupBins[gr];
                bool hybrid = gr < layout.SubQmfGroups;
                int first = layout.GroupBorders[gr];
                int last = hybrid ? first + 1 : layout.GroupBorders[gr + 1];
                double h11 = _previousH[0][bin];
                double h12 = _previousH[1][bin];
                double h21 = _previousH[2][bin];
                double h22 = _previousH[3][bin];
                double d11 = (h[bin] - h11) / length;
                double d12 = (h[MaxBins + bin] - h12) / length;
                double d21 = (h[(2 * MaxBins) + bin] - h21) / length;
                double d22 = (h[(3 * MaxBins) + bin] - h22) / length;
                if (!phases)
                {
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

                    continue;
                }

                // The imaginary parts start from the previous envelope's, negated in the sub-subbands of negative
                // frequency (groups 0 and 1), as FFmpeg starts them (it doesn't negate the end).
                double sign = gr >= layout.Negated.First && gr <= layout.Negated.Last ? -1 : 1;
                double g11 = sign * _previousHIm[0][bin];
                double g12 = sign * _previousHIm[1][bin];
                double g21 = sign * _previousHIm[2][bin];
                double g22 = sign * _previousHIm[3][bin];
                double e11 = (hIm[bin] - g11) / length;
                double e12 = (hIm[MaxBins + bin] - g12) / length;
                double e21 = (hIm[(2 * MaxBins) + bin] - g21) / length;
                double e22 = (hIm[(3 * MaxBins) + bin] - g22) / length;
                for (int i = starts[e]; i < starts[e + 1]; i++)
                {
                    h11 += d11;
                    h12 += d12;
                    h21 += d21;
                    h22 += d22;
                    g11 += e11;
                    g12 += e12;
                    g21 += e21;
                    g22 += e22;
                    var mRe = hybrid ? _leftRe[i] : re[i];
                    var mIm = hybrid ? _leftIm[i] : im[i];
                    var dRe = hybrid ? _rightRe[i] : rightRe[i];
                    var dIm = hybrid ? _rightIm[i] : rightIm[i];
                    for (int sb = first; sb < last; sb++)
                    {
                        double mr = mRe[sb];
                        double mi = mIm[sb];
                        double dr = dRe[sb];
                        double di = dIm[sb];
                        mRe[sb] = (h11 * mr) + (h21 * dr) - (g11 * mi) - (g21 * di);
                        mIm[sb] = (h11 * mi) + (h21 * di) + (g11 * mr) + (g21 * dr);
                        dRe[sb] = (h12 * mr) + (h22 * dr) - (g12 * mi) - (g22 * di);
                        dIm[sb] = (h12 * mi) + (h22 * di) + (g12 * mr) + (g22 * dr);
                    }
                }
            }

            for (int bin = 0; bin < layout.Bins; bin++)
            {
                for (int m = 0; m < 4; m++)
                {
                    _previousH[m][bin] = h[(m * MaxBins) + bin];
                    if (phases)
                    {
                        _previousHIm[m][bin] = hIm[(m * MaxBins) + bin];
                    }
                }
            }
        }
    }

    private static (double[] Re, double[] Im) SmoothPhases()
    {
        double half = Math.Sqrt(0.5);
        double[] cos = [1, half, 0, -half, -1, -half, 0, half];
        double[] sin = [0, half, 1, half, 0, -half, -1, -half];
        var re = new double[512];
        var im = new double[512];
        for (int i = 0; i < 512; i++)
        {
            (int oldest, int previous, int current) = (i >> 6, (i >> 3) & 7, i & 7);
            double x = (0.25 * cos[oldest]) + (0.5 * cos[previous]) + cos[current];
            double y = (0.25 * sin[oldest]) + (0.5 * sin[previous]) + sin[current];
            double length = Math.Sqrt((x * x) + (y * y));
            re[i] = x / length;
            im[i] = y / length;
        }

        return (re, im);
    }

    /// <summary>
    /// Mixing procedure Rb's matrix (8.6.4.6.2) for a parameter band: with c = 10^(IID / 20), here from Ra's scale factor
    /// of the right channel (<paramref name="right"/> = sqrt(2 / (1 + c^2))), and the ICC's <paramref name="rho"/>,
    /// alpha = arctan(2 c rho / (c^2 - 1)) / 2 taken in [0, pi / 2], mu = sqrt(1 + (4 rho^2 - 4) / (c + 1 / c)^2) and
    /// gamma = arctan(sqrt((1 - mu) / (1 + mu))): h11 = sqrt(2) cos(alpha) cos(gamma), h12 = sqrt(2) sin(alpha) cos(gamma),
    /// h21 = -sqrt(2) sin(alpha) sin(gamma), h22 = sqrt(2) cos(alpha) sin(gamma). The angles' sines and cosines come by
    /// half-angle identities and square roots alone, which are exact on every platform (CLAUDE.md, HE-AAC: Math's
    /// trigonometry differs in its last bit on WebAssembly). Rho is taken as at least 0.05, as FFmpeg takes it: without
    /// that the Rb sample (heaacv2-rb, its ICC down to index 7, rho -1) was up to 0.3 off FFmpeg's output.
    /// </summary>
    internal static void MatrixRb(double right, double rho, out double h11, out double h12, out double h21, out double h22)
    {
        rho = Math.Max(rho, 0.05);
        double c2 = (2 / (right * right)) - 1;
        double c = Math.Sqrt(c2);

        // 2 alpha as the angle of (c^2 - 1, 2 c rho), turned by pi where it is negative (alpha + pi / 2).
        double x = c2 - 1;
        double y = 2 * c * rho;
        if (y < 0)
        {
            (x, y) = (-x, -y);
        }

        double length = Math.Sqrt((x * x) + (y * y));
        double cosAlpha = 1;
        double sinAlpha = 0;
        if (length > 0)
        {
            double cos2 = x / length;
            double sin2 = y / length;
            if (cos2 >= 0)
            {
                cosAlpha = Math.Sqrt((1 + cos2) / 2);
                sinAlpha = sin2 / (2 * cosAlpha);
            }
            else
            {
                sinAlpha = Math.Sqrt((1 - cos2) / 2);
                cosAlpha = sin2 / (2 * sinAlpha);
            }
        }

        double sum = c + (1 / c);
        double mu = Math.Sqrt(Math.Max(0, 1 + (((4 * rho * rho) - 4) / (sum * sum))));
        double cosGamma = Math.Sqrt((1 + mu) / 2);
        double sinGamma = Math.Sqrt(Math.Max(0, (1 - mu) / 2));
        h11 = Math.Sqrt(2) * cosAlpha * cosGamma;
        h12 = Math.Sqrt(2) * sinAlpha * cosGamma;
        h21 = -Math.Sqrt(2) * sinAlpha * sinGamma;
        h22 = Math.Sqrt(2) * cosAlpha * sinGamma;
    }

    // One slot of a 34-band hybrid filter: the band's slots i to i + 12 through each of its N complex filters, into
    // sub-subbands offset to offset + N - 1.
    private static void Filter(double[] bandRe, double[] bandIm, int i, (double[] Re, double[] Im) filter, int count, double[] outRe, double[] outIm, int offset)
    {
        for (int q = 0; q < count; q++)
        {
            double sumRe = 0;
            double sumIm = 0;
            for (int n = 0; n < 13; n++)
            {
                double xr = bandRe[i + n];
                double xi = bandIm[i + n];
                double c = filter.Re[(13 * q) + n];
                double s = filter.Im[(13 * q) + n];
                sumRe += (xr * c) - (xi * s);
                sumIm += (xi * c) + (xr * s);
            }

            outRe[offset + q] = sumRe;
            outIm[offset + q] = sumIm;
        }
    }

    // A prototype times exp(-i 2 pi / bands (q + 0.5)(n - 6)), by sub-subband q and tap n.
    private static (double[] Re, double[] Im) Filters(double[] prototype, int bands)
    {
        var re = new double[13 * bands];
        var im = new double[13 * bands];
        for (int q = 0; q < bands; q++)
        {
            for (int n = 0; n < 13; n++)
            {
                double theta = -2 * Math.PI * (q + 0.5) * (n - 6) / bands;
                re[(13 * q) + n] = prototype[n] * PsMath.Cos(theta);
                im[(13 * q) + n] = prototype[n] * PsMath.Sin(theta);
            }
        }

        return (re, im);
    }

    /// <summary>
    /// Parameters sent in <paramref name="from"/> bands as <paramref name="to"/> processing bands (20 or 34 for IID and
    /// ICC, 11 or 17 for the phases), by the spec's index maps as FFmpeg applies them: 10 to 20 doubles each band, 10 or
    /// 20 to 34 repeats or averages them (halves rounded towards zero), 34 to 20 averages them; the phases' 5, 11 and 17
    /// take the same maps over their lower bands, the last of 11 or 17 from 0 where 5 bands don't reach it. No values (a
    /// parameter not enabled) give zeros.
    /// </summary>
    internal static void Map(int[]? values, int from, int[] mapped, int to)
    {
        if (values is null || from == 0)
        {
            Array.Clear(mapped, 0, to);
            return;
        }

        // The phases map their lower bands by the IID's maps: 5, 11 and 17 bands as 10, 20 and 34.
        bool full = to is 20 or 34;
        int fromBands = full ? from : from switch { 5 => 10, 11 => 20, _ => 34 };
        int toBands = full ? to : (to == 11 ? 20 : 34);
        if (fromBands == toBands)
        {
            Array.Copy(values, mapped, to);
            return;
        }

        Span<int> m = stackalloc int[MaxBins];
        m.Clear();
        if (toBands == 20 && fromBands == 10)
        {
            for (int b = 0; b < (full ? 10 : 5); b++)
            {
                m[2 * b] = m[(2 * b) + 1] = values[b];
            }
        }
        else if (toBands == 20)
        {
            MapIndex34To20(values, m, full);
        }
        else if (fromBands == 10)
        {
            MapIndex10To34(values, m, full);
        }
        else
        {
            MapIndex20To34(values, m, full);
        }

        m[..to].CopyTo(mapped);
    }

    private static void MapIndex34To20(int[] p, Span<int> m, bool full)
    {
        m[0] = ((2 * p[0]) + p[1]) / 3;
        m[1] = (p[1] + (2 * p[2])) / 3;
        m[2] = ((2 * p[3]) + p[4]) / 3;
        m[3] = (p[4] + (2 * p[5])) / 3;
        m[4] = (p[6] + p[7]) / 2;
        m[5] = (p[8] + p[9]) / 2;
        m[6] = p[10];
        m[7] = p[11];
        m[8] = (p[12] + p[13]) / 2;
        m[9] = (p[14] + p[15]) / 2;
        m[10] = p[16];
        if (full)
        {
            m[11] = p[17];
            m[12] = p[18];
            m[13] = p[19];
            m[14] = (p[20] + p[21]) / 2;
            m[15] = (p[22] + p[23]) / 2;
            m[16] = (p[24] + p[25]) / 2;
            m[17] = (p[26] + p[27]) / 2;
            m[18] = (p[28] + p[29] + p[30] + p[31]) / 4;
            m[19] = (p[32] + p[33]) / 2;
        }
    }

    // Band b of 34 from the 10 (or the phases' 5) bands' indices.
    private static readonly int[] From10 = [0, 0, 0, 1, 1, 1, 2, 2, 2, 2, 3, 3, 4, 4, 4, 4, 5, 5, 6, 6, 7, 7, 7, 7, 8, 8, 8, 8, 9, 9, 9, 9, 9, 9];

    private static void MapIndex10To34(int[] p, Span<int> m, bool full)
    {
        for (int b = 0; b < (full ? 34 : 16); b++)
        {
            m[b] = p[From10[b]];
        }
    }

    // Band b of 34 from the 20 (or the phases' 11) bands' indices: one band, or the half sum of two.
    private static readonly int[] From20 = [0, 0, 1, 2, 2, 3, 4, 4, 5, 5, 6, 7, 8, 8, 9, 9, 10, 11, 12, 13, 14, 14, 15, 15, 16, 16, 17, 17, 18, 18, 18, 18, 19, 19];

    private static void MapIndex20To34(int[] p, Span<int> m, bool full)
    {
        for (int b = 0; b < (full ? 34 : 17); b++)
        {
            m[b] = p[From20[b]];
        }

        m[1] = (p[0] + p[1]) / 2;
        m[4] = (p[2] + p[3]) / 2;
    }

    // A switch between 20 and 34 bands, as FFmpeg makes it: the decorrelator starts over (its transient detector and
    // every delay line cleared), the matrices the last envelope ended on and the phases' history move to the new bands
    // (20 to 34 by repeating and averaging, 34 to 20 by averaging), and the last parameters move with them.
    internal void SwitchLayout(bool to34)
    {
        Array.Clear(_peakDecay);
        Array.Clear(_peakDifference);
        Array.Clear(_energy);
        foreach (var rows in new[] { _hybridDelayRe, _hybridDelayIm, _qmfDelayRe, _qmfDelayIm })
        {
            foreach (var row in rows)
            {
                Array.Clear(row);
            }
        }

        foreach (var links in new[] { _hybridLinkRe, _hybridLinkIm, _qmfLinkRe, _qmfLinkIm })
        {
            foreach (var rows in links)
            {
                foreach (var row in rows)
                {
                    Array.Clear(row);
                }
            }
        }

        for (int m = 0; m < 4; m++)
        {
            MapValues(_previousH[m], to34);
            MapValues(_previousHIm[m], to34);
        }

        Array.Clear(_ipdHistory);
        Array.Clear(_opdHistory);
        int[] copy = new int[MaxBins];
        foreach (var (last, bins) in new[] { (_lastIid, to34 ? 34 : 20), (_lastIcc, to34 ? 34 : 20), (_lastIpd, to34 ? 17 : 11), (_lastOpd, to34 ? 17 : 11) })
        {
            last.CopyTo(copy, 0);
            Map(copy, bins == 34 ? 20 : bins == 20 ? 34 : bins == 17 ? 11 : 17, last, bins);
        }
    }

    // A matrix entry's values per parameter band from 20 bands to 34 (repeated, two averaged) or from 34 to 20 (averaged).
    internal static void MapValues(double[] values, bool to34)
    {
        Span<double> p = stackalloc double[MaxBins];
        values.CopyTo(p);
        if (to34)
        {
            for (int b = 0; b < 34; b++)
            {
                values[b] = p[From20[b]];
            }

            values[1] = (p[0] + p[1]) / 2;
            values[4] = (p[2] + p[3]) / 2;
            return;
        }

        values[0] = ((2 * p[0]) + p[1]) / 3;
        values[1] = (p[1] + (2 * p[2])) / 3;
        values[2] = ((2 * p[3]) + p[4]) / 3;
        values[3] = (p[4] + (2 * p[5])) / 3;
        values[4] = (p[6] + p[7]) / 2;
        values[5] = (p[8] + p[9]) / 2;
        values[6] = p[10];
        values[7] = p[11];
        values[8] = (p[12] + p[13]) / 2;
        values[9] = (p[14] + p[15]) / 2;
        values[10] = p[16];
        values[11] = p[17];
        values[12] = p[18];
        values[13] = p[19];
        values[14] = (p[20] + p[21]) / 2;
        values[15] = (p[22] + p[23]) / 2;
        values[16] = (p[24] + p[25]) / 2;
        values[17] = (p[26] + p[27]) / 2;
        values[18] = (p[28] + p[29] + p[30] + p[31]) / 4;
        values[19] = (p[32] + p[33]) / 2;
    }

    // The sub-subbands added back into their QMF bands.
    private static void HybridSynthesis(Layout layout, double[][] hybridRe, double[][] hybridIm, double[][] re, double[][] im)
    {
        if (layout == Layout34)
        {
            for (int i = 0; i < Slots; i++)
            {
                var hr = hybridRe[i];
                var hi = hybridIm[i];
                int k = 0;
                for (int band = 0; band < HybridQmfBands; band++)
                {
                    int count = band switch { 0 => 12, 1 => 8, _ => 4 };
                    double sumRe = 0;
                    double sumIm = 0;
                    for (int q = 0; q < count; q++, k++)
                    {
                        sumRe += hr[k];
                        sumIm += hi[k];
                    }

                    re[i][band] = sumRe;
                    im[i][band] = sumIm;
                }
            }

            return;
        }

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
        for (int k = 0; k < LongDelay; k++)
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

    /// <summary>
    /// A parameter band layout (20 or 34 bands): the hybrid sub-subbands that replace the lowest QMF bands, the groups of
    /// (sub-)subbands that share a parameter band, and the decorrelator's tables that depend on them.
    /// </summary>
    internal sealed class Layout
    {
        public Layout(int bins, int hybridBands, int qmfStart, int subQmfGroups, int[] groupBorders, int[] groupBins, double[] centres, int decayCutoff, int phaseBins, (int First, int Last) negated)
        {
            Bins = bins;
            HybridBands = hybridBands;
            QmfStart = qmfStart;
            GroupBorders = groupBorders;
            GroupBins = groupBins;
            SubQmfGroups = subQmfGroups;
            HybridPhase = Phases(centres, Fraction);
            HybridLinkPhase = [.. LinkFractions.Select(q => Phases(centres, q))];
            QmfDecay = QmfDecays(decayCutoff);
            PhaseBins = phaseBins;
            Negated = negated;
        }

        /// <summary>Parameter bands: 20 or 34.</summary>
        public int Bins { get; }

        /// <summary>Hybrid sub-subbands, indexed as the groups' first borders give them.</summary>
        public int HybridBands { get; }

        /// <summary>The first QMF band not split into sub-subbands: 3 or 5.</summary>
        public int QmfStart { get; }

        /// <summary>Each hybrid group's sub-subband, then each QMF group's first band and the end (64).</summary>
        public int[] GroupBorders { get; }

        /// <summary>Each group's parameter band.</summary>
        public int[] GroupBins { get; }

        public int Groups => GroupBins.Length;

        /// <summary>The groups of single hybrid sub-subbands, which come first.</summary>
        public int SubQmfGroups { get; }

        public (double[] Re, double[] Im) HybridPhase { get; }

        public (double[] Re, double[] Im)[] HybridLinkPhase { get; }

        public double[] QmfDecay { get; }

        /// <summary>The parameter bands that take phase parameters: 11 or 17.</summary>
        public int PhaseBins { get; }

        /// <summary>The hybrid groups whose phases' imaginary parts start each envelope negated, as FFmpeg starts them.</summary>
        public (int First, int Last) Negated { get; }
    }
}

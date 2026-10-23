namespace ManagedCodecs.Aac.Sbr;

/// <summary>sbr_header (ISO/IEC 14496-3 4.4.2.8, Table 4.63), with the defaults its omitted parts stand for.</summary>
internal sealed record SbrHeader(
    int AmpResolution,
    int StartFrequency,
    int StopFrequency,
    int CrossoverBand,
    int FrequencyScale,
    bool AlterScale,
    int NoiseBands,
    int LimiterBands,
    int LimiterGains,
    bool InterpolateFrequency,
    bool SmoothingMode)
{
    public static SbrHeader Parse(AacBitReader r)
    {
        int ampResolution = r.Read(1);
        int startFrequency = r.Read(4);
        int stopFrequency = r.Read(4);
        int crossoverBand = r.Read(3);
        r.Read(2); // bs_reserved
        bool extra1 = r.ReadFlag();
        bool extra2 = r.ReadFlag();
        int frequencyScale = 2;
        bool alterScale = true;
        int noiseBands = 2;
        if (extra1)
        {
            frequencyScale = r.Read(2);
            alterScale = r.ReadFlag();
            noiseBands = r.Read(2);
        }

        int limiterBands = 2;
        int limiterGains = 2;
        bool interpolateFrequency = true;
        bool smoothingMode = true;
        if (extra2)
        {
            limiterBands = r.Read(2);
            limiterGains = r.Read(2);
            interpolateFrequency = r.ReadFlag();
            smoothingMode = r.ReadFlag();
        }

        return new SbrHeader(ampResolution,
            startFrequency,
            stopFrequency,
            crossoverBand,
            frequencyScale,
            alterScale,
            noiseBands,
            limiterBands,
            limiterGains,
            interpolateFrequency,
            smoothingMode);
    }

    /// <summary>Whether <paramref name="other"/> changes what the frequency tables come from, which resets SBR (4.6.18.3.1).</summary>
    public bool NeedsReset(SbrHeader? other) =>
        other is null || (StartFrequency, StopFrequency, CrossoverBand, FrequencyScale, AlterScale, NoiseBands)
            != (other.StartFrequency, other.StopFrequency, other.CrossoverBand, other.FrequencyScale, other.AlterScale, other.NoiseBands);
}

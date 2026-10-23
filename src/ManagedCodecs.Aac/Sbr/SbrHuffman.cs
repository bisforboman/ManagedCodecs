namespace ManagedCodecs.Aac.Sbr;

/// <summary>One of SBR's Huffman codebooks (Annex 4.A): signed deltas, stored by index from the most negative.</summary>
internal sealed class SbrHuffman
{
    public static readonly SbrHuffman EnvelopeTime15 = new(SbrTables.EnvelopeTime15);
    public static readonly SbrHuffman EnvelopeFrequency15 = new(SbrTables.EnvelopeFrequency15);
    public static readonly SbrHuffman BalanceTime15 = new(SbrTables.BalanceTime15);
    public static readonly SbrHuffman BalanceFrequency15 = new(SbrTables.BalanceFrequency15);
    public static readonly SbrHuffman EnvelopeTime30 = new(SbrTables.EnvelopeTime30);
    public static readonly SbrHuffman EnvelopeFrequency30 = new(SbrTables.EnvelopeFrequency30);
    public static readonly SbrHuffman BalanceTime30 = new(SbrTables.BalanceTime30);
    public static readonly SbrHuffman BalanceFrequency30 = new(SbrTables.BalanceFrequency30);
    public static readonly SbrHuffman NoiseTime = new(SbrTables.NoiseTime);
    public static readonly SbrHuffman NoiseBalanceTime = new(SbrTables.NoiseBalanceTime);

    private readonly Vlc _vlc;
    private readonly int _largest;

    internal SbrHuffman(string[] codes)
    {
        _vlc = new Vlc(codes);
        _largest = codes.Length / 2;
    }

    public int Read(AacBitReader r) => r.Read(_vlc) - _largest;
}

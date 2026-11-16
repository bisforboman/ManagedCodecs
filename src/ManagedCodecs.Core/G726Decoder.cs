namespace ManagedCodecs;

/// <summary>
/// Decodes ITU-T G.726 ADPCM (16, 24, 32 and 40 kbit/s: codes of 2, 3, 4 and 5 bits, one channel) to 32-bit float
/// samples, x / 8192 of the Recommendation's reconstructed signal limited to 14 bits: <see cref="CodecIds.AdpcmG726"/>
/// (codes packed from each byte's most significant bit: the WAVE format tag 0x45 as FFmpeg writes it, RTP's
/// AAL2-G726-*) and <see cref="CodecIds.AdpcmG726Le"/> (from the least significant bit: RTP's G726-*, RFC 3551).
/// </summary>
/// <remarks>
/// <para>
/// The stream's CodecPrivate is one byte, the bits a code (2 to 5; 4, the 32 kbit/s of G.721, when it is empty): the
/// WAVEFORMATEX's wBitsPerSample, or the rate in an RTP payload's name over 8. A packet of B bytes holds
/// floor(8 * B / bits) codes, each packet's first code starting at its first byte; bits after the last whole code are
/// ignored. The decoder adapts from code to code: it carries its state from packet to packet, so packets are decoded in
/// order, and after a lost packet or a seek the sound converges again within a few hundred samples.
/// </para>
/// <para>
/// Implemented from the Recommendation's algorithm (G.726's computational details: the fixed-point arithmetic of each
/// block, whose names the comments give, and its tables), read with the Sun Microsystems g72x reference code (public
/// domain) as the cross-check, and proven on the Recommendation's digital test sequences (its Appendix II: every
/// decoder sequence, at each rate, through the G.711 outputs). It is exact to the Recommendation, not to FFmpeg, whose
/// adpcm_g726 decoder gives other samples (docs/decisions/2026-11-15-g726.md).
/// </para>
/// </remarks>
public sealed class G726Decoder : IAudioDecoder
{
    // The Recommendation's tables for each rate, by the code's magnitude |I| (a code is a sign bit and |I|; a negative
    // code holds |I| inverted): the quantizer's normalized output levels DQLN (log2 in 1/128; -2048 gives 0), the scale
    // factor multipliers W (in 1/16) and the speed control's F.
    private static ReadOnlySpan<short> Levels16 => [116, 365];

    private static ReadOnlySpan<short> Levels24 => [-2048, 135, 273, 373];

    private static ReadOnlySpan<short> Levels32 => [-2048, 4, 135, 213, 273, 323, 373, 425];

    private static ReadOnlySpan<short> Levels40 => [-2048, -66, 28, 104, 169, 224, 274, 318, 358, 395, 429, 459, 488, 514, 539, 566];

    private static ReadOnlySpan<short> Multipliers16 => [-22, 439];

    private static ReadOnlySpan<short> Multipliers24 => [-4, 30, 137, 582];

    private static ReadOnlySpan<short> Multipliers32 => [-12, 18, 41, 64, 112, 198, 355, 1122];

    private static ReadOnlySpan<short> Multipliers40 => [14, 14, 24, 39, 40, 41, 58, 100, 141, 179, 219, 280, 358, 440, 529, 696];

    private static ReadOnlySpan<byte> Speeds16 => [0, 7];

    private static ReadOnlySpan<byte> Speeds24 => [0, 1, 2, 7];

    private static ReadOnlySpan<byte> Speeds32 => [0, 0, 0, 1, 1, 1, 3, 7];

    private static ReadOnlySpan<byte> Speeds40 => [0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 3, 4, 5, 6, 6];

    // A float of the predictor's (FLOATA, FLOATB): a sign bit, a 4-bit exponent and a 6-bit mantissa; this one is 0.
    private const int FloatZero = 32;
    private const int FloatSign = 1 << 10;

    private readonly int _bits;
    private readonly bool _leastSignificantFirst;
    private readonly Rational _timeBase;

    // The state after a reset (the Recommendation's optional reset values): the predictor's coefficients (a1 and a2 for
    // its two poles, b1 to b6 for its six zeros, 16-bit, 1 as 16384) and its signals as floats (the last six quantized
    // differences DQ, the last two reconstructed signals SR); the signs PK of the last two DQ + SEZ; the tone detector's
    // TD; the scale factor's fast and slow parts YU and YL; the speed control's averages DMS and DML and its AP.
    private readonly int[] _b = new int[6];
    private readonly int[] _dq = [FloatZero, FloatZero, FloatZero, FloatZero, FloatZero, FloatZero];
    private int _a1;
    private int _a2;
    private int _sr1 = FloatZero;
    private int _sr2 = FloatZero;
    private int _pk1;
    private int _pk2;
    private int _td;
    private int _yu = 544;
    private int _yl = 34816;
    private int _dms;
    private int _dml;
    private int _ap;

    // Samples still to drop at the start of the next packets, from a Packet.SkipSamples larger than its packet's output.
    private int _pendingSkip;

    /// <summary>A decoder for an audio stream: its codec, its rate, its time base, and the bits a code as its CodecPrivate's byte.</summary>
    /// <exception cref="NotSupportedException">
    /// The codec is not <see cref="CodecIds.AdpcmG726"/> or <see cref="CodecIds.AdpcmG726Le"/>, the stream has more than
    /// one channel, or its codes are not of 2 to 5 bits.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The rate is not positive.</exception>
    public G726Decoder(AudioStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Codec is not (CodecIds.AdpcmG726 or CodecIds.AdpcmG726Le))
        {
            throw new NotSupportedException($"No G.726 decoder for {stream.Codec}.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stream.SampleRate);
        if (stream.Channels != 1)
        {
            throw new NotSupportedException($"G.726 has one channel, not {stream.Channels}.");
        }

        _bits = stream.CodecPrivate.IsEmpty ? 4 : stream.CodecPrivate.Span[0];
        if (_bits is < 2 or > 5)
        {
            throw new NotSupportedException($"G.726 has codes of 2 to 5 bits (16 to 40 kbit/s), not {_bits}.");
        }

        _leastSignificantFirst = stream.Codec == CodecIds.AdpcmG726Le;
        SampleRate = stream.SampleRate;
        _timeBase = stream.TimeBase;
    }

    /// <summary>The sample rate, as the stream gives it (G.726's own is 8000).</summary>
    public int SampleRate { get; }

    /// <summary>Always 1.</summary>
    public int Channels => 1;

    /// <summary>Decodes one packet's codes, floor(8 * bytes / bits) of them, on from the state the last packet left.</summary>
    public AudioFrame Decode(ReadOnlySpan<byte> packet)
    {
        int count = (int)((long)packet.Length * 8 / _bits);
        var frame = new AudioFrame(SampleRate, 1, count);
        var output = frame.Samples[0];
        int mask = (1 << _bits) - 1;
        int buffer = 0;
        int held = 0;
        int at = 0;
        for (int i = 0; i < count; i++)
        {
            int code;
            if (_leastSignificantFirst)
            {
                // RFC 3551, 4.5.4: the first code in the byte's least significant bits, a code split over two bytes
                // with its low bits in the first.
                if (held < _bits)
                {
                    buffer |= packet[at++] << held;
                    held += 8;
                }

                code = buffer & mask;
                buffer >>= _bits;
            }
            else
            {
                if (held < _bits)
                {
                    buffer = ((buffer << 8) | packet[at++]) & 0xFFFF;
                    held += 8;
                }

                code = (buffer >> (held - _bits)) & mask;
            }

            held -= _bits;

            // The output limiting of the Recommendation's uniform PCM interface (14 bits), as COMPRESS limits SR for G.711.
            output[i] = Math.Clamp(DecodeCode(code), -8192, 8191) / 8192f;
        }

        return frame;
    }

    /// <inheritdoc/>
    public AudioFrame Decode(Packet packet)
    {
        var frame = Decode(packet.Data.Span);
        int dropped;
        if (packet.Discard)
        {
            dropped = frame.SampleCount;
        }
        else
        {
            int skip = _pendingSkip + Math.Max(0, packet.SkipSamples);
            dropped = Math.Min(skip, frame.SampleCount);
            _pendingSkip = skip - dropped;
        }

        var output = frame.Drop(dropped, packet.Discard ? 0 : packet.SkipEndSamples);
        output.Pts = packet.Pts + SamplesToTime(dropped);
        return output;
    }

    // One code to the reconstructed signal SR, a 16-bit two's complement value in the scale of 14-bit uniform PCM (which
    // it can overshoot), and the state's adaptation for the next code.
    private int DecodeCode(int code)
    {
        // The adaptive predictor's estimate (FMULT, ACCUM): the six zeros' part SEZ, and with the two poles SE. The
        // sums wrap in 16 bits, as the Recommendation's do.
        int sezi = 0;
        for (int i = 0; i < _b.Length; i++)
        {
            sezi += Multiply(_b[i] >> 2, _dq[i]);
        }

        int sei = sezi + Multiply(_a1 >> 2, _sr1) + Multiply(_a2 >> 2, _sr2);
        int sez = (short)sezi >> 1;
        int se = (short)sei >> 1;

        // The scale factor (LIMA, MIX): between the slow YL and the fast YU by the speed control's AL, AP limited to 1
        // (256 >> 2); the difference's product rounds toward zero.
        int y = _yu;
        if (_ap < 256)
        {
            y = _yl >> 6;
            int difference = _yu - y;
            int product = (Math.Abs(difference) * (_ap >> 2)) >> 6;
            y += difference < 0 ? -product : product;
        }

        // The inverse adaptive quantizer (RECONST, ADDA, ANTILOG): the code's level plus the scale factor, out of the
        // log domain: the quantized difference DQ, as a sign and a magnitude.
        bool negative = (code >> (_bits - 1)) != 0;
        int magnitude = negative ? ~code & ((1 << (_bits - 1)) - 1) : code;
        int dql = Levels[magnitude] + (y >> 2);
        int dq = dql < 0 ? 0 : ((128 + (dql & 127)) << 7) >> (14 - ((dql >> 7) & 15));

        // The reconstructed signal (ADDB), and DQ + SEZ for the poles' adaptation (ADDC), both wrapping in 16 bits.
        int signedDq = negative ? -dq : dq;
        int sr = (short)(se + signedDq);
        int dqsez = (short)(sez + signedDq);

        Adapt(y, magnitude, dq, negative, sr, dqsez);
        return sr;
    }

    private ReadOnlySpan<short> Levels => _bits switch { 2 => Levels16, 3 => Levels24, 4 => Levels32, _ => Levels40 };

    private ReadOnlySpan<short> Multipliers => _bits switch { 2 => Multipliers16, 3 => Multipliers24, 4 => Multipliers32, _ => Multipliers40 };

    private ReadOnlySpan<byte> Speeds => _bits switch { 2 => Speeds16, 3 => Speeds24, 4 => Speeds32, _ => Speeds40 };

    // The state for the next code, from this one's scale factor y, magnitude |I|, quantized difference (dq, negative),
    // reconstructed signal sr and dqsez = DQ + SEZ.
    private void Adapt(int y, int magnitude, int dq, bool negative, int sr, int dqsez)
    {
        int pk0 = dqsez < 0 ? 1 : 0;

        // The transition detector (TRANS): a partial band signal (TD, a tone) whose difference jumps past 24 * 2^yl
        // (3/4 of a threshold from YL's integer and fraction, capped where YL's integer part is past 9).
        int ylInteger = _yl >> 15;
        int threshold = ylInteger > 9 ? 31 << 10 : (32 + ((_yl >> 10) & 31)) << ylInteger;
        bool transition = _td == 1 && dq > (threshold + (threshold >> 1)) >> 1;

        // The scale factor's adaptation (FUNCTW, FILTD, LIMB, FILTE): the fast part toward the code's multiplier with
        // a time constant of 32 samples, kept in 544..5120, and the slow part toward the fast one with 64.
        _yu = Math.Clamp(y + (((Multipliers[magnitude] << 5) - y) >> 5), 544, 5120);
        _yl += _yu + (-_yl >> 6);

        int a2;
        if (transition)
        {
            // TRIGB: a transition resets the predictor's coefficients.
            _a1 = 0;
            _a2 = 0;
            _b.AsSpan().Clear();
            a2 = 0;
        }
        else
        {
            // The second pole (UPA2, LIMC): leaking by 1/128, moved by the signs of this dqsez and the one two back
            // (128) and by the first pole's coefficient limited to ±8191, over 32 (so -256..255), toward this dqsez's
            // and the last one's signs; nothing moves it when dqsez is 0. Kept in ±0.75 (12288).
            int same1 = pk0 ^ _pk1;
            a2 = _a2 - (_a2 >> 7);
            if (dqsez != 0)
            {
                int f = same1 != 0 ? _a1 : -_a1;
                a2 += f < -8191 ? -256 : f > 8191 ? 255 : f >> 5;
                a2 = Math.Clamp(a2 + ((pk0 ^ _pk2) != 0 ? -128 : 128), -12288, 12288);
            }

            _a2 = a2;

            // The first pole (UPA1, LIMD): leaking by 1/256, moved by 192 by the signs of this dqsez and the last
            // one, kept within ±(15360 - a2).
            _a1 -= _a1 >> 8;
            if (dqsez != 0)
            {
                _a1 += same1 != 0 ? -192 : 192;
            }

            int limit = 15360 - a2;
            _a1 = Math.Clamp(_a1, -limit, limit);

            // The zeros (UPB): leaking by 1/256 (1/512 at 40 kbit/s), moved by 128 by the signs of this difference and
            // each of the last six, unless this one is 0. Nothing limits them but their 16 bits, which wrap.
            int leak = _bits == 5 ? 9 : 8;
            for (int i = 0; i < _b.Length; i++)
            {
                int b = _b[i] - (_b[i] >> leak);
                if (dq != 0)
                {
                    b += negative == ((_dq[i] & FloatSign) != 0) ? 128 : -128;
                }

                _b[i] = (short)b;
            }
        }

        // The differences' delay line, the newest as a float (FLOATA).
        for (int i = _dq.Length - 1; i > 0; i--)
        {
            _dq[i] = _dq[i - 1];
        }

        _dq[0] = ToFloat(dq, negative);

        // The reconstructed signals' (FLOATB): -32768 has no magnitude in 15 bits and becomes a negative 0.
        _sr2 = _sr1;
        _sr1 = sr < 0 ? ToFloat(-sr & 0x7FFF, true) : ToFloat(sr, false);

        _pk2 = _pk1;
        _pk1 = pk0;

        // The tone detector (TONE, TRIGB): a second pole below -0.71875 (which a transition has just reset to 0).
        _td = a2 < -11776 ? 1 : 0;

        // The speed control (FILTA, FILTB, SUBTC, FILTC, TRIGA): the short and long averages of F(|I|); AP moves
        // toward 2 (512) while they differ by an eighth of the long one or more, the scale factor is under 3 (1536)
        // or a tone is there, else toward 0; a transition sets it to 1.
        int f512 = Speeds[magnitude] << 9;
        _dms += (f512 - _dms) >> 5;
        _dml += ((f512 << 2) - _dml) >> 7;
        if (transition)
        {
            _ap = 256;
        }
        else if (y < 1536 || _td == 1 || Math.Abs((_dms << 2) - _dml) >= _dml >> 3)
        {
            _ap += (512 - _ap) >> 4;
        }
        else
        {
            _ap += -_ap >> 4;
        }
    }

    // FLOATA and FLOATB: a magnitude (15 bits at most) as a 4-bit exponent, its bit length, and its top 6 bits as the
    // mantissa (0 as exponent 0 and mantissa 32), under a sign.
    private static int ToFloat(int magnitude, bool negative)
    {
        int exponent = BitLength(magnitude);
        int mantissa = magnitude == 0 ? 32 : (magnitude << 6) >> exponent;
        return (negative ? FloatSign : 0) | (exponent << 6) | mantissa;
    }

    // FMULT: a coefficient (16-bit, here already over 4) times a float signal, in the float's 6-bit precision: the
    // coefficient's 13-bit magnitude as a float, the mantissas' product rounded (+48, over 16), and the result back as
    // a 15-bit magnitude, bits past it lost.
    private static int Multiply(int coefficient, int signal)
    {
        int magnitude = coefficient < 0 ? -coefficient & 0x1FFF : coefficient;
        int exponent = BitLength(magnitude);
        int mantissa = magnitude == 0 ? 32 : (magnitude << 6) >> exponent;
        int productExponent = exponent + ((signal >> 6) & 15);
        int productMantissa = ((mantissa * (signal & 63)) + 48) >> 4;
        int product = productExponent <= 26
            ? (productMantissa << 7) >> (26 - productExponent)
            : ((productMantissa << 7) << (productExponent - 26)) & 0x7FFF;
        return (coefficient < 0) != ((signal & FloatSign) != 0) ? -product : product;
    }

    // The bits a value up to 15 bits takes: 0 for 0, 15 for 16384 and more.
    private static int BitLength(int value)
    {
        int length = 0;
        while (value != 0)
        {
            length++;
            value >>= 1;
        }

        return length;
    }

    // Output samples as a duration in the packets' time base, rounded to nearest (halves away from zero).
    private long SamplesToTime(int samples)
    {
        if (samples == 0)
        {
            return 0;
        }

        long numerator = (long)samples * _timeBase.Denominator;
        long denominator = (long)SampleRate * _timeBase.Numerator;
        return (numerator + (denominator / 2)) / denominator;
    }
}

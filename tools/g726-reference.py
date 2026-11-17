#!/usr/bin/env python3
"""ITU-T G.726 ADPCM (16, 24, 32 and 40 kbit/s) in plain integers: the reference G726Decoder's samples are checked
against, and the encoder behind the wav-g726-itu samples (docs/decisions/2026-11-15-g726.md). FFmpeg can't be either:
its adpcm_g726 codec is not exact to the Recommendation.

    tools/g726-reference.py decode <bits> <be|le> <codes.raw> <out.f32>
    tools/g726-reference.py encode <bits> <be|le> <in.s16> <codes.raw>
    tools/g726-reference.py repack <bits> <be|le> <codes.raw> <out.raw>
    tools/g726-reference.py verify <directory of the Recommendation's test sequences>

decode writes what G726Decoder gives: 32-bit little-endian floats, the reconstructed signal limited to 14 bits, over
8192. encode takes 16-bit little-endian samples (their top 14 bits). <bits> is the bits a code (2 to 5); "be" packs the
codes from each byte's most significant bit (FFmpeg's g726, .wav files, RTP's AAL2-G726-*), "le" from the least (RTP's
G726-*, RFC 3551); repack reads the codes in that packing and writes them in the other.

verify proves this file on the Recommendation's digital test sequences (its Appendix II: 16-bit words, a code or a
G.711 sample each, as the ITU-T Software Tool Library distributes them in src/g726/test_data of
https://github.com/openitu/STL, which is not copied here): every decoder sequence (normal, overload and the code
sequences, from reset, to A-law and to μ-law through the synchronous coding adjustment) and every encoder sequence, at
each rate. Run it after any change to this file.

Written from the Recommendation's algorithm (the fixed-point arithmetic of its blocks, whose names the comments give),
with Sun Microsystems' public-domain g72x code as the cross-check; nothing of FFmpeg's.
"""
import struct
import sys

# By the code's magnitude |I|: the quantizer's output levels DQLN, the scale factor multipliers W, the speed control's
# F; and the quantizer's decision levels (the encoder's, and the decoder's synchronous coding adjustment's).
LEVELS = {
    2: [116, 365],
    3: [-2048, 135, 273, 373],
    4: [-2048, 4, 135, 213, 273, 323, 373, 425],
    5: [-2048, -66, 28, 104, 169, 224, 274, 318, 358, 395, 429, 459, 488, 514, 539, 566],
}
MULTIPLIERS = {
    2: [-22, 439],
    3: [-4, 30, 137, 582],
    4: [-12, 18, 41, 64, 112, 198, 355, 1122],
    5: [14, 14, 24, 39, 40, 41, 58, 100, 141, 179, 219, 280, 358, 440, 529, 696],
}
SPEEDS = {
    2: [0, 7],
    3: [0, 1, 2, 7],
    4: [0, 0, 0, 1, 1, 1, 3, 7],
    5: [0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 3, 4, 5, 6, 6],
}
DECISIONS = {
    2: [261],
    3: [8, 218, 331],
    4: [-124, 80, 178, 246, 300, 349, 400],
    5: [-122, -16, 68, 139, 198, 250, 298, 339, 378, 413, 445, 475, 502, 528, 553],
}


def wrap16(x):
    return ((x + 32768) & 0xFFFF) - 32768


def to_float(magnitude, negative):
    """FLOATA, FLOATB: a sign, an exponent (the bit length) and a 6-bit mantissa."""
    exponent = magnitude.bit_length()
    return (negative, exponent, 32 if magnitude == 0 else (magnitude << 6) >> exponent)


def multiply(coefficient, signal):
    """FMULT: a coefficient (over 4) times a float signal."""
    negative, signal_exponent, signal_mantissa = signal
    magnitude = (-coefficient) & 0x1FFF if coefficient < 0 else coefficient
    exponent = magnitude.bit_length()
    mantissa = 32 if magnitude == 0 else (magnitude << 6) >> exponent
    product_exponent = exponent + signal_exponent
    product_mantissa = (mantissa * signal_mantissa + 48) >> 4
    if product_exponent <= 26:
        product = (product_mantissa << 7) >> (26 - product_exponent)
    else:
        product = ((product_mantissa << 7) << (product_exponent - 26)) & 0x7FFF
    return -product if (coefficient < 0) != negative else product


class G726:
    """The state an encoder and a decoder share, from reset."""

    def __init__(self, bits):
        self.bits = bits
        self.a = [0, 0]
        self.b = [0] * 6
        self.dq = [(False, 0, 32)] * 6
        self.sr = [(False, 0, 32)] * 2
        self.pk = [0, 0]
        self.td = 0
        self.yu = 544
        self.yl = 34816
        self.dms = 0
        self.dml = 0
        self.ap = 0

    def predict(self):
        """SEZ, SE (FMULT, ACCUM: sums of 16 bits) and the scale factor Y (LIMA, MIX)."""
        sezi = wrap16(sum(multiply(self.b[i] >> 2, self.dq[i]) for i in range(6)))
        sei = wrap16(sezi + multiply(self.a[0] >> 2, self.sr[0]) + multiply(self.a[1] >> 2, self.sr[1]))
        y = self.yu
        if self.ap < 256:
            y = self.yl >> 6
            difference = self.yu - y
            product = (abs(difference) * (self.ap >> 2)) >> 6
            y += -product if difference < 0 else product
        return sezi >> 1, sei >> 1, y

    def quantize(self, d, y):
        """The encoder's code for a difference (LOG, SUBTB, QUAN), which SYNC uses too."""
        magnitude = abs(d)
        exponent = (magnitude >> 1).bit_length()
        dln = (exponent << 7) + (((magnitude << 7) >> exponent) & 127) - (y >> 2)
        decisions = DECISIONS[self.bits]
        i = sum(1 for level in decisions if dln >= level)
        if self.bits == 2:
            return 3 - i if d < 0 else i
        all_ones = (len(decisions) << 1) + 1
        if d < 0:
            return all_ones - i
        return i or all_ones  # the code of all zeros is never sent

    def step(self, code, prediction=None):
        """One code to the reconstructed signal SR (16 bits), adapting the state."""
        sez, se, y = prediction or self.predict()
        negative = (code >> (self.bits - 1)) != 0
        magnitude = ~code & ((1 << (self.bits - 1)) - 1) if negative else code

        # RECONST, ADDA, ANTILOG; ADDB, ADDC.
        dql = LEVELS[self.bits][magnitude] + (y >> 2)
        dq = 0 if dql < 0 else ((128 + (dql & 127)) << 7) >> (14 - ((dql >> 7) & 15))
        signed_dq = -dq if negative else dq
        sr = wrap16(se + signed_dq)
        dqsez = wrap16(sez + signed_dq)
        pk0 = 1 if dqsez < 0 else 0

        # TRANS.
        yl_integer = self.yl >> 15
        threshold = 31 << 10 if yl_integer > 9 else (32 + ((self.yl >> 10) & 31)) << yl_integer
        transition = self.td == 1 and dq > (threshold + (threshold >> 1)) >> 1

        # FUNCTW, FILTD, LIMB, FILTE.
        self.yu = min(max(y + (((MULTIPLIERS[self.bits][magnitude] << 5) - y) >> 5), 544), 5120)
        self.yl += self.yu + ((-self.yl) >> 6)

        if transition:
            # TRIGB.
            self.a = [0, 0]
            self.b = [0] * 6
            a2 = 0
        else:
            # UPA2, LIMC.
            same1 = pk0 ^ self.pk[0]
            a2 = self.a[1] - (self.a[1] >> 7)
            if dqsez != 0:
                f = self.a[0] if same1 else -self.a[0]
                a2 += -256 if f < -8191 else 255 if f > 8191 else f >> 5
                a2 = min(max(a2 + (-128 if pk0 ^ self.pk[1] else 128), -12288), 12288)
            self.a[1] = a2

            # UPA1, LIMD.
            a1 = self.a[0] - (self.a[0] >> 8)
            if dqsez != 0:
                a1 += -192 if same1 else 192
            self.a[0] = min(max(a1, a2 - 15360), 15360 - a2)

            # UPB.
            leak = 9 if self.bits == 5 else 8
            for i in range(6):
                b = self.b[i] - (self.b[i] >> leak)
                if dq != 0:
                    b += 128 if negative == self.dq[i][0] else -128
                self.b[i] = wrap16(b)

        # FLOATA, FLOATB and the delays.
        self.dq = [to_float(dq, negative)] + self.dq[:5]
        self.sr = [to_float((-sr) & 0x7FFF, True) if sr < 0 else to_float(sr, False), self.sr[0]]
        self.pk = [pk0, self.pk[0]]

        # TONE, TRIGB.
        self.td = 1 if a2 < -11776 else 0

        # FILTA, FILTB, SUBTC, FILTC, TRIGA.
        f512 = SPEEDS[self.bits][magnitude] << 9
        self.dms += (f512 - self.dms) >> 5
        self.dml += ((f512 << 2) - self.dml) >> 7
        if transition:
            self.ap = 256
        elif y < 1536 or self.td == 1 or abs((self.dms << 2) - self.dml) >= self.dml >> 3:
            self.ap += (512 - self.ap) >> 4
        else:
            self.ap += (-self.ap) >> 4
        return sr


def decode(codes, bits):
    """The reconstructed signal limited to 14 bits, a sample a code."""
    state = G726(bits)
    return [min(max(state.step(code), -8192), 8191) for code in codes]


def encode(samples, bits):
    """A code for each 14-bit sample."""
    state = G726(bits)
    codes = []
    for sample in samples:
        prediction = state.predict()
        code = state.quantize(sample - prediction[1], prediction[2])
        state.step(code, prediction)
        codes.append(code)
    return codes


def unpack(data, bits, least_first):
    codes = []
    buffer = held = 0
    for byte in data:
        if least_first:
            buffer |= byte << held
        else:
            buffer = (buffer << 8) | byte
        held += 8
        while held >= bits:
            if least_first:
                codes.append(buffer & ((1 << bits) - 1))
                buffer >>= bits
                held -= bits
            else:
                held -= bits
                codes.append(buffer >> held)
                buffer &= (1 << held) - 1
    return codes


def pack(codes, bits, least_first):
    """The codes' bytes; a last byte's missing bits are 0."""
    data = bytearray()
    buffer = held = 0
    for code in codes + [0] * (-len(codes) * bits % 8 // bits + 1):
        if least_first:
            buffer |= code << held
        else:
            buffer = (buffer << bits) | code
        held += bits
        while held >= 8:
            if least_first:
                data.append(buffer & 255)
                buffer >>= 8
                held -= 8
            else:
                held -= 8
                data.append(buffer >> held)
                buffer &= (1 << held) - 1
    return bytes(data[: (len(codes) * bits + 7) // 8])


# G.711, for verify: the decoder's output to A-law or μ-law (COMPRESS) and back (EXPAND), in 14-bit uniform PCM.
def compress(sr, mu_law):
    if mu_law:
        negative = sr < 0
        value = min(-sr if negative else sr, 8158) + 33
        segment = value.bit_length() - 6
        return ((segment << 4) | ((value >> (segment + 1)) & 15)) ^ (0x7F if negative else 0xFF)
    value = (-1 if sr <= -32768 else sr) >> 1
    negative = value < 0
    value = min(-value - 1 if negative else value, 4095)
    segment = max(value.bit_length() - 5, 0)
    code = value >> 1 if value < 32 else (segment << 4) | ((value >> segment) & 15)
    return code ^ (0x55 if negative else 0xD5)


def expand(code, mu_law):
    if mu_law:
        code = ~code & 0xFF
        value = ((((code & 15) << 1) + 33) << ((code & 0x70) >> 4)) - 33
        return -value if code & 0x80 else value
    code ^= 0x55
    value, segment = (code & 15) << 1, (code & 0x70) >> 4
    value = value + 1 if segment == 0 else (value + 33) << (segment - 1)
    return (value if code & 0x80 else -value) << 1


def synchronize(state, code, sr, se, y, mu_law):
    """SYNC: the G.711 sample, moved one step when coding it again would not give back the code."""
    sp = compress(0 if mu_law and sr <= -32768 else sr, mu_law)
    again = state.quantize(expand(sp, mu_law) - se, y)
    if again == code:
        return sp
    sign = 1 << (state.bits - 1)
    lower = (again ^ sign) > (code ^ sign)
    if mu_law:
        if lower:
            return (0x7E if sp == 0xFF else sp + 1) if sp & 0x80 else max(sp - 1, 0)
        return max(sp - 1, 0x80) if sp & 0x80 else (0xFE if sp == 0x7F else sp + 1)
    if lower:
        return (0x55 if sp == 0xD5 else ((sp ^ 0x55) - 1) ^ 0x55) if sp & 0x80 else (0x2A if sp == 0x2A else ((sp ^ 0x55) + 1) ^ 0x55)
    return (0xAA if sp == 0xAA else ((sp ^ 0x55) + 1) ^ 0x55) if sp & 0x80 else (0xD5 if sp == 0x55 else ((sp ^ 0x55) - 1) ^ 0x55)


def verify(directory):
    def words(name):
        with open(f"{directory}/{name}", "rb") as f:
            data = f.read()
        return list(struct.unpack(f"<{len(data) // 2}H", data))

    failed = 0
    for bits in (2, 3, 4, 5):
        rate = 8 * bits
        decodings = [(f"{kind}{rate}f{law}.i", f"{kind}{rate}f{out}.o", out in "mx")
                     for kind in ("rn", "rv") for law, out in (("m", "m"), ("a", "a"), ("m", "c"), ("a", "x"))]
        decodings += [(f"i{rate}", f"ri{rate}fm.o", True), (f"i{rate}", f"ri{rate}fa.o", False)]
        for codes_name, output_name, mu_law in decodings:
            state = G726(bits)
            got = []
            for code in words(codes_name):
                prediction = state.predict()
                sr = state.step(code, prediction)
                got.append(synchronize(state, code, sr, prediction[1], prediction[2], mu_law))
            wrong = sum(1 for a, b in zip(got, words(output_name)) if a != b)
            failed += wrong
            print(f"decode {codes_name} -> {output_name}: {len(got)} samples, {wrong} wrong")
        for source, law, codes_name in [(f"{kind}.{law}", law, f"{prefix}{rate}f{law}.i")
                                        for kind, prefix in (("nrm", "rn"), ("ovr", "rv")) for law in "ma"]:
            got = encode([expand(sample, law == "m") for sample in words(source)], bits)
            wrong = sum(1 for a, b in zip(got, words(codes_name)) if a != b)
            failed += wrong
            print(f"encode {source} -> {codes_name}: {len(got)} codes, {wrong} wrong")
    sys.exit(f"g726-reference.py: {failed} values differ from the Recommendation's" if failed else 0)


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "verify":
        verify(sys.argv[2])
    if len(sys.argv) != 6 or sys.argv[1] not in ("decode", "encode", "repack") or sys.argv[2] not in "2345" or sys.argv[3] not in ("be", "le"):
        sys.exit(__doc__)
    bits, least_first = int(sys.argv[2]), sys.argv[3] == "le"
    with open(sys.argv[4], "rb") as f:
        data = f.read()
    if sys.argv[1] == "decode":
        samples = decode(unpack(data, bits, least_first), bits)
        out = struct.pack(f"<{len(samples)}f", *(sample / 8192 for sample in samples))
    elif sys.argv[1] == "repack":
        out = pack(unpack(data, bits, least_first), bits, not least_first)
    else:
        out = pack(encode([sample >> 2 for sample in struct.unpack(f"<{len(data) // 2}h", data)], bits), bits, least_first)
    with open(sys.argv[5], "wb") as f:
        f.write(out)


if __name__ == "__main__":
    main()

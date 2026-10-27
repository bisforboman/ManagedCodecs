# Parametric stereo beyond the baseline (2026-10-27)

The HE-AAC v2 decoder implements the baseline parametric stereo decoder of ISO/IEC 14496-3 8.6.4: 10 and 20
bands, mixing procedure Ra, no inter-channel or overall phase differences (IPD, OPD). The rest of the tool is three pieces,
listed in README's Open section as untested because no encoder at hand sends them. This record says what each piece
is, what it takes, how it would be tested, and asks the owner which to do and in what order.

## Where the decoder stands

- **34 bands** (iid_mode or icc_mode 2 or 5): refused with `NotSupportedException` (`PsData`).
- **Mixing procedure Rb** (icc_mode 3 to 5): refused with `NotSupportedException` (`PsProcessor.Apply`).
- **IPD and OPD** (the ps_extension with extension id 0): read past and ignored, as the baseline decoder may do. A
  stream that sends them decodes, but not as a full decoder would: FFmpeg, built with `PS_BASELINE 0`, applies them,
  so on such a stream this decoder and FFmpeg disagree without a word. No sample has them, so no test shows it.

FFmpeg 6.1.1 decodes all three (`libavcodec/aacps.c`, `aacps_common.c`: `PS_BASELINE` is 0, the 34-band hybrid
analysis and decorrelation, the mixing matrices of both procedures, IPD and OPD with their smoothing), so it can be
the reference for each, as it is for the rest of the AAC decoder.

## A test source: the FDK encoder, patched

The FDK encoder (`tools/make-heaac-samples.sh`, already patched twice: the SBR header's second part, downsampled
SBR) writes only 10 and 20 bands, mixing Ra and no IPD/OPD. Its PS bit writer (`libSBRenc/src/ps_bitenc.cpp`)
gets each piece closer than a writer of our own would start:

- **Mixing Rb** is a header field: the writer puts `iccMode` in 3 bits, and Rb is the same parameters read with
  `icc_mode + 3`. A one-line patch (`iccMode += 3` where the header is filled) gives a valid Rb stream of the
  encoder's own parameters.
- **IPD and OPD**: the writer already has the extension (`PS_EXT_ID_V0`, byte aligned), the IPD and OPD Huffman
  tables for both delta directions and `enableIpdOpd`; the encoder never sets it. A patch sets it and fills the
  values: zeros, then a slowly turning phase per band, so the smoothing and the 8-step quantization both show.
- **34 bands**: `getNoBands` says "fine not supported" and the analysis is 20 bands. A patch that writes 34 values
  can take them from the 20-band analysis by the spec's 20-to-34 band mapping (the decoder's own map run backwards):
  the stream is then valid 34-band data whose parameters vary as a real signal's do, which is what the decoder's
  34-band hybrid filterbank, decorrelation and parameter mapping need to be checked. Larger than the other two.

If a patch proves impractical, the fallback is a hand-built SBR+PS stream writer in the tests (as
`CabacEncoder.cs` is for H.264): more code, and its parameters would be chosen rather than measured.

## The work, one PR each

1. **Mixing Rb.** The Rb mixing matrix (8.6.4.6), selected by icc_mode; the FDK patch and
   a sample; README and docs/conformance.md. Smallest: the parameters and their mapping are the same as Ra's.
2. **IPD and OPD.** Reading the extension instead of skipping it, the phase parameters' dequantization and their
   smoothing over envelopes (8.6.4.6), applied to the mixing; the patch and a sample. Also fixes the silent
   disagreement above.
3. **34 bands.** The 34-band hybrid analysis (sub-QMF filters splitting the low QMF bands into 32 more), the 34-band
   decorrelation tables (all-pass delays, fractional delays, decay slopes), the 10/20 to 34 parameter mapping and the
   34-band mixing; the patch and a sample. The largest, and the one most likely to need an `AacStatistics` counter
   to prove the sample reaches it.

Each keeps the decoder's rule: implemented from ISO/IEC 14496-3, checked against FFmpeg on the sample, a mutation
entry for any guard only the new sample reaches, and a fuzz pass over the new parsing (the extension, the 34-band
paths). None changes the public API: they are patch-version work.

## For the owner to decide

- **Which pieces, and in what order.** Proposed: Rb, then IPD/OPD, then 34 bands (smallest first; the second also
  fixes a silent disagreement).
- **The test source.** Proposed: patch the FDK encoder; hand-built streams only where a patch fails.
- **Whether IPD/OPD should first be refused** rather than ignored, until piece 2 lands, so a stream that sends them
  fails loudly instead of decoding slightly wrong. Proposed: no; piece 2 is near, and refusing would stop such streams
  from playing at all, where ignoring the phases still gives usable stereo.

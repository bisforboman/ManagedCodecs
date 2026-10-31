# AAC and HE-AAC: notes for working on the AAC and HE-AAC decoder and their samples

The repository's CLAUDE.md (process, core idea, cross-cutting gotchas) applies too.

## Samples: which encoder writes what

FFmpeg's AAC encoder writes no pulse data (`tools/make-aac-pulse.py` writes a stream with it) and no SBR or PS:
HE-AAC comes from the FDK encoder, `tools/make-heaac-samples.sh`, its streams committed in `samples/heaac/`.

## Gotchas (each cost time once)

- FFmpeg's `-ignore_editlist 1` gives every decoded AAC sample, priming included; without it the first 1024 go.
- Tables shared between sample rates (AAC's 44.1 and 48 kHz band offsets) are one array: never find a rate index by
  the array's identity.
- FFmpeg reads AAC channel configuration 7 as 7.1 (FL FR FC LFE BL BR SL SR), not the spec's "outside front" pair.
- HE-AAC: FFmpeg's SBR is single precision, and the HF generator's prediction is nearly singular for a subband holding
  one sinusoid, so its output moves by up to about 5e-3 of the peak there (this decoder's does too in float): compare
  with a loose largest and a tight mean difference, and check a suspected bug by whether it moves the mean. Math.Cos and
  Math.Sin differ in their last bit between .NET and the WebAssembly runtime, which that prediction can magnify into the
  output (wasm-check.sh fails): tables that feed SBR are computed in arithmetic alone (`QmfTrig`). Math.Pow too: `Math.Pow(x, 4.0 / 3)` differs in the
  last bit at 7 of AAC's 8192 dequantization values, which only a stream reaching one shows (an unseeded noise sample
  made `wasm` fail now and then); `FourThirdsPower` and `QuarterPowerOfTwo` replace it. The FDK encoder
  writes the SBR header's second part only with non-default settings (a patched copy, `make-heaac-samples.sh`), and
  downsampled SBR only for ELD (another patched copy; with PS also `sbrEncoder_IsSingleRatePossible`, and a build
  without assertions, `-DCMAKE_BUILD_TYPE=Release`: its PS analysis asserts at one rate). The downsampled synthesis kernel is
  the full one at even n, (4n - 255): read as (2n - 127) it lags a quarter of a sample and misses FFmpeg by 14 to 17% at
  3 to 6 kHz, which looks like a core bug. FFmpeg's parametric stereo (the oracle where the spec's text is out of reach): the phases' band count follows
  the last header that enabled IID (none before one), a frame is in 34 bands when either IID or ICC is, and its
  per-envelope slots keep imaginary parts above the phase bands that later frames interpolate towards. A one-line
  `fprintf` in a scratch FFmpeg build (`git clone -b n6.1.1` from GitHub, `./configure --disable-everything
  --enable-decoder=aac --enable-demuxer=mov ...`, a few minutes) settles such questions faster than reading.

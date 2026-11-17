# Players: sound with more than two channels (2026-11-17)

The open item of #535 (docs/decisions/2026-11-15-sound-tracks.md, "to decide if missed"). A plan, decided on by the owner
(Decided, below).

## The gap

`MediaPlayer`, `AudioPlayer` and `LivePlayer` give sound as `AudioChunk`: interleaved stereo, "more channels give their
first two" (`AudioPipeline.ToStereo`). For a 5.1 film, front left and right play, and the centre, where the dialogue is,
is lost, with the surrounds and the LFE. The decoders give every channel (`AudioFrame`); only the players drop them.

Which streams have more than two channels:

- **AAC:** channel configurations 3 to 7 and program config elements. The decoder gives them in WAVE (FFmpeg's) order,
  5.1 as FL FR FC LFE BL BR.
- **PCM:** WAV and AVI (WAVEFORMATEXTENSIBLE's channel mask names the layout; `WaveFormat` reads past it today),
  QuickTime and MP4 lpcm, Matroska, and MXF's multichannel AES3 or BWF.
- **Not:** MP3 (stereo at most), and IMA and Microsoft ADPCM and G.726 as the players meet them.

## What FFmpeg does (measured, FFmpeg 6.1.1)

`ffmpeg -ac 2` to float, a sine of its own on each channel and least squares on the output. Each input channel's
weight into left and right:

| Layout | Left | Right |
|---|---|---|
| 2.1 | FL | FR (LFE dropped) |
| quad | FL + 0.7071 BL | FR + 0.7071 BR |
| 5.0 | FL + 0.7071 FC + 0.7071 BL | FR + 0.7071 FC + 0.7071 BR |
| 5.1 | FL + 0.7071 FC + 0.7071 BL | FR + 0.7071 FC + 0.7071 BR (LFE dropped) |
| 6.1 | FL + 0.7071 FC + 0.5 BC + 0.7071 SL | FR + 0.7071 FC + 0.5 BC + 0.7071 SR |
| 7.1 | FL + 0.7071 FC + 0.7071 BL + 0.7071 SL | FR + 0.7071 FC + 0.7071 BR + 0.7071 SR |

So swresample's default matrix: the centre and every surround at -3 dB, a back centre at -6 dB on both, the LFE dropped
(its `lfe_mix_level` is 0 by default). To float, the sums are not scaled down, so a loud 5.1 frame can exceed 1. To
16-bit integers, swresample scales the whole matrix so a row sums to 1 (5.1: FL x 0.4142, FC and BL x 0.2929), as
integer output can't hold more.

A reference is then possible: each multichannel sample decoded by FFmpeg and downmixed with `-ac 2` to f32le, against
the player's chunks by value (as `.f32` references are compared today).

Twelve more layouts (5.1(side), 7.1(wide), 7.1(wide-side), 4.0, 3.0, 3.1, 6.0, hexagonal, octagonal) give one rule a
speaker, whatever else the layout holds:

| Speaker | Left | Right |
|---|---|---|
| FL, FR; FLC, FRC | 1 on its side | 1 on its side |
| FC | 0.7071 | 0.7071 |
| BL, BR; SL, SR | 0.7071 on its side | 0.7071 on its side |
| BC | 0.5 | 0.5 |
| LFE | 0 | 0 |

A stream with only a channel count (raw PCM, `channel_layout` unknown in ffprobe) is downmixed as FFmpeg's default
layout for the count: 3 as 2.1, 4 as 4.0 (FL FR FC BC: not quad), 5 as 5.0, 6 as 5.1, 7 as 6.1, 8 as 7.1. That is the
case for Matroska and MXF, which carry no layout, and for a WAVEFORMATEX without WAVEFORMATEXTENSIBLE's mask.

Still to measure while building: the top speakers (5.1.2 and up, a WAV mask can name them), more than eight channels
with no layout, and AAC's matrix-mixdown coefficients in a program config element (whether FFmpeg's decoder uses them
by default).

## Decisions for the owner

1. **What the players give for more than two channels by default.**
   - **(a) A stereo downmix, FFmpeg's matrix, in float, unscaled** (recommended). The dialogue and the surrounds are
     heard; checked against `ffmpeg -ac 2` by value. A consumer that clips to [-1, 1] hears a loud passage clip, as
     FFmpeg's float output would. No new API, so a patch, and CHANGELOG says such files sound different.
   - (b) The same matrix, scaled so a row sums to 1 (swresample's integer output): never above 1, but every 5.1
     film 7.7 dB quieter than its stereo version, and the reference is FFmpeg's s16 output, rounded to 16 bits.
   - (c) Keep the first two channels, as today.
2. **Every channel, for an application that plays surround.**
   - **(a) Not now** (recommended): no consumer in the repository plays more than two (mclive and mclive-wpf are silent;
     the Unity, Godot and MonoGame samples play stereo), and it can be added later without a break.
   - (b) Opt-in: `PlaybackOptions.AudioChannels` (stereo by default, or the source's), `AudioChunk.Channels`, and the
     channel order documented as WAVE's. New public API: the next minor version.
3. **Where the layout comes from.**
   - **(a) The decoder's**, with a `ChannelLayout` kept internal: AAC's from its configuration, PCM's from WAVE's
     channel mask or, without one, the default for the count as FFmpeg does (to measure) (recommended).
   - (b) A public `AudioStreamInfo.ChannelLayout` now: new public API across Core and every demuxer, wanted only if 2(b)
     is.

## Decided (the owner, 2026-11-17)

1. **(a):** FFmpeg's downmix to stereo in float, unscaled, by default. No new API.
2. **(a):** no way to get every channel for now.
3. **(a):** the layout from the decoder, kept internal: AAC's configuration or program config element, PCM's
   WAVEFORMATEXTENSIBLE mask, else FFmpeg's default for the count.

## Checks

- Samples in make-samples.sh, each saying what it isolates: 5.1 AAC (channel configuration 6), 7.1 AAC, 5.1 PCM in
  WAV with a mask and without, and an MXF file of six mono tracks with `PairMonoTracks` off (the pairing is #570's).
- References: each one's `-ac 2` f32le from FFmpeg, compared with the player's chunks by value.
- Unit tests for every row of both tables on hand-made frames, and mutation entries for the matrix's -3 dB and the LFE
  being dropped (a rule only some streams reach).

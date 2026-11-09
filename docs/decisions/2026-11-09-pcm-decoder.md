# A PCM decoder for AVI audio (2026-11-09)

AVI audio is exposed since #446 (docs/decisions/2026-11-08-avi-audio.md), but only MP3 and AAC are decoded: the
cameras' AVIs (realworld.py's three MJPEG AVIs, 8-bit PCM at 11 kHz and 16-bit PCM) play silent, their audio named
`"0x0001"` and skipped. That record left PCM decoding for later; this one decides it. Nothing is built yet.

## What AVI gives today

- `AviDemuxer.Audio.Codec` is `"0x0001"` for every PCM stream (wFormatTag 1), whatever its depth, and `CodecPrivate`
  only the WAVEFORMATEX's cbSize bytes (none for PCM): a decoder can't tell 8-bit from 16-bit.
- Packets: a dwSampleSize stream's chunks are cut as FFmpeg cuts them and timed in samples (#446).

## What FFmpeg makes of it

FFmpeg's decoders are `pcm_u8` (unsigned 8-bit, wBitsPerSample 8) and `pcm_s16le` (signed 16-bit little-endian,
wBitsPerSample 16), interleaved. Its float output (`-f f32le`) is exactly (x - 128) / 128 and x / 32768: checked bit
for bit on FFmpeg AVIs of each (11.025 kHz mono u8, 22.05 kHz stereo s16), so the comparison can be exact.

## Decided (the owner, 2026-11-09)

1. **Names:** new constants `CodecIds.PcmU8` and `CodecIds.PcmS16Le` (as FFmpeg's `pcm_u8` and `pcm_s16le`).
   `AviDemuxer` names a wFormatTag 1 stream by its wBitsPerSample; other depths (24, 32 bits, WAVE_FORMAT_EXTENSIBLE)
   stay `"0x0001"` and skipped. This amends the AVI decision's "no new `CodecIds` constants": a constant names a codec
   the library decodes.
2. **Where:** `PcmDecoder : IAudioDecoder` in `ManagedCodecs.Core`: about a hundred lines and no tables, so every
   package and the Unity build have it without a new package. `PcmDecoder(AudioStreamInfo)` takes the format from the
   codec; `Decode(Packet)` honours SkipSamples, SkipEndSamples and Discard as the other decoders do.
3. **Scope:** AVI only at first. PCM in MP4 (`sowt`, `twos`, `lpcm`), Matroska (`A_PCM/INT/LIT`, `A_PCM/INT/BIG`) and
   `.wav` files are later decisions.
4. **Playback:** `MediaPlayer` plays it, so the camera AVIs have sound; `mcdec` decodes it, so the AOT, Unity and
   WebAssembly checks compare it.
5. **When:** after #448 (a second AVI audio PR, now conflicting with main, which has #446) is merged or closed: both
   change `AviDemuxer`.

## Checks

- Exact against FFmpeg (`-f f32le`): main's `avi-mjpeg-pcm` (16-bit) and a new 8-bit sample beside it.
- realworld.py's three camera AVIs then compare their audio too (its "not decoded" notes go).
- A CHANGELOG "Added" line, in the next minor version (new public API).

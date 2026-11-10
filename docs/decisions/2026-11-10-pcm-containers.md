# PCM in MOV, MP4, Matroska and .wav files (2026-11-10)

0.11.0 decodes AVI's 8- and 16-bit PCM (docs/decisions/2026-11-09-pcm-decoder.md, which left the other containers for
later). This record decides the rest. Built, all of it: the decoder's formats with QuickTime and Matroska, then
`.wav` files, then ISO MP4's `ipcm` and `fpcm`.

## What the demuxers give today

Every layout FFmpeg's muxers write, made from one 22.05 kHz tone (FFmpeg 6.1), demuxed by `mcdec --dump`. Every track
is found and its packets read, but the codec is only named, never decoded:

| FFmpeg's decoder | MOV entry | ISO MP4 entry | Matroska CodecID | Our `Codec` today |
| --- | --- | --- | --- | --- |
| pcm_u8 | `raw ` | (refused) | A_PCM/INT/LIT, BitDepth 8 | the entry's name / CodecID |
| pcm_s16le | `sowt` | `ipcm` | A_PCM/INT/LIT, 16 | the same |
| pcm_s16be | `twos` | `ipcm` | A_PCM/INT/BIG, 16 | the same |
| pcm_s24le, pcm_s24be | `in24` (+ `enda`) | `ipcm`, as 32-bit | A_PCM/INT/LIT or BIG, 24 | the same |
| pcm_s32le, pcm_s32be | `in32` (+ `enda`) | `ipcm` | A_PCM/INT/LIT or BIG, 32 | the same |
| pcm_f32le, pcm_f32be | `fl32` (+ `enda`) | `fpcm` | A_PCM/FLOAT/IEEE, 32 (LE only) | the same |
| pcm_f64le, pcm_f64be | `fl64` (+ `enda`) | `fpcm` | A_PCM/FLOAT/IEEE, 64 (LE only) | the same |

So one name can stand for several formats: MOV's `in24`, `in32`, `fl32` and `fl64` take their byte order from an
`enda` box (big-endian without one), and ISO's `ipcm` and `fpcm` take the order and depth from a `pcmC` box.
Matroska's A_PCM/INT/LIT at BitDepth 8 is unsigned, as FFmpeg reads it. QuickTime's `lpcm` (sound description version
2) carries its format in flags, and FFmpeg's muxer never writes it.

## What FFmpeg makes of it

FFmpeg's float output (`-f f32le`) is a formula for each: u8 (x − 128) / 128, s16 x / 2^15, s24 x / 2^23, s32
x / 2^31, f32 as it is, f64 rounded to the nearest float. That was checked bit for bit on 20000 random values each
(s16be, s24le, s32le, s32be, f32be, f64le, u8), so the comparison can be exact, as AVI's is.

## Decided (the owner, 2026-11-10)

1. **Containers:** all four. MOV, ISO MP4 (`ipcm`/`fpcm`, ISO/IEC 23003-5), Matroska, and raw `.wav` files.
2. **Formats:** `PcmDecoder` takes integer PCM at 8, 16, 24 and 32 bits and float PCM at 32 and 64 bits, in both
   byte orders (8-bit unsigned only).
3. **Names:** one `CodecIds` constant per format, as FFmpeg names them, beside 0.11's `PcmU8` and `PcmS16Le`:
   `PcmS16Be`, `PcmS24Le`, `PcmS24Be`, `PcmS32Le`, `PcmS32Be`, `PcmF32Le`, `PcmF32Be`, `PcmF64Le` and `PcmF64Be`.
   Each demuxer resolves its own description (`enda`, `pcmC`, BitDepth, wBitsPerSample) into one of them; a layout
   none of them fits keeps its container's name, undecoded.

## Still to settle while building

- **.wav:** a `WavDemuxer` that `Demuxer.Open` detects by `RIFF....WAVE`. It reads tag 1 (integer PCM), tag 3 (float)
  and WAVE_FORMAT_EXTENSIBLE (0xFFFE, by its sub-format GUID), and names other tags as AVI does (MP3, AAC, else
  `"0x...."`), sharing AVI's WAVEFORMATEX code. Its packets are cut as FFmpeg's wav demuxer cuts them, checked
  against ffprobe's packets.
- **Order:** the decoder's formats with MOV and Matroska first (the commonest in practice: cameras and editors write
  MOV, Matroska tools write A_PCM), then ISO MP4's `ipcm`/`fpcm`, then `.wav`. Each is its own PR.
- **Samples:** one per layout FFmpeg writes (the table above), each compared exactly with FFmpeg's `-f f32le`.
  `lpcm` and big-endian float in Matroska have no FFmpeg sample: unit tests, and docs/conformance.md says so.

## Checks

- Exact against FFmpeg on every new sample, as `PcmDecoderTests` does for AVI.
- `MediaPlayer` plays each; `mcdec` decodes each, so the AOT, Unity and WebAssembly checks compare them.
- A CHANGELOG "Added" line per PR, in the next minor version (new public API).

## Later: seeking in .wav (2026-11-10)

The owner chose that `WavDemuxer` seeks, before its first release. Every packet is a keyframe, and the packets are
listed when opening, so `Seek` starts at the one whose samples hold the time, with no read of the file. FFmpeg's wav
seek starts a packet at the exact sample instead; ours keep the packets of a read from the start, as `IDemuxer.Seek`
asks of every demuxer (docs/conformance.md, .wav files).

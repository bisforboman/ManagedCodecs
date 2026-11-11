# Playing audio-only files: AudioPlayer (2026-11-11)

0.12.0 reads audio-only files (`.wav` through `WavDemuxer`, raw MP3 and AAC), but nothing plays them:
`MediaPlayer` throws `NotSupportedException` ("The file has no video stream") when a file has no video, and its
`Video` property is non-nullable. VideoTexture (ManagedCodecs.MonoGame), the Godot node and the Unity sample read it.
This record decides how such files play. Nothing is built yet.

## What there is

- `MediaPlayer` decodes on a thread of its own and hands out frames (`TakeFrame(position)`) and audio
  (`TakeAudio()`, float stereo `AudioChunk`s, a bounded queue of 32). The game's clock drives the frames; the audio
  is taken as the sound has room for it.
- `Loop` seeks the demuxer back to its start (`IDemuxer.Seek`). `WavDemuxer` seeks (#481), but `Mp3Demuxer` and
  `AdtsDemuxer` don't (`SeekTests.AdtsCannotSeek`), so a raw MP3 or AAC file couldn't loop.

## Decided (the owner, 2026-11-11)

1. **API:** a new class, `AudioPlayer` (ManagedCodecs.Playback): `Start`, `TakeAudio`, `IsFinished`, `Error`,
   `Statistics`, `Dispose`, and `PlaybackOptions.Loop`, as `MediaPlayer` has them. `MediaPlayer` stays as it is, so
   nothing breaks. The decoding thread's audio path (decoder choice, interleaving to stereo, the bounded queue) is
   shared between the two.
2. **Seeking:** `Mp3Demuxer` and `AdtsDemuxer` seek, to the packet holding the time, from the frames they find when
   opening, as `WavDemuxer.Seek` does. MP3's gapless trim (the LAME tag's delay) applies again from the file's start.
   So `Loop` works on every audio-only file, and `SeekTests` covers the two demuxers.
3. **Scope:** the player only, with its tests (each sample's audio as the decoders give it) and `mcdec`. VideoTexture,
   the Godot node and the Unity sample keep needing video; playing sound-only files there is a later step.

## Checks

- `AudioPlayerTests`: every audio-only sample plays the decoders' own output, stereo-interleaved, in order; `Loop`
  plays it again; errors end up in `Error`, not on the thread.
- `SeekTests` takes raw MP3 and AAC; a mutation entry for each demuxer's seek.
- A CHANGELOG "Added" line, in the next minor version (new public API).

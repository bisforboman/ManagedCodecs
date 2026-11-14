# Sound-only files in MonoGame: AudioTrack (2026-11-13)

0.13.0 plays sound-only files (`.wav`, raw MP3 and AAC) through `AudioPlayer` (2026-11-11-audio-player.md), and the
samples fall back to it, each with its own sound output: mcplay's was an internal `AudioOnly` class. `VideoTexture`,
public in ManagedCodecs.MonoGame, stayed video-only: its constructors throw `NotSupportedException` on a file without
video, and its `Player` is a non-nullable `MediaPlayer`. This record decides how a MonoGame game plays such files.

## Decided (the owner, 2026-11-13)

1. **A new class beside VideoTexture**, `AudioTrack` (ManagedCodecs.MonoGame), over `AudioPlayer`: the four
   constructors (path or stream, with or without `PlaybackOptions`, of which `Loop` counts), `Player`, `Play`, `Pause`,
   `Update()`, `Volume`, `Position`, `IsPlaying`, `IsFinished`, `Dispose`. `VideoTexture` is unchanged, so nothing
   breaks: a minor version. (Not taken: `VideoTexture` playing sound-only files, which would need `Player` nullable or
   throwing, a break; a static factory returning either class.) Named `AudioTrack`: not `AudioStream` (it is no
   `System.IO.Stream`), not `Song` or `SoundEffect` (XNA has both).
2. **Position follows the sound:** the buffers the `DynamicSoundEffectInstance` has finished (what was queued less its
   `PendingBufferCount`), not the game's clock, so it stands still while paused or while the decoding falls behind,
   in steps of a buffer (a frame of AAC or MP3, 4096 bytes of PCM). `Update` takes no `GameTime`: nothing in it needs
   one. A looped file goes on counting. Without an audio device the audio is dropped as it is decoded, and counted.
3. **mcplay** uses it in place of its `AudioOnly`. The Godot node and the Unity sample keep their own outputs (no
   MonoGame there).

## Checks

- `AudioTrackTests`: a chunk's duration (stereo pairs at its rate), the finished buffers' time, a file without audio
  refused, the state before playing. The sound needs an audio device, which CI doesn't have; mcplay plays through it.
- A CHANGELOG "Added" line, in the next minor version (new public API).

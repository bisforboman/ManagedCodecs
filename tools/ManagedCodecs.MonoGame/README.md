# mcplay: in-game video with MonoGame

A sample for issue #254 (docs/decisions/2026-10-31-game-engines.md): plays an MP4 or AVI (H.264 or MJPEG, AAC audio)
in a MonoGame window, decoded by ManagedCodecs, to show the pieces a game needs and what they cost. Not a media
player: no seeking, no pause, Escape quits.

    dotnet run -c Release --project tools/ManagedCodecs.MonoGame -- <file.mp4>

- A thread of its own demuxes and decodes ahead (H.264 with frame threading, up to 4 threads) and converts each frame
  to RGBA on the CPU (`YuvToRgba`); the game loop shows the newest frame due by a clock started at the first frame and
  drops late ones, through one `Texture2D` (`SetData` each frame).
- Audio goes to a `DynamicSoundEffectInstance` as 16-bit stereo (more channels downmixed to the first two).
- The window title shows the decode and conversion time per frame, the number a game's frame budget cares about.
- Colour: frames carry no colour metadata yet, so H.264 converts as limited range with BT.709 from 720 lines and
  BT.601 below, MJPEG as full range BT.601. Checked against FFmpeg's scaler (`scale=in_color_matrix=...:in_range=tv:
  flags=neighbor+full_chroma_int+accurate_rnd`, nearest-neighbour chroma as here): within 2 levels for 8-bit samples,
  1 for 10-bit (mean 0.1 to 0.2).
- A shader would convert faster than the CPU, but MonoGame's effect compiler (mgfxc) needs Wine on Linux, so it is
  left for when the CPU conversion turns out to be the bottleneck.

CI builds it; it needs a window and a GPU to run, so it runs on a developer's machine, not in CI.

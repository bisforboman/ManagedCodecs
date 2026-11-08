# mcplay: in-game video with MonoGame

A sample for issue #254 (docs/decisions/2026-10-31-game-engines.md): plays an MP4, Matroska or AVI (H.264 or MJPEG,
AAC audio) in a MonoGame window with the `ManagedCodecs.MonoGame` package's `VideoTexture`, to show what in-game video
costs. Not a media player: no seeking; space pauses, Escape quits.

    dotnet run -c Release --project tools/ManagedCodecs.MonoGame.Sample -- <file.mp4>

- `VideoTexture` (`ManagedCodecs.MonoGame`) runs `ManagedCodecs.Playback`'s `MediaPlayer`, which demuxes and decodes
  ahead on a thread of its own (H.264 with frame threading, up to 4 threads) and converts each frame to RGBA on the CPU
  (`YuvToRgba`, inside the package); each `Update` shows the newest frame due by the game's clock from the first frame
  (`TakeFrame` drops late ones), through one `Texture2D` (`SetData` each frame). A frame's pixels go back to
  `MediaPlayer.Return`
  once uploaded or dropped and the next frame is converted into them: a new 3.6 MB array per 720p frame meant a gen 2
  collection every 8 frames or so (31 in 240 frames of a 720p clip, 2 with the arrays reused).
- Audio goes to a `DynamicSoundEffectInstance` as 16-bit stereo (of more channels, the first two). Without
  an audio device (OpenAL finds none) the video plays alone, the audio decoded and dropped.
- The window title shows the decoding's and the RGBA conversion's time per frame, means over the run on the decoding
  thread. The decoding's is the time in the decoder's calls over the frames they returned: with H.264 frame threading
  one call's time doesn't belong to one frame, so only a mean over many is meaningful.
- Colour: each frame converts by its own `ColorMatrix` and `ColorRange` (BT.601, BT.709, BT.2020, SMPTE 240M; limited
  or full), and where the stream and its container say nothing, as players assume: BT.709 from 720 lines, BT.601
  below, limited range (MJPEG frames say full range BT.601). Checked against FFmpeg's scaler
  (`scale=in_color_matrix=...:in_range=...:flags=neighbor+full_chroma_int+accurate_rnd`, nearest-neighbour chroma as
  here) on the first frame of `h264-color-bt709`, `-bt2020-pq`, `-hlg-sar`, `h264-colr-only` and `testsrc-420.avi`:
  within 2 levels for 8-bit samples (1 for 10-bit when first measured), mean under 0.3. The transfer is not
  converted: PQ and HLG show dim and flat, as without tone mapping.
- Shape: each frame is drawn at its `SampleAspectRatio` (anamorphic video wider than its samples), and turned by the
  stream's `Rotation` (phone video), counter-clockwise as FFmpeg's command line turns it.
- A shader would convert faster than the CPU, but MonoGame's effect compiler (mgfxc) needs Wine on Linux: still so in
  dotnet-mgfxc 3.8.6.1 (2026-11-04, error MGFXC0001 without it). With the conversion now about 2 ms a 1080p frame
  (Measured, below), it is parked until MonoGame compiles shaders without Wine.

CI builds it. It needs a window and OpenGL, so CI doesn't run it, but it runs headless under Xvfb with Mesa's software
renderer (`Xvfb :99 &`, `DISPLAY=:99`, and `ALSOFT_DRIVERS=null` for audio with no device behind it).

## Measured

Under Xvfb on the cloud VM (4 vCPUs of a 2.1 GHz Xeon, Mesa's llvmpipe drawing), the window title's means, with the
conversion by tables on one thread (as it was then):

| Video | Frames shown | Decode, ms a frame | RGBA conversion, ms a frame |
|---|---|---:|---:|
| Caminandes: Gran Dillama (1920x1080, 24 fps, H.264 High and AAC, 6.9 Mbps) | 1290 in 54 s: all, in time | 4.7 | 13.0 |
| testsrc2 with noise (1920x1080, 30 fps, libx264 High CRF 20, and AAC, 37 Mbps) | 20 s played in 21 s with start-up | 9.0 | 18.9 |

1080p30 keeps up, the noisy one with little to spare: decoding and conversion share the decoding thread, 28 of a
frame's 33 ms there. That conversion was by tables on one thread. It is now by 128-bit vectors where chroma has half
the width (4:2:0, 4:2:2), shared out over threads from about 512x512 (`YuvToRgba.Convert(VideoFrame, byte[])`), and
checked byte for byte against the formula (tests/ManagedCodecs.Tests/Playback/YuvToRgbaTests.cs):

| Caminandes' frames (1080p, 4:2:0) | ms a frame |
|---|---:|
| Tables, one thread (before) | 6.3 to 6.7 |
| Vectors, one thread | 3.4 to 3.8 |
| Vectors, threads (what mcplay calls) | 1.8 to 2.0 |

Converted alone, 120 decoded frames each three times (two runs of each, alternated). In mcplay, beside the decoder's
threads and llvmpipe on the same 4 vCPUs, the title's conversion went from 11.0 and 12.7 ms a frame to 8.0 and 7.5
(30 s of Caminandes, the same order alternated).

**Sound against picture** (2026-11-08, issue #352). Under Xvfb with Mesa's llvmpipe, OpenAL Soft writing its mixed
output to a file (`ALSOFT_DRIVERS=wave`, the file's creation time when mixing began) while ffmpeg grabbed the screen
with wall-clock timestamps (`x11grab -use_wallclock_as_timestamps 1`); both matched against FFmpeg's decoding of the
clip (the frames, and the sound's envelope in 1 ms windows). 15 s of Big Buck Bunny (640x360, 24 fps, AAC),
re-encoded so that its picture and sound start together:

| Build | Sound against the picture, two runs |
|---|---|
| 0.9.0 | 49 and 87 ms early |
| 0.9.1 (`VideoTexture` starts the sound with the first frame) | 17 and 27 ms late |

A cut by stream copy (its video from an earlier keyframe, hidden by an edit list) gave 21 and 23 ms late with 0.9.1,
`MediaPlayer`'s audio the same as FFmpeg's sample for sample. Late by a frame or less is inside what viewers accept
(sound late is the lenient side). The very first run after boot had 234 ms of silence between OpenAL opening its
device (which `VideoTexture` does at the first frame) and the sound, then played without a gap: opening the device
cold sits on the start path.

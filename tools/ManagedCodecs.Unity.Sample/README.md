# A Unity sample

For issue #254 (docs/decisions/2026-10-31-game-engines.md): `ManagedCodecsVideo.cs`, a `MonoBehaviour` that plays an
MP4 or AVI file (H.264 or MJPEG, AAC audio) onto its object's renderer with the `ManagedCodecs.Unity` package. Not a
media player: no seeking, no pause.

**Not run in Unity.** CI builds it for netstandard2.1 against `UnityEngineStubs.cs`, which declares only the UnityEngine
members the sample calls (UnityEngine is not on NuGet). That checks its use of the decoders compiles against the Unity
build; whether Unity runs it as intended needs a Unity install, which nobody has tried yet. Please report back:
[Testing it in Unity](#testing-it-in-unity) says what to try and what to record.

## Using it

1. Add `ManagedCodecs.Unity` (NuGetForUnity, or the DLL and `System.Runtime.CompilerServices.Unsafe` in
   `Assets/Plugins`; its package page says how).
2. Copy into `Assets/`: `ManagedCodecsVideo.cs`, `IsExternalInit.cs`, and mcplay's `MediaSource.cs` and `YuvToRgba.cs`
   (`tools/ManagedCodecs.MonoGame`), which this project links. Not `UnityEngineStubs.cs`.
3. Put a video in `Assets/StreamingAssets/`, add `ManagedCodecsVideo` to an object with a renderer (a quad), and set
   its file name.

## What it does

- A thread of its own demuxes and decodes ahead (`MediaSource`, as mcplay: H.264 with frame threading, up to 4
  threads) and converts each frame to RGBA on the CPU (`YuvToRgba`, by the frame's own matrix and range).
- `Update` shows the newest frame due by a clock started at the first frame and drops late ones, through one
  `Texture2D` (`RGBA32`, `LoadRawTextureData` and `Apply`), handing each frame's pixels back to `MediaSource.Return`
  once copied or dropped, for a later frame (no new array each frame). Unity's textures start at the bottom row, so the
  material flips the image (`mainTextureScale` y -1) instead of turning every frame over in memory.
- Audio streams through an `AudioClip` (`AudioClip.Create` with a reader callback, which runs on Unity's audio thread)
  as 16-bit stereo turned to floats, made once the first audio gives the rate.
- Not for WebGL: it has no threads, and the decoder thread is the sample's design. On Android, StreamingAssets lie in
  the APK: read the file with `UnityWebRequest` into a `MemoryStream`, which `Demuxer.Open` takes.
- Speed on Mono: the package's page has the table. Audio, MJPEG and small videos play; 720p H.264 decodes at about
  12.5 frames a second on one thread.

## Testing it in Unity

What nobody has checked yet, and how. Report on issue #254 (or a new issue), one line a run.

1. **Set-up.** Unity 2022.3 LTS or Unity 6 (2021.2 is the oldest with .NET Standard 2.1), a new 3D project, Player
   settings' API Compatibility Level at .NET Standard 2.1, then the three steps of [Using it](#using-it) on a quad
   in front of the camera.
2. **Videos.** Three files, each into `Assets/StreamingAssets/`:
   - `BigBuckBunny_640x360.m4v` (H.264 and AAC, from
     [download.blender.org](https://download.blender.org/peach/bigbuckbunny_movies/BigBuckBunny_640x360.m4v.zip));
   - `tears_of_steel_720p.mov` (720p H.264 and AAC,
     [download.blender.org](https://download.blender.org/demo/movies/ToS/tears_of_steel_720p.mov));
   - `testsrc-420.avi` (MJPEG, no audio; `tools/make-samples.sh` makes it, or any MJPEG AVI).
3. **Runs.** Each video in the editor's Play mode (Mono), then in a standalone player built with the Mono backend and
   one with IL2CPP (Player settings, Scripting Backend). An Android or iOS build is a bonus (IL2CPP only on iOS; on
   Android read the file into a `MemoryStream` first, see above).
4. **Record**, for each run: the Unity version, the backend, the CPU, and the last line the sample logs (every 5 seconds
   and on stopping):
   `ManagedCodecsVideo: 1280x720, 1500 frames shown, 3 dropped, ms a frame: decode 21.7, RGBA conversion 4.1`. A frame
   is dropped when a later one was already due, so a steadily growing count means playback doesn't keep up; the two
   times (means over the run) say which of decoding and conversion is short of the frame's duration.
   Also whether the picture is upright and its colours right (compare with VLC), whether the sound plays without
   gaps and in sync, and the first error in the console, if any (an IL2CPP build error or a `MissingMethodException`
   above all: those are what Mono and IL2CPP could do differently from CoreCLR).

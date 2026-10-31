# A Unity sample

For issue #254 (docs/decisions/2026-10-31-game-engines.md): `ManagedCodecsVideo.cs`, a `MonoBehaviour` that plays an
MP4 or AVI file (H.264 or MJPEG, AAC audio) onto its object's renderer with the `ManagedCodecs.Unity` package. Not a
media player: no seeking, no pause.

**Not run in Unity.** CI builds it for netstandard2.1 against `UnityEngineStubs.cs`, which declares only the UnityEngine
members the sample calls (UnityEngine is not on NuGet). That checks its use of the decoders compiles against the Unity
build; whether Unity runs it as intended needs a Unity install, which nobody has tried yet. Please report back.

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
  `Texture2D` (`RGBA32`, `LoadRawTextureData` and `Apply`). Unity's textures start at the bottom row, so the material
  flips the image (`mainTextureScale` y -1) instead of turning every frame over in memory.
- Audio streams through an `AudioClip` (`AudioClip.Create` with a reader callback, which runs on Unity's audio thread)
  as 16-bit stereo turned to floats, made once the first audio gives the rate.
- Not for WebGL: it has no threads, and the decoder thread is the sample's design. On Android, StreamingAssets lie in
  the APK: read the file with `UnityWebRequest` into a `MemoryStream`, which `Demuxer.Open` takes.
- Speed on Mono: the package's page has the table. Audio, MJPEG and small videos play; 720p H.264 decodes at about
  12.5 frames a second on one thread.

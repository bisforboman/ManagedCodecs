# A Godot sample

For issue #254 (docs/decisions/2026-10-31-game-engines.md): an addon, `addons/managedcodecs/`, whose
`ManagedCodecsVideo` is a `TextureRect` node that plays a video (H.264 or MJPEG, AAC or MP3 audio; MP4, Matroska or AVI) into
an `ImageTexture` with the ManagedCodecs packages, on Godot 4.7's .NET build. Not a media player: no seeking, no pause.

**Run in Godot 4.7.2 on Windows (issue #402) and Linux, exported too; sound not yet checked on a desktop.** CI builds it with the Godot .NET SDK
(`Godot.NET.Sdk` and `GodotSharp` 4.7.2, from nuget.org). It has also run in Godot itself (2026-11-07), on a cloud VM
under Xvfb with Mesa's software OpenGL and no sound card: [Testing it in Godot](#testing-it-in-godot) has the results
and what is left to try. Please report back from a desktop.

## Using it

1. Godot 4.7, the .NET build, and the .NET 10 SDK (the project targets net10.0, as the packages do).
2. Open this folder (`project.godot`) in the editor, or copy `addons/managedcodecs/` into a project of your own, which
   then references the `ManagedCodecs.Playback` package (the addon's README says how). The node is a global class:
   once built, the Add Node dialog lists `ManagedCodecsVideo`.
3. Copy a video in as `video.mp4` (ignored by git), or set the node's `File Path`, and run: `Main.tscn` is a
   `TextureRect` filling the window.
4. Export: `export_presets.cfg` has a Linux preset that packs `*.mp4` into the `.pck` (a video isn't a Godot resource,
   so the default filter leaves it out), and `ManagedCodecs.Godot.sln` is the solution Godot builds the C# from
   (`godot --headless --path . --export-release Linux <dir>/game.x86_64`, with the .NET export templates installed).

## What it does

- `_Ready` reads the file through Godot's `FileAccess` (`res://` from the project folder in the editor, from the
  `.pck` in an exported game) into a `MemoryStream`, which `MediaPlayer` takes. The whole file is in memory: right for
  cutscenes, not for films.
- `ManagedCodecs.Playback`'s `MediaPlayer` demuxes and decodes ahead on a thread of its own (H.264 with frame threading,
  up to 4 threads) and converts each frame to RGBA on the CPU, by the frame's own matrix and range.
- `_Process` shows the newest frame due by a clock started at the first frame (summing the frames' `delta`;
  `MediaPlayer.TakeFrame` drops late ones), through one `ImageTexture` (`Image.SetData` and `ImageTexture.Update` each
  frame), handing each frame's pixels back to `MediaPlayer.Return`. Godot's images start at the top row, as the frames do. The rectangle keeps the
  aspect ratio as shown (the frame's width stretched by its sample aspect ratio).
- Audio: an `AudioStreamPlayer` child with an `AudioStreamGenerator` at the decoded rate, made once the first frame
  is shown (so that the sound starts with the picture's clock; the audio decoded before it is held), filled each frame
  with as many stereo frames as it takes (`PushBuffer`).
- `Loop` (exported) plays the file again from its start, for a menu's backdrop.
- Rotation (a phone video's) isn't applied: turn the node by `MediaPlayer.Video.Rotation` if you need it.
- Web exports: Godot's C# can't export to the web yet.

## The Asset Library

`listing/` (which Godot ignores, `.gdignore`) holds the listing's icon and previews and the text to submit;
`tools/godot-addon.sh` makes the zip it downloads, which each GitHub release carries.

## Testing it in Godot

What has been checked, and how to check the rest. Report in a new issue, one line a run (issue #254, now closed, has
the history and the MonoGame measurements).

1. **Set-up.** Godot 4.7 (.NET), the .NET 10 SDK, this folder opened in the editor.
2. **Videos**, each copied in as `video.mp4` (or the `File Path` set to it):
   - `BigBuckBunny_640x360.m4v` (H.264 and AAC, from
     [download.blender.org](https://download.blender.org/peach/bigbuckbunny_movies/BigBuckBunny_640x360.m4v.zip));
   - `tears_of_steel_720p.mov` (1280x534 H.264 and MP3; [download.blender.org](https://download.blender.org/demo/movies/ToS/tears_of_steel_720p.mov));
   - `testsrc-420.avi` (MJPEG, no audio; `tools/make-samples.sh` makes it).
3. **Runs.** Each in the editor (F5), then in an exported desktop build (the export templates for .NET).
4. **Record**, for each run: the Godot version, the OS and CPU, and the last line the sample prints (every 5 seconds and
   on quitting): `ManagedCodecsVideo: 1280x720, 1500 frames shown, 3 dropped, ms a frame: decode 9.1, RGBA conversion
   2.0`. A steadily growing dropped count means playback doesn't keep up. The decode time is the decoding thread's wait
   for the decoder's threads, near zero while they keep ahead, so it can't be compared across resolutions; the RGBA
   conversion's is its cost a frame. The csproj builds the decoders in Release whatever Godot's configuration (issue
   #402: Debug ones were about 4 times slower at 1080p).
   Also whether the picture is upright and its colours right (compare with VLC), whether the sound plays without gaps
   and in sync, and the first error in the output panel, if any.

**Run so far** (2026-11-07, issue #352): Godot 4.7.2 .NET on Linux (a 4-core cloud VM, an Intel Xeon at 2.1 GHz),
under Xvfb with Mesa's llvmpipe (`--rendering-driver opengl3`) and no sound card, in the editor's runner and as an
exported release build (the rows that say so). Movie Maker mode (`--write-movie out.avi --fixed-fps N`) recorded what the game shows and plays, which
was compared with FFmpeg's decoding of the same file:

| Video | Run | Last line | Picture | Sound |
| --- | --- | --- | --- | --- |
| `BigBuckBunny_640x360.m4v` | Movie Maker, 24 fps, 20 s | 640x359, 467 frames shown, 0 dropped, decode 0.8, RGBA conversion 3.7 | upright; a frame 29.5 dB from FFmpeg's (after Movie Maker's MJPEG and scaling), mean colour within 0.5 of 255 | no gap; starts 6 ms after the picture |
| `BigBuckBunny_640x360.m4v` | Movie Maker, 60 fps, 20 s | 640x359, 473 frames shown, 0 dropped | | |
| `tears_of_steel_720p.mov` | Movie Maker, 24 fps, 20 s | 1280x534, 471 frames shown, 0 dropped, decode 2.5, RGBA conversion 6.6 | | none (MP3) |
| `tears_of_steel_720p.mov` | live, 16 s | 1280x534, 384 frames shown, 0 dropped, decode 2.0, RGBA conversion 6.3 | | none (MP3) |
| `BigBuckBunny_640x360.m4v` | exported release build, Movie Maker, 24 fps, 20 s | 640x359, 468 frames shown, 0 dropped, decode 0.7, RGBA conversion 3.5 | | starts 6 ms after the picture |
| `BigBuckBunny_640x360.m4v` | exported release build, live | 640x359, 266 frames shown, 0 dropped | | |
| `testsrc-420.avi` | Movie Maker, 25 fps, 12 s | 320x240, 50 frames shown, 0 dropped | upright, pillarboxed at 4:3; 29.6 dB, mean colour within 0.2 | (none in the file) |

That run found four faults, fixed since: a game ticking at the video's own rate dropped about 1 frame in 5
(`MediaPlayer.TakeFrame` took a frame as due only from its exact time, which the summed clock falls a rounding error
short of; it now shows the frame nearest the clock), the sound started about 280 ms before the picture (it now waits
for the first frame), opening the folder in the editor wrote `net8.0` into the `.csproj` (it now names
`net10.0` itself), and an export left out the C# (no solution file) and the video (not a resource); the folder now has
both a solution and an export preset.

The table's times are a Debug build's decoders: Godot builds through the solution, where the csproj's Release setting
for its references was stripped (2026-11-08; fixed since, `ShouldUnsetParentConfigurationAndPlatform`). With the
decoders in Release, the 640x360 clip's RGBA conversion takes 1.0 ms a frame, against 3 to 4.

Windows: issue #402 (480p and 1080p H.264, the picture; the decoders built in Release since). Still to try: a desktop
with a sound card (does the sound play live without gaps), and macOS.

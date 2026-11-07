# A Godot sample

For issue #254 (docs/decisions/2026-10-31-game-engines.md): `ManagedCodecsVideo.cs`, a `TextureRect` node that plays a
video (H.264 or MJPEG, AAC audio; MP4, Matroska or AVI) into an `ImageTexture` with the ManagedCodecs packages, on
Godot 4.7's .NET build. Not a media player: no seeking, no pause.

**Not run in Godot.** CI builds it with the Godot .NET SDK (`Godot.NET.Sdk` and `GodotSharp` 4.7.2, from nuget.org), so
it compiles against Godot's real C# API. Whether Godot runs it as intended needs the Godot editor, which nobody has
tried with it yet. Please report back: [Testing it in Godot](#testing-it-in-godot) says what to try and what to record.

## Using it

1. Godot 4.7, the .NET build, and the .NET 10 SDK (the project targets net10.0, as the packages do).
2. Open this folder (`project.godot`) in the editor, or copy `ManagedCodecsVideo.cs` into a project of your own, which
   then references the `ManagedCodecs.Playback` package.
3. Copy a video in as `video.mp4` (ignored by git), or set the node's `File Path`, and run: `Main.tscn` is a
   `TextureRect` filling the window.

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
- Audio: an `AudioStreamPlayer` child with an `AudioStreamGenerator` at the decoded rate, made once the first audio
  comes, filled each frame with as many stereo frames as it takes (`PushBuffer`).
- `Loop` (exported) plays the file again from its start, for a menu's backdrop.
- Rotation (a phone video's) isn't applied: turn the node by `MediaPlayer.Video.Rotation` if you need it.
- Web exports: Godot's C# can't export to the web yet.

## Testing it in Godot

What nobody has checked yet, and how. Report on issue #254 (or a new issue), one line a run.

1. **Set-up.** Godot 4.7 (.NET), the .NET 10 SDK, this folder opened in the editor.
2. **Videos**, each copied in as `video.mp4` (or the `File Path` set to it):
   - `BigBuckBunny_640x360.m4v` (H.264 and AAC, from
     [download.blender.org](https://download.blender.org/peach/bigbuckbunny_movies/BigBuckBunny_640x360.m4v.zip));
   - `tears_of_steel_720p.mov` (720p H.264 and AAC,
     [download.blender.org](https://download.blender.org/demo/movies/ToS/tears_of_steel_720p.mov));
   - `testsrc-420.avi` (MJPEG, no audio; `tools/make-samples.sh` makes it).
3. **Runs.** Each in the editor (F5), then in an exported desktop build (the export templates for .NET).
4. **Record**, for each run: the Godot version, the OS and CPU, and the last line the sample prints (every 5 seconds and
   on quitting): `ManagedCodecsVideo: 1280x720, 1500 frames shown, 3 dropped, ms a frame: decode 9.1, RGBA conversion
   2.0`. A steadily growing dropped count means playback doesn't keep up; the two times say which part is short.
   Also whether the picture is upright and its colours right (compare with VLC), whether the sound plays without gaps
   and in sync, and the first error in the output panel, if any.

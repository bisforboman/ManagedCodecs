# ManagedCodecs.Playback

Video in a game, in pure managed C#: an MP4, Matroska, WebM or AVI file (H.264 or MJPEG video, AAC or MP3 audio) decoded on a
thread of its own into RGBA frames for a texture and float stereo for a streaming sound. No native binaries and no
engine's API, so it plays the same in MonoGame, FNA, Godot .NET and Stride, on every platform .NET 10 runs on. For
Unity, `ManagedCodecs.Unity` holds the same `MediaPlayer`.

```csharp
using ManagedCodecs.Playback;

using var player = new MediaPlayer("intro.mp4");
player.Start(); // the decoding thread

// Each frame of the game, with the game's own clock (time played since the first frame was shown):
if (player.TakeFrame(clock.Elapsed) is { } frame)
{
    texture.SetData(frame.Pixels); // frame.Width x frame.Height, R, G, B, A from the top row
    player.Return(frame);          // its pixels back, for a later frame
}

while (player.TakeAudio() is { } audio)
{
    // audio.Samples: left, right, left, right... (-1 to 1) at audio.SampleRate, to the engine's streaming sound
}

if (player.IsFinished) { /* the video has ended; player.Error says why if it ended early */ }
```

- **Timing:** `TakeFrame` gives the newest frame due by the clock you pass (the first frame is due at zero), and drops
  the ones that are already late, as a player does when it can't keep up. Pause by stopping your clock.
- **Bounded:** the decoder runs up to 8 frames and about 32 audio chunks ahead, then waits. Take the audio, or drop it,
  even with no sound device.
- **Speed:** H.264 with frame threading (by default one thread fewer than the processors, at most 4). On 4 vCPUs at
  2.1 GHz, 1080p24 and 1080p30 keep up, conversion to RGBA included (about 2 ms a 1080p frame).
- **Colours:** each frame by its own matrix and range, and where the stream says nothing as players assume (BT.709 from
  720 lines, BT.601 below, limited range); `PlaybackOptions.Matrix` and `Range` override them. HDR is not tone mapped.
- **Shape:** `frame.SampleAspectRatio` (anamorphic video) and `player.Video.Rotation` (phone video) are for you to
  apply when drawing.
- **Damaged files:** play up to the damage, then end with the exception in `Error`, never thrown at the game loop.
- **Not a media player:** no seeking. `PlaybackOptions.Loop` plays the file again from its start, the frames' times
  running on.

## Live cameras

`LivePlayer` shows a camera serving MJPEG over HTTP (ESP32-CAM, mjpg-streamer, OctoPrint, most IP cameras'
`video.mjpg`): the UI takes the newest frame on its render tick, older ones are dropped and counted, and a dropped or
stalled connection is made again.

```csharp
using var player = new LivePlayer(new MjpegHttpSource(new Uri("http://camera.local/video.mjpg")));
player.Start();

// On each render tick:
if (player.TakeLatest() is { } frame)
{
    // frame.Pixels: frame.Width x frame.Height, R, G, B, A from the top row, into the UI's bitmap
    player.Return(frame);
}
// player.State (Connecting, Playing, Reconnecting, Failed) and player.Error for a status line.
```

Samples for MonoGame, Godot 4 .NET and Unity are in the repository's `tools/` folder. The
[README](https://github.com/bisforboman/ManagedCodecs#in-game-video) has the rest.

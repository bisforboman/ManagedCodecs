# ManagedCodecs.MonoGame

Video for MonoGame games, in pure managed C#: `VideoTexture` plays an MP4, Matroska, WebM or AVI file (H.264 or MJPEG
video, AAC or MP3 audio) into a `Texture2D` and a `DynamicSoundEffectInstance`. No native binaries and no codec of the
platform's, so it plays the same everywhere. The decoding runs on a thread of its own (`ManagedCodecs.Playback`).

```csharp
using ManagedCodecs.MonoGame;

VideoTexture video;

protected override void LoadContent()
{
    video = new VideoTexture(GraphicsDevice, "Content/intro.mp4");
    video.Play();
}

protected override void Update(GameTime gameTime)
{
    video.Update(gameTime); // the frame due into video.Texture, the audio queued
    if (video.IsFinished) { /* on to the game */ }
    base.Update(gameTime);
}

protected override void Draw(GameTime gameTime)
{
    GraphicsDevice.Clear(Color.Black);
    spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
    video.Draw(spriteBatch, GraphicsDevice.Viewport.Bounds); // letterboxed, aspect ratio and rotation applied
    spriteBatch.End();
    base.Draw(gameTime);
}
```

- **The clock is the game's:** `Update` moves it by `gameTime.ElapsedGameTime` while playing, so `Pause()` stops
  picture and sound together, and frames due while the game was busy are dropped rather than shown late.
- **Sound-only files:** `AudioTrack` plays a `.wav`, raw MP3 or AAC file (or any file's audio) the same way:
  `Play()`, then `Update()` once a frame; its `Position` follows the sound.
- **Requirements:** .NET 10 (the decoders' target) and MonoGame 3.8, any platform package: this one is compiled against
  MonoGame's API and doesn't bring a platform of its own.
- **Speed:** on 4 vCPUs at 2.1 GHz, 1080p24 and 1080p30 keep up, conversion to RGBA included.
- **Options:** `PlaybackOptions` (`Loop` for a menu's backdrop, `Matrix` and `Range` for the colours, `DecoderThreads`);
  `Volume`; `Player` for the stream's facts and the decoding's timings.
- **Without an audio device** the video plays alone. **A damaged file** plays up to the damage, then ends with the
  exception in `Player.Error`.
- **Not a media player:** no seeking.

The [README](https://github.com/bisforboman/ManagedCodecs#in-game-video) has the rest; `mcplay`, the repository's
MonoGame sample, is a `Game` around it.

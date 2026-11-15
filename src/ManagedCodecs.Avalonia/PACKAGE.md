# ManagedCodecs.Avalonia

Live cameras in Avalonia, in pure managed C#: `LiveView` shows a `LivePlayer`'s newest frame (ManagedCodecs.Playback:
MJPEG over HTTP, H.264 or JPEG over RTSP and RTSPS). No native libraries and no codec of the platform's, so it shows
the same picture on Windows, macOS and Linux, and in a trimmed or Native AOT app.

```csharp
using ManagedCodecs.Avalonia;
using ManagedCodecs.Playback;

var player = new LivePlayer(new RtspSource(new Uri("rtsp://user:password@camera/stream1")));
player.Start();
window.Content = new LiveView { Player = player }; // Stretch.Uniform, as an Image
// ... and when the window closes:
player.Dispose();
```

- **The application owns the player:** it makes it (its source, credentials, `CertificateHash` for RTSPS, `Audio`),
  starts it and disposes it. The control only shows frames: on each frame Avalonia renders, the newest decoded one,
  older ones dropped. Several views may show one player in turn, but each frame goes to one of them.
- **Layout as an `Image`:** `Stretch` (`Uniform` by default) and `StretchDirection`.
- **Sound:** Avalonia has no way to play raw samples, so `LivePlayer.TakeAudio()` is the application's to play.
- **Requirements:** .NET 10 and Avalonia 12.
- More: [the repository](https://github.com/bisforboman/ManagedCodecs) (README, Live cameras).

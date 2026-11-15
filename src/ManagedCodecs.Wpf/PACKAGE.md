# ManagedCodecs.Wpf

Live cameras in WPF, in pure managed C#: `LiveView` shows a `LivePlayer`'s newest frame (ManagedCodecs.Playback: MJPEG
over HTTP, H.264 or JPEG over RTSP and RTSPS). No native libraries and no codec of the platform's.

```csharp
using ManagedCodecs.Playback;
using ManagedCodecs.Wpf;

var player = new LivePlayer(new RtspSource(new Uri("rtsp://user:password@camera/stream1")));
player.Start();
window.Content = new LiveView { Player = player }; // Stretch.Uniform, as an Image
// ... and when the window closes:
player.Dispose();
```

- **The application owns the player:** it makes it (its source, credentials, `CertificateHash` for RTSPS, `Audio`),
  starts it and disposes it. The control only shows frames: on each frame WPF renders while it is loaded, the newest
  decoded one, older ones dropped. Several views may show one player in turn, but each frame goes to one of them.
- **Layout as an `Image`:** `Stretch` (`Uniform` by default) and `StretchDirection`.
- **Sound:** WPF has no way to play raw samples without a platform API, so `LivePlayer.TakeAudio()` is the
  application's to play.
- **Requirements:** .NET 10 on Windows (`net10.0-windows`). Built and packed in CI on Linux; not yet run on Windows.
- More: [the repository](https://github.com/bisforboman/ManagedCodecs) (README, Live cameras).

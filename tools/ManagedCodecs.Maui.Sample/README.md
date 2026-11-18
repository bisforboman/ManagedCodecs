# mclive-maui: a live camera in .NET MAUI

A sample for issue #526 (docs/decisions/2026-11-17-live-maui.md): shows a camera serving MJPEG over HTTP or H.264 or
JPEG over RTSP or RTSPS with `ManagedCodecs.Playback`'s `LivePlayer`, in a MAUI app on Android, Windows, iOS and Mac
Catalyst. A sample
to copy, not a package: `LiveView.cs`, `LiveViewHandler.cs` and the handler of each platform you need
(`Platforms/*/LiveViewHandler.*.cs`).

- **`LiveView`** (a MAUI `View`): `Player`, `Aspect` (as `Image`'s: `AspectFit` by default) and `ShownFrames`. The
  application makes, starts and disposes the player, as with the Avalonia and WPF `LiveView`s.
- **Its handler, one per platform**, takes the newest frame on each frame the platform draws and copies it into a
  native bitmap, which the platform's own image view scales:
  - Android: an `ImageView` showing a `Bitmap` (`ARGB_8888`, which is red, green, blue, alpha in memory: no swap),
    filled through a direct buffer; `Choreographer` paces it.
  - Windows: WinUI's `Image` showing a `WriteableBitmap` (blue, green, red, alpha: red and blue swapped, as in WPF);
    `CompositionTarget.Rendering` paces it.
  - iOS and Mac Catalyst (one handler, Apple/): a `UIView` whose layer shows a `CGImage` of the frame (made
    from a bitmap context of the frame's size, red, green, blue and a skipped alpha: no swap), a new image per frame
    as a layer's contents can't change in place; Core Animation scales it (`ContentsGravity`); `CADisplayLink` paces
    it.
- **What each platform needs declared** (Platforms/Android/AndroidManifest.xml): `INTERNET`;
  `usesCleartextTraffic` for MJPEG over `http://` (Android's HTTP stack refuses clear text from Android 9 without it;
  RTSP's sockets aren't subject to it); `CHANGE_WIFI_MULTICAST_STATE` and a `MulticastLock` while playing RTSP, for
  multicast on Wi-Fi (`LivePage`). Windows needs nothing: a MAUI app there is full trust. iOS and Mac Catalyst
  (Platforms/iOS/Info.plist, Platforms/MacCatalyst/Info.plist): `NSLocalNetworkUsageDescription`, the text of the
  question the system asks before the first connection to the local network, and App Transport Security's
  `NSAllowsLocalNetworking` for MJPEG over `http://` on the local network; Mac Catalyst's sandbox
  (Platforms/MacCatalyst/Entitlements.plist) needs `network.client` and, for RTP over UDP, `network.server`. RTSP
  multicast there needs Apple's `com.apple.developer.networking.multicast` entitlement, granted on request: not in
  the sample.

## Running it

It isn't in ManagedCodecs.slnx: it needs MAUI's workloads; the Windows target builds on Windows only, iOS and Mac
Catalyst on macOS with Xcode.

    dotnet workload install maui-android --version 10.0.115     # or maui-windows on Windows
    dotnet build tools/ManagedCodecs.Maui.Sample -f net10.0-android -c Release   # needs the Android SDK (ANDROID_HOME)
    adb install -r tools/ManagedCodecs.Maui.Sample/bin/Release/net10.0-android/io.github.managedcodecs.mclive-Signed.apk

Started from the launcher, it asks for the URL. With one given it plays at once:

    adb shell am start -n io.github.managedcodecs.mclive/.MainActivity -e url http://camera.local/video.mjpg
    mclive-maui.exe rtsps://user:password@camera.local/stream --certificate-hash <64 hex digits>
    mclive-maui.app/Contents/MacOS/mclive-maui http://camera.local/video.mjpg              # Mac Catalyst
    xcrun simctl launch --console-pty booted io.github.managedcodecs.mclive http://camera.local/video.mjpg

## Its check of itself

With a number of frames (`--frames N` on Windows, `--ei frames N` on Android) the page takes each frame from the
player, hands it to the handler, and compares what the platform drew of the view with the decoded frame
(`FrameCheck`). The view is drawn at its own size and filtered, so not pixel for pixel as mclive-wpf does: an 8 by 8
grid of cells, each cell's mean colour within 10 levels, which fails on red and blue swapped, rows of the wrong length
and a missing picture. After N frames it writes two lines starting `mclive-maui:` to standard output (Android's log,
tag DOTNET) and exits: 0, 1 if the player gave up, 2 if a frame was drawn wrong (on Windows its one such run ended
with 1 instead, so the workflow goes by the text). `.github/workflows/maui.yml` runs it on an Android emulator, on
the Windows runner, and on a macOS runner as the Mac Catalyst app and in an iPhone simulator, against
`tools/mjpeg-http-server.py` (results: docs/conformance.md, MJPEG over HTTP).

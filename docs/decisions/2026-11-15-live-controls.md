# Live camera viewing: packaged Avalonia and WPF controls (2026-11-15)

A plan, decided on by the owner (Decided, below), and built in the same pull request. Issue #526, point 2. Live cameras play
in two samples, `tools/ManagedCodecs.Avalonia.Sample` (`mclive`) and `tools/ManagedCodecs.Wpf.Sample` (`mclive-wpf`),
not in packages: the demand survey on #462 advised packaging a control only once someone asks for one, to keep
Avalonia's and WPF's version churn out of the package set and its release cadence. Nobody has asked yet.

## What the samples do

Each is one window of about 100 lines, the same five steps:

1. Make a `LivePlayer` for the URL (`RtspSource` or `MjpegHttpSource`) and start it.
2. On each frame the UI renders (Avalonia's `TopLevel.RequestAnimationFrame`, WPF's `CompositionTarget.Rendering`),
   take the newest frame (`TakeLatest`).
3. Copy it into a `WriteableBitmap`, a new one when the size changes: Avalonia row by row (`Rgba8888`, its rows may be
   padded), WPF after swapping red and blue in place (it has no RGBA format: `Bgra32`).
4. Give the frame back (`Return`), so its pixels are reused.
5. Dispose the player when the window closes.

Neither plays sound: Avalonia and WPF have no way to play raw samples without a native library or a platform API
(mcplay, the MonoGame sample, plays it: #534). And both build an `RtspSource` only for `rtsp://`, so an `rtsps://` URL
falls through to the MJPEG path: a fix for the samples whatever is decided here.

## What a package would add

- **A control** that does steps 2 to 4 itself, drawn like an `Image` (`Stretch`), so an application places a camera
  view in its layout as it would a picture, and steps 1 and 5 according to decision 2.
- **Its own package per framework**: `ManagedCodecs.Avalonia` (depends on Avalonia) and `ManagedCodecs.Wpf`
  (net10.0-windows, `UseWPF`), each with PublicAPI files, package validation and the release's one version.
- **The coupling the survey warned of**: every package here ships at one version, so a breaking Avalonia release
  (its next major) needs a new minor version of all packages, and the control's dependency range has to say which
  Avalonia it takes. WPF changes rarely.
- **Tests**: Avalonia has a headless platform (`Avalonia.Headless`) in which a control can be laid out and rendered
  without a display, so a test can check that a frame from a loopback camera reaches the bitmap. WPF only builds on
  Linux (`EnableWindowsTargeting`); nothing runs it in CI, as for `mclive-wpf` today.
- **Not covered**: sound (above), MAUI (#526 point 4), and recording or snapshots.

## Proposal

1. **Wait for someone to ask** (decision 1): as the survey said; the samples stay the way to copy, with the `rtsps://`
   fix. If built:
2. **The control's API** (decision 2): a `LiveView` that takes a `LivePlayer` the application makes, starts and
   disposes (`Player` property): one class whatever the source (MJPEG, RTSP, RTSPS with its certificate pin), and no
   lifetime surprises. It shows the newest frame, and `Stretch` as `Image` has.
3. **Avalonia first**, with a headless test; WPF after, built in CI only.

## Built

- `ManagedCodecs.Avalonia`'s `LiveView` (a `Control`) and `ManagedCodecs.Wpf`'s (a `FrameworkElement`): `Player`,
  `Stretch` (`Uniform` by default), `StretchDirection` and `ShownFrames`. Each takes the newest frame on each frame the
  UI renders while it is in a window, copies it into a bitmap (made anew when the size changes), gives it back, and
  draws it as `Image` draws its source.
- Both packages depend on ManagedCodecs.Playback at the release's one version, and on Avalonia 12.1.6 or later
  (the version they are built against). Neither has a version on nuget.org yet, so their package validation runs
  without a baseline (`PackageValidationBaselineVersion` empty in their projects) until the release that first
  ships them; the baseline pull request after it removes the override. nuget.org's trusted publishing has to accept
  the two new package IDs before that release pushes them.
- `LiveViewTests` renders the Avalonia control in Avalonia's headless platform with Skia drawing: a window holding a
  `LiveView` whose player reads a loopback MJPEG server, captured, its pixels the decoded frame's within a level (a
  test that fails without the copy into the bitmap, or without the drawing). The WPF control is built and packed in
  CI only.
- The samples (`mclive`, `mclive-wpf`) use the controls, and take `rtsps://` URLs, which they used to send down the
  MJPEG path. `mclive` showed 50 frames through the control under Xvfb against FFmpeg's `-f mpjpeg`.

## Decided

Asked with the multiple-choice prompt (2026-11-15).

1. **Build now, or wait. Decided: (c), both now** (against the survey's advice to wait for an ask).
   - **(a) Wait for someone to ask** (recommended): the survey's advice, and nothing has changed it; the samples get
     the `rtsps://` fix and stay as they are.
   - (b) Build the Avalonia control now, WPF after.
   - (c) Build both now.
2. **Who owns the player. Decided: (a), the application.**
   - **(a) The application** (recommended): `LiveView.Player` takes a `LivePlayer` it made and started, and disposes
     itself; the control only shows frames. Every source and option the player has works, with nothing new to learn.
   - (b) The control: a `Source` (a URL) it makes the player from, starting on attach and stopping on detach. Shorter
     for the simple case, but each option of `RtspSource` (credentials, audio, the certificate pin, timeouts) needs a
     property of its own.

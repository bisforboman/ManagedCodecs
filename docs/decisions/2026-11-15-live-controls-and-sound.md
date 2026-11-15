# Live cameras: packaged controls, MAUI and sound in the samples (2026-11-15)

A plan for issue #526, split from #462, decided on by the owner (Decided, below). Nothing here is built yet. `LivePlayer` is as released in 0.17.0 (`TakeLatest`, `Return`, `TakeAudio`, `Audio`, `Statistics`);
another session is changing it for lip sync (#527), so nothing here edits the Playback package.

## Where it stands

- `mclive` (tools/ManagedCodecs.Avalonia.Sample) and `mclive-wpf` (tools/ManagedCodecs.Wpf.Sample) each show a
  `LivePlayer` in a window, about 100 lines of drawing code each. Both are silent.
- `mclive` has run under Xvfb against FFmpeg's and GStreamer's servers (docs/conformance.md). `mclive-wpf` is built in
  CI on Linux (`EnableWindowsTargeting`) and has never run: every CI job is on Ubuntu.
- `ManagedCodecs.MonoGame` has `VideoTexture` and `AudioTrack`, for files. `mcplay` (its sample) plays files only. It
  runs under Xvfb with Mesa's software renderer, and with `ALSOFT_DRIVERS=null` OpenAL plays into nothing.
- The survey on #462 recommended samples "until someone asks for a packaged control". Nobody has: #526 has no comments
  and no other issue asks.

## The four items

### 1. Run `mclive-wpf` on Windows

- **By CI, not by hand** (recommended): a job on `windows-latest`, whose runner has a desktop session that WPF can
  open a window in. `mclive-wpf` gains `--frames N` (as `mclive` has): it exits 0 after N frames shown, 1 if the
  player failed. The job serves MJPEG over HTTP on the loopback and runs it. A workflow of its own, on demand and
  weekly, not a required check: a Windows runner would otherwise hold up every PR.
- What it would show: the window opens, `WriteableBitmap.WritePixels` takes the frames, and the counts are those of
  the stream. It wouldn't show that the picture looks right: red and blue are swapped by hand for `Bgra32`, and only
  a look at the window, or a pixel read back from the bitmap, says the swap is the right way round. `--frames` can
  check one pixel of the bitmap against the frame it came from.
- **Not verifiable from this session**: the cloud sandbox is Linux. The workflow itself can only be tried by running
  it on GitHub after it merges (or from the PR's branch with `workflow_dispatch`).

### 2. Packaged controls

What a package would be, if the owner wants one:

- **`ManagedCodecs.Avalonia`**: `LiveView`, a `Control` with a `Player` property (a `LivePlayer` the application
  makes, starts and disposes) and a `Stretch`. Each frame Avalonia renders it takes the newest frame into a
  `WriteableBitmap` and draws it, as `mclive` does now. The application keeps the player, so its `RtspSource`
  (credentials, `Audio`, a pinned certificate), `Statistics` and `Error` need nothing from the control. That is two
  public properties.
  - Not proposed: a control that takes a URL and owns the player. It would have to mirror every `RtspSource` setting
    as a property, and grow with each one.
  - Checked by Avalonia.Headless on Linux: the bitmap's pixels are the frame's, a size change makes a new bitmap,
    detaching stops the render callbacks.
- **`ManagedCodecs.Wpf`**: the same as a `FrameworkElement`, `net10.0-windows`. Packs on Linux. **No test of it can
  run on Linux**, so it needs item 1's Windows job first, running its tests.
- **What each costs**:
  - release.yml's `Pack` loop, ci.yml's package validation, `PublicAPI.*.txt`, a PACKAGE.md, README and CHANGELOG.
  - A package compiled against Avalonia 12 doesn't load in an Avalonia 11 application. Each Avalonia major is then a
    break here, or a second package. WPF follows .NET's version: `net10.0-windows` now, another target with .NET 11.
  - Against that, what it saves an application is about 40 lines (`Show` in each sample's LiveWindow.cs).
- **Recommendation: not yet.** The survey's condition (someone asks) hasn't been met, the saving is small and the
  samples already show the lines to copy. If one is wanted, Avalonia first: it can be tested here, and it is where the
  survey found the pain (Linux, ARM panels).

### 3. Sound

- **A live mode in `mcplay`** (recommended): `mcplay rtsp://camera/stream` or an `http://` MJPEG URL plays a
  `LivePlayer` with `RtspSource.Audio` on. Each `Update` takes the newest frame into a `Texture2D` and queues
  `TakeAudio()`'s chunks into a `DynamicSoundEffectInstance`, as `VideoTexture` does for a file. No clock and no
  sync, as the player has none (docs/decisions/2026-11-15-live-rtsp-audio.md, decision 3). The title shows the state
  and the statistics, the audio counts among them.
- **Sample only, or public** (decision 3):
  - An internal class in the sample (recommended): no API. Lip sync (#527) may change what a chunk carries, and a
    public class now would have to follow it.
  - A public `LiveTexture` in ManagedCodecs.MonoGame beside `VideoTexture`: new API, so the next minor version.
- **One rule beyond the file player's**: the camera's clock and the sound card's differ. If the queued sound grows
  past a bound (the camera is faster, or the game stalled), the oldest chunks are dropped before queueing, or latency
  grows for as long as the window is open. `LivePlayer` already bounds its own queue to half a second. The sample
  adds the same bound on what it has handed to the sound.
- **What can be checked here**: under Xvfb with `ALSOFT_DRIVERS=null`, against the loopback RTSP server the tests
  use or mediamtx: pictures shown, chunks queued, none dropped at a steady rate. OpenAL Soft's `wave` driver writes
  what it mixed to a file (mcplay's README used it for sound against picture in files), so the sound itself can be
  compared with the clip's. **Nobody will have heard it** from this session.
- **Not proposed**: sound in `mclive-wpf` through `winmm`'s `waveOut` by P/Invoke. It is a platform API and ships
  nothing native, but it can't be run here, and it is more sample code than the picture's. Avalonia has nothing of
  the kind across platforms.

### 4. MAUI

- MAUI has no writeable bitmap of its own. A control needs a handler per platform (Android's `Bitmap`, iOS's
  `CGImage`, WinUI's `WriteableBitmap`) or SkiaSharp, which is a native library.
- Only the Android target builds on Linux, with a workload CI doesn't have, and nothing of it runs here or in CI
  without an emulator.
- The survey counted 19 issues for `rtsp maui`, against 132 for WPF.
- **Recommendation: not now.** It stays in #526 as an idea until someone asks.

## Proposed order

1. Sound: the live mode in `mcplay` (item 3). No API, one sample, checked here as far as a machine without speakers
   can.
2. The Windows run of `mclive-wpf` in a workflow (item 1). It also is what a WPF package would need first.
3. Packages (item 2) only if the owner decides for them. MAUI not now.

Each is a PR of its own. README.md, CHANGELOG.md, ManagedCodecs.slnx and the workflows are shared with the sessions on
#527 and #469: each PR checks the open PRs first and keeps its edits there to a line or two.

## Decided

Asked with the multiple-choice prompt; the owner took each recommendation (2026-11-15).

1. **Which item first. Decided: (a), sound, the live mode in `mcplay`.**
   - **(a) Sound: the live mode in `mcplay`** (recommended).
   - (b) The Windows run of `mclive-wpf` in a workflow.
   - (c) A packaged Avalonia control.
   - (d) MAUI.
2. **Packaged controls. Decided: (a), not yet.**
   - **(a) Not yet: they stay samples** (recommended). Nobody has asked, and each package follows a UI framework's
     versions.
   - (b) `ManagedCodecs.Avalonia` only, with `LiveView` as above.
   - (c) `ManagedCodecs.Avalonia` and `ManagedCodecs.Wpf`, the WPF one after the Windows job exists.
3. **Live playing in MonoGame. Decided: (a), in the sample only.**
   - **(a) In the sample only** (recommended).
   - (b) A public `LiveTexture` in ManagedCodecs.MonoGame, in the next minor version.
4. **`mclive-wpf` on Windows. Decided: (a), a workflow.**
   - **(a) A workflow on `windows-latest`, on demand and weekly** (recommended).
   - (b) The owner runs it once by hand and the result goes in docs/conformance.md.
   - (c) Leave it as it is, and README goes on saying nobody has run it.

So no new package and no public API: nothing for PublicAPI.Unshipped.txt, release.yml or package validation.

## Since

- **Item 3 was built twice** (2026-11-15). While this plan's session was building the live mode, another session
  opened PR #534 with one (`LiveView.cs` in the sample: `mcplay <rtsp://...>`, `--certificate-hash`). The version
  built here wasn't pushed: #534 changes the same files.
- **Measured, both versions** (2026-11-15, the cloud VM under Xvfb): mediamtx 1.21.3 serving FFmpeg's push of a
  640x360 H.264 clip with a 440 Hz tone as 48 kHz AAC (3 or 4 frames a packet), OpenAL Soft writing what it mixed to a
  file (`drivers=wave`), 40 s a run, the two alternated. Gaps in the tone (`silencedetect`, 3 ms or more below
  -50 dB):

  | Version | Gaps in 40 s, three runs |
  |---|---|
  | #534: the sound starts with the first chunk, at most 6 chunks queued (128 ms at 48 kHz) | 6, 7 and 3, each about 19 ms (one of OpenAL's mixing periods) |
  | Here: the sound starts once 100 ms is queued (and again after running dry), at most 250 ms queued | none |

  FFmpeg's packets carry 64 to 85 ms and arrive a packet at a time, so a sound started on the first chunk has nothing
  queued when the next packet is a few milliseconds late. With 16 kHz AAC (5 frames a packet, 320 ms) and with A-law
  the version here also played without running dry (one run each, the machine busy with make-samples.sh).
- **Follow-up to #534**, once it has merged: the start rule and the bound by time, and the measurement in the sample's
  README. Nobody has heard either version: the VM has no sound device.
- **Built** (2026-11-15):
  - Item 3, sound: #534 (the other session's live mode in `mcplay`), then #539, the follow-up above. On the merged
    code the tone had 6 and 4 gaps in two more runs, and none with #539. The numbers are in the sample's README.
  - Item 1, Windows: #538. `mclive-wpf --frames N` compares each frame with what WPF draws of the bitmap,
    `tools/mjpeg-http-server.py` serves committed JPEGs without FFmpeg, and `.github/workflows/windows.yml` runs the
    two on `windows-2025`. Its first run showed 100 frames, each drawn as decoded, and a run without the swap of red
    and blue failed at the first frame (docs/conformance.md, MJPEG over HTTP).
- **Decision 2 was overtaken** (2026-11-15). Asked again in the other session, the owner decided to build both
  packages now, `ManagedCodecs.Avalonia` and `ManagedCodecs.Wpf`, each with a `LiveView` that takes the application's
  `LivePlayer`: docs/decisions/2026-11-15-live-controls.md and PR #542. That decision is the later one and stands;
  "not yet" above is what was answered here first. Decisions 1, 3 and 4 are as built.
- **Not done:** MAUI (not now, as decided), sound in `mclive` and `mclive-wpf`, RTSP on Windows. Nobody has heard the
  sound or looked at the WPF window.

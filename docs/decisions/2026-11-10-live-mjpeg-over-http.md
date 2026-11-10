# Live camera viewing, step 1: MJPEG over HTTP (2026-11-10)

A plan, decided on by the owner (below). Nothing here is built yet. It is step 1 of #462, whose demand survey
([comment](https://github.com/bisforboman/ManagedCodecs/issues/462#issuecomment-6760330647)) recommended building it in this
repository, scoped small: first a live player and MJPEG over HTTP, then RTSP with H.264 (RFC 6184). Avalonia and WPF
come as samples, not packages.

## Why this first

- MJPEG over HTTP (`multipart/x-mixed-replace`) is what hobby devices and many cameras serve (ESP32-CAM, OctoPrint,
  mjpg-streamer, Blue Iris relays, the `video.mjpg` or `video.cgi` path on most IP cameras). Its frames are JPEGs that
  `MjpegDecoder` already decodes, bit-exact with FFmpeg.
- It is the smallest source on which to settle the **live player**: no clock, show the newest frame, drop late ones,
  reconnect, say what was dropped. RTSP then only adds a source behind the same player.
- On its own it barely fills a gap (survey: AForge and four small splitters handle the network side, but each hands
  the JPEG to `System.Drawing`, ImageSharp or a native decoder). Its value is the player shape and a first managed path
  from camera to pixels.

## How it differs from `MediaPlayer`

`MediaPlayer` plays a file on the game's clock: bounded queues, so decoding waits for playback, and `TakeFrame(position)`
picks the frame that is due. A live source can't wait, because the camera keeps sending, and has no position. The
newest frame is the only one worth showing. The two players share the RGBA conversion, `RgbaFrame` and the spare-array
pool, and nothing else.

## Proposal

1. **`LivePlayer`**: a thread of its own reads the source, decodes, converts to RGBA and keeps **one slot**, the
   newest frame. `TakeLatest()` returns it (or null when nothing is newer than what was last taken), and `Return(frame)`
   gives the array back, as with `MediaPlayer`. A frame replaced before anyone took it counts as dropped.
2. **Dropping before decoding**: MJPEG frames are independent, so when the slot is still full the reader skips a
   part's decoding altogether and counts it (a camera at 30 fps on a panel that draws 10 costs 10 decodes, not 30).
   H.264 can't skip decoding (later frames refer to earlier ones), so for RTSP the drop comes after decoding, before
   conversion. The difference stays inside the source.
3. **`MjpegHttpSource(Uri, …)`** with an `HttpClient` the caller may pass in (proxies, certificates, a handler with
   `Credentials` for Basic or Digest, which `SocketsHttpHandler` answers). It reads
   `HttpCompletionOption.ResponseHeadersRead` and splits parts by the boundary:
   - A part's `Content-Length` when it has one. Otherwise the next boundary.
   - The boundary from `Content-Type`, with or without its leading `--`. Some cameras send a boundary other than the
     one in the header: if the first part's delimiter doesn't match, the first delimiter line seen is taken as the
     boundary. Both quirks are what MJPEG clients commonly tolerate; which devices really send them is to be confirmed on the
     real devices below. Each gets a test either way.
   - Parts that aren't `image/jpeg` are skipped and counted. A part `MjpegDecoder` rejects is counted as damaged and
     skipped, not fatal.
   - A response that is a single `image/jpeg` (a snapshot URL) is refused with a message that names the mismatch.
     Polling snapshots is a possible later source, not this one.
4. **Reconnecting**: on a dropped connection, an error status or a **stall** (no part for `StallTimeout`, 5 s by
   default), the source reconnects with backoff (0.5 s, doubling, capped at 10 s, reset after the first frame).
   `State` (`Connecting`, `Playing`, `Reconnecting`, `Stopped`, `Failed`) and `Error` (the last error) say what is
   happening. 401/403/404 are `Failed` at once, as retrying won't help. Timers go through an internal clock with a
   test double (decision 3), so tests run without waiting.
5. **`LiveStatistics`**: frames received, decoded, shown (taken), dropped as late, skipped as damaged, reconnects, and
   the decode and conversion milliseconds as in `PlaybackStatistics`.
6. **Not in step 1**: audio (MJPEG over HTTP has none), RTSP, ONVIF discovery, PTZ, recording, snapshot polling,
   HTTPS client certificates beyond what a passed-in `HttpClient` gives.

## Checks

- **No camera in CI.** A test server on `TcpListener` (loopback, no ASP.NET) serves the JPEGs of the committed MJPEG
  samples as a multipart stream, in variants: with and without `Content-Length`; each boundary quirk; a part cut off
  mid-JPEG; a garbage part; a stall; a dropped connection; a 401. Decoded frames are compared by MD5 with
  `MjpegDecoder`'s output on the same JPEGs, which is itself checked against FFmpeg, so the network layer adds no
  second oracle.
- **Late frames and reconnects** on the fake clock: a slow consumer sees only the newest frame and the counts
  add up (received = decoded + late-before-decode + damaged). Each guard (the slot's replacement, the skip before
  decoding, the stall timer, no retry on 401) gets an entry in `tools/mutation/mutations.py`.
- **Real devices**, by hand, not CI: mjpg-streamer on a Raspberry Pi, an ESP32-CAM, and `ffmpeg -f mpjpeg` served over
  HTTP. Each is listed in docs/conformance.md with what it showed.
- **AOT, WASM, Unity**: the AOT check covers the player on the test server. On WebAssembly, `HttpClient` streams
  a response only with browser streaming enabled, and the camera must allow CORS. Both are documented, and a
  WASM test is out of step 1. Unity's build compiles Playback's sources, so the
  player is there too: its clock needs no `TimeProvider` (decision 3), and Mono's `HttpClient` streaming is to be
  checked in CI's `unity` job.

## Samples

- **Avalonia** (`tools/ManagedCodecs.Avalonia.Sample`): a window that shows one URL into a `WriteableBitmap`, pulling
  `TakeLatest()` on each render (`TopLevel.RequestAnimationFrame`), with the statistics in a status line. It builds
  on Linux in CI.
- **WPF** (`tools/ManagedCodecs.Wpf.Sample`): the same with `CompositionTarget.Rendering`. It builds in CI with
  `EnableWindowsTargeting` and runs only on Windows.

## Decided

Asked with the multiple-choice prompt; the owner took each proposal (2026-11-10).

1. **Where the player lives. Decided:** in `ManagedCodecs.Playback`, beside `MediaPlayer`. It shares `RgbaFrame`, the
   conversion and the pool, and needs nothing new (`System.Net.Http` is in the runtime). A separate
   `ManagedCodecs.Live` package was the alternative.
2. **Pull or push. Decided:** pull only. The UI calls `TakeLatest()` on its render tick, as with `MediaPlayer`, so
   there is no marshalling to the UI thread and late frames drop naturally. No `FrameAvailable` event.
3. **The clock. Decided:** a small internal clock interface with a test double in place of `TimeProvider`, which
   netstandard2.1 lacks. No new package reaches the Unity build.
4. **Samples. Decided:** Avalonia and WPF together.

A public API change, so a new minor version, with a CHANGELOG line under "Unreleased" when it is built.

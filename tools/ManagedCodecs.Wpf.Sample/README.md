# mclive-wpf: a live camera in WPF

A sample for issue #462 (docs/decisions/2026-11-10-live-mjpeg-over-http.md): shows a camera serving MJPEG over HTTP
in a WPF window with `ManagedCodecs.Playback`'s `LivePlayer`, or H.264 over RTSP. One camera, its URL on the command
line; not a viewer.

    dotnet run -c Release --project tools/ManagedCodecs.Wpf.Sample -- http://camera.local/video.mjpg
    dotnet run -c Release --project tools/ManagedCodecs.Wpf.Sample -- rtsp://user:password@camera.local/stream1
    dotnet run -c Release --project tools/ManagedCodecs.Wpf.Sample -- http://camera.local/video.mjpg --frames 100

- On each frame WPF renders (`CompositionTarget.Rendering`), `TakeLatest()` gives the newest decoded frame, if one
  arrived since. WPF has no RGBA pixel format, so its red and blue are swapped in place (the frame is the window's
  until `Return`) and it is written into a `WriteableBitmap` as `Bgra32`. The status line shows `State`,
  `LiveStatistics` and `Error`.
- `--frames N` makes it a check of itself: every frame shown is compared with what WPF draws of the bitmap (rendered
  into a `RenderTargetBitmap`, a pixel for each of the bitmap's), so red and blue the wrong way round, or rows out of
  step, fail it. After N frames it prints the status line and the result and exits: 0, 1 if the player gave up, 2 if a
  frame was drawn unlike the decoded one.
- Code only, no XAML. Windows only to run; CI builds it on Linux (`EnableWindowsTargeting`), and
  `.github/workflows/windows.yml` runs it with `--frames 100` on a Windows runner, weekly and when the sample changes,
  against `tools/mjpeg-http-server.py` serving `samples/gstreamer/jpeg-422` (5 JPEGs of 320x240, in a loop at 25 fps).
  Nobody has looked at its window yet. To try it without a camera, run that server
  (`python tools/mjpeg-http-server.py samples/gstreamer/jpeg-422`) and open `http://127.0.0.1:8090/video.mjpg`.

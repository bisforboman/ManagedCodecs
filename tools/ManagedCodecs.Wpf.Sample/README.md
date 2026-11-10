# mclive-wpf: a live camera in WPF

A sample for issue #462 (docs/decisions/2026-11-10-live-mjpeg-over-http.md): shows a camera serving MJPEG over HTTP
in a WPF window with `ManagedCodecs.Playback`'s `LivePlayer`. One camera, its URL on the command line; not a viewer.

    dotnet run -c Release --project tools/ManagedCodecs.Wpf.Sample -- http://camera.local/video.mjpg

- On each frame WPF renders (`CompositionTarget.Rendering`), `TakeLatest()` gives the newest decoded frame, if one
  arrived since. WPF has no RGBA pixel format, so its red and blue are swapped in place (the frame is the window's
  until `Return`) and it is written into a `WriteableBitmap` as `Bgra32`. The status line shows `State`,
  `LiveStatistics` and `Error`.
- Code only, no XAML. Windows only to run; CI builds it on Linux (`EnableWindowsTargeting`). Nobody has run it yet:
  the Avalonia sample (`mclive`) is the one tried, under Xvfb on Linux. To try it without a camera, serve
  `ffmpeg -re -f lavfi -i testsrc2 -c:v mjpeg -f mpjpeg -listen 1 http://127.0.0.1:8090/video.mjpg` and open that URL.

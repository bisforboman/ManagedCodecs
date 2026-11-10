# mclive: a live camera in Avalonia

A sample for issue #462 (docs/decisions/2026-11-10-live-mjpeg-over-http.md): shows a camera serving MJPEG over HTTP
in an Avalonia window with `ManagedCodecs.Playback`'s `LivePlayer`. One camera, its URL on the command line; not a
viewer.

    dotnet run -c Release --project tools/ManagedCodecs.Avalonia.Sample -- http://camera.local/video.mjpg
    dotnet run -c Release --project tools/ManagedCodecs.Avalonia.Sample -- http://camera.local/video.mjpg --frames 100

- Each frame Avalonia renders (`TopLevel.RequestAnimationFrame`), `TakeLatest()` gives the newest decoded frame, if
  one arrived since; it is copied row by row into a `WriteableBitmap` (`PixelFormats.Rgba8888`, the player's own
  layout) and handed back with `Return`. The status line shows `State`, `LiveStatistics` and `Error`.
- `--frames N` prints the status line and exits after N frames have been shown, or with 1 if the player gave up (a 401,
  a snapshot URL). A server that isn't there is retried, not given up on, so wrap it in `timeout`. A smoke test that
  needs no camera, against FFmpeg's HTTP server:

      ffmpeg -re -f lavfi -i testsrc2=size=640x480:rate=25 -c:v mjpeg -q:v 5 -f mpjpeg -listen 1 http://127.0.0.1:8090/video.mjpg
      xvfb-run -a dotnet tools/ManagedCodecs.Avalonia.Sample/bin/Release/net10.0/mclive.dll http://127.0.0.1:8090/video.mjpg --frames 100

  (`-listen 1` serves one client, then ends.) On the cloud VM (2026-11-10): 100 frames at the stream's 25 fps, about
  1 ms to decode and 1 ms to convert each 640x480 frame (docs/conformance.md, MJPEG over HTTP).
- Code only, no XAML. Avalonia 12; Windows, macOS and Linux (X11).

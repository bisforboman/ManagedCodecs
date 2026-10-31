# ManagedCodecs thumbnails in the browser (Blazor WebAssembly)

A page that probes and thumbnails a video picked in the browser with `ManagedCodecs.Thumbnails`: the file never leaves
the machine, and no FFmpeg, native library or server is involved. `App.razor` is the whole of it:

1. `InputFile` gives the browser's stream, which reads asynchronously only; it is copied into a `MemoryStream`, since
   an MP4's index can follow its data and the package needs a seekable stream.
2. `VideoProbe.Read` gives the duration, size as shown, codec, frame rate, rotation and audio.
3. `VideoThumbnail.Create` gives the thumbnail (480 x 480 at most, `SkipDark` on), shown as a PNG `data:` URI.

```
dotnet run --project tools/ManagedCodecs.Thumbnails.Blazor
```

or `dotnet publish -c Release` and serve `bin/Release/net10.0/publish/wwwroot` as static files.

- **One thread.** WebAssembly in a browser has one, so the page waits while the frame decodes (it says "Decoding"
  first). A thumbnail decodes from the keyframe before its time: a long GOP at 1080p is the slow case.
- **Interpreted or AOT.** A plain publish runs the decoders in .NET's WebAssembly interpreter; on the cloud VM in
  headless Chromium that took 28 s for Sintel's 1080p trailer (the frame near 5.2 s, after its keyframe's GOP) and
  0.5 s for Elephants Dream's 480x270 frame at 10 s. `-p:RunAOTCompilation=true` (the `wasm-tools` workload) compiles
  them ahead of time: Sintel's frame in 7.3 s, for 34 MB of `_framework` files against 16 MB (before the server's
  compression; README, WebAssembly).
- **Memory.** The file is held in memory (512 MB at most here, `MaxBytes`); reading a 100 MB file through the browser
  took about 11 s. A server-side upload would do the same with a temporary file.
- **Not checked by Blazor.** The package's output on WebAssembly is checked by `tools/wasm-check.sh`, which compares
  every sample's probe and thumbnail on browser-wasm (interpreted and AOT, under Node) with the native ones; CI publishes
  this sample (trimmed, warnings as errors) but does not open it in a browser.

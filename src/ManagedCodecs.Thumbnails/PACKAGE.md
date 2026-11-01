# ManagedCodecs.Thumbnails

Thumbnails and the basic facts of an uploaded video, without FFmpeg, in pure managed C#: for web back ends, serverless
functions and Blazor WebAssembly apps that cannot run FFmpeg, on files nobody has vetted.

**The stream must be seekable.** An MP4 file's index can come after its data (most camera and phone files), so an
ASP.NET request body or a serverless upload is first buffered: a `MemoryStream`, or a temporary file for large ones.

```csharp
using ManagedCodecs.Thumbnails;

await using var buffer = new MemoryStream();
await request.Body.CopyToAsync(buffer);
buffer.Position = 0;

var info = VideoProbe.Read(buffer);
// info.Duration, info.DisplayWidth x info.DisplayHeight (as shown: aspect ratio and phone rotation applied),
// info.Codec, info.FramesPerSecond, info.Rotation, info.Audio?.SampleRate, info.Audio?.Channels

buffer.Position = 0;
var thumbnail = await VideoThumbnail.CreateAsync(buffer, new ThumbnailOptions { MaxWidth = 480 }, cancellationToken);
thumbnail.SavePng(output); // or thumbnail.SaveJpeg(output, quality: 85), or thumbnail.Pixels (RGBA) to an image library for WebP
```

- **The thumbnail:** the frame shown at `ThumbnailOptions.At`, as ffmpeg's `-ss` takes it (by default 10% in, at most 10 s; with
  `SkipDark`, past dark frames keyframe by keyframe), upright, scaled down by area to fit `MaxWidth` x `MaxHeight`,
  converted by the frame's own matrix and range. Within 0 to 3 levels of FFmpeg's scaler on every sample tested.
- **Untrusted input:** frames larger than `MaxPixels` (16,777,216 by default) are refused before decoding, a
  `CancellationToken` stops the work, and damaged streams decode on with concealment.
- **What it reads:** MP4 (QuickTime, fragmented and edited files too) and AVI, with H.264 or MJPEG video. The probe decodes
  nothing; it reads the headers and sample tables only.
- **As ffprobe reports them:** the duration is `format=duration`'s and the size, aspect ratio, rotation, frame rate
  and audio facts are ffprobe's, checked on every sample the library is tested with. The displayed size is the coded
  one stretched by the pixels' aspect ratio (wider for a ratio above 1, taller below it), then turned by the rotation.
- **What it refuses:** a codec the library does not decode (HEVC, VP9, AV1, MPEG-4 Part 2) throws
  `NotSupportedException`; a file without video, or one that isn't AVI or MP4 or is malformed, throws
  `InvalidDataException`. AVI's audio is not read (`Audio` is null), so an AVI file lasts as long as its video.

The [README](https://github.com/bisforboman/ManagedCodecs#readme) has the rest, and
[the decision record](https://github.com/bisforboman/ManagedCodecs/blob/main/docs/decisions/2026-10-31-thumbnails.md)
the design. MIT licensed; the license covers the code, not the formats: codec patents (H.264, AAC) apply
regardless of the language a decoder is written in.

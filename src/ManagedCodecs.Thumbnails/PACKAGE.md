# ManagedCodecs.Thumbnails

The basic facts of an uploaded video, without FFmpeg, in pure managed C#: for web back ends, serverless functions and
Blazor WebAssembly apps that cannot run FFmpeg, on files nobody has vetted. Thumbnails come in later versions; this one
probes.

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
```

- **What it reads:** MP4 (QuickTime, fragmented and edited files too) and AVI, with H.264 or MJPEG video. Nothing is
  decoded; it reads the headers and sample tables only.
- **As ffprobe reports them:** the duration is `format=duration`'s and the size, aspect ratio, rotation, frame rate
  and audio facts are ffprobe's, checked on every sample the library is tested with. The displayed size is the coded
  one stretched by the pixels' aspect ratio (wider for a ratio above 1, taller below it), then turned by the rotation.
- **What it refuses:** a codec the library does not decode (HEVC, VP9, AV1, MPEG-4 Part 2) throws
  `NotSupportedException`; a file without video, or one that isn't AVI or MP4 or is malformed, throws
  `InvalidDataException`. AVI's audio is not read (`Audio` is null), so an AVI file lasts as long as its video.

The [README](https://github.com/bisforboman/ManagedCodecs#readme) has the rest, and
[the decision record](https://github.com/bisforboman/ManagedCodecs/blob/main/docs/decisions/2026-10-31-thumbnails.md)
the plan for thumbnails. MIT licensed; the license covers the code, not the formats: codec patents (H.264, AAC) apply
regardless of the language a decoder is written in.

# ManagedCodecs.Unity

The [ManagedCodecs](https://github.com/bisforboman/ManagedCodecs) decoders for Unity and other .NET Standard 2.1
runtimes (Mono, IL2CPP). It is the same C# as the other packages, compiled into one assembly, `ManagedCodecs.Unity.dll`:

- **H.264**: Constrained Baseline to High 4:4:4 Predictive. CAVLC and CABAC, I/P/B, interlaced, 4:0:0 to 4:4:4,
  8 to 14 bits, lossless.
- **MJPEG**: baseline and extended Huffman JPEG, interlaced AVI1.
- **AAC LC and HE-AAC** (SBR, parametric stereo), mono to 7.1.
- **Containers**: AVI, MP4 (fragmented too) and raw AAC (ADTS).
- **Thumbnails**: `VideoProbe` (a video file's duration, size as shown, codecs, rotation) and `VideoThumbnail` (one
  frame as a small upright RGBA image, PNG or JPEG), as in the ManagedCodecs.Thumbnails package.

It is the scalar build. .NET Standard 2.1 has no `System.Runtime.Intrinsics`, so the SIMD paths are left out and their
scalar references run instead. The output is the same to the bit: the whole test suite, conformance against FFmpeg
included, runs against this assembly, and every sample decodes the same on Mono.

## Speed

On Mono 6.12 (mono-project.com's, one thread, GitHub's x64 runner), the time a frame, against the other packages on
.NET 10:

| Sample | net10.0 (ms a frame) | Mono 6.12 (ms a frame) | Mono / net10.0 |
| --- | ---: | ---: | ---: |
| h264-bench-720p (1280x720, 1 I, 49 P) | 4.22 | 79.8 | 18.9 |
| h264-p-refs (320x240, P frames, CAVLC) | 0.30 | 3.89 | 12.8 |
| h264-b-pyramid (320x240, CABAC, B frames) | 0.72 | 6.45 | 9.0 |
| h264-high-default (320x240, libx264's High defaults) | 0.59 | 5.75 | 9.8 |
| h264-10bit-b (320x240, High 10) | 0.99 | 7.04 | 7.1 |
| h264-mbaff-default (320x240, MBAFF) | 1.05 | 7.53 | 7.2 |
| h264-high-motion (320x240, QP 22, fast motion) | 4.15 | 21.6 | 5.2 |
| testsrc-420.avi (320x240 MJPEG) | 0.23 | 1.05 | 4.5 |
| noise-q1.avi (MJPEG, highest quality) | 0.89 | 3.42 | 3.8 |
| aac-stereo-48k (a frame: 1024 samples, 21 ms) | 0.018 | 0.096 | 5.3 |
| aac-5.1 | 0.065 | 0.33 | 5.0 |
| heaac-5.1 (a frame: 2048 samples, 43 ms) | 0.30 | 2.84 | 9.5 |
| heaacv2-stereo | 0.12 | 0.99 | 8.1 |

So: audio (AAC 0.5 to 1.5% of a core in real time, HE-AAC 2 to 7%), MJPEG and small cutscenes play; HD H.264 does not
in real time on one thread (720p at 12.5 frames a second; `H264Decoder.Threads` decodes several pictures at once where
threads exist). Unity's own Mono and IL2CPP are not measured. On .NET 10 itself this build takes 1 to 3.4 times the
other packages' time: use them wherever .NET 10 runs.

## In Unity

Unity 2021.2 or later (the .NET Standard 2.1 profile). Take the package through
[NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity), or copy `lib/netstandard2.1/ManagedCodecs.Unity.dll`
from it into `Assets/Plugins`, together with its one dependency, `System.Runtime.CompilerServices.Unsafe`, if your
Unity version doesn't already include it.

- **Files**: `Demuxer.Open(path)` reads a file; `Demuxer.Open(stream)` reads any seekable `Stream`. On Android,
  StreamingAssets live inside the APK, so read them into a `MemoryStream` first.
- **Threads**: `H264Decoder.Threads` decodes several pictures at once with `Thread` and `Monitor`. WebGL builds have no
  threads: leave it at 1 there.
- **Tested**: on CoreCLR (the full test suite) and on Mono 6.12 (every sample decoded, output compared with the .NET
  build's). IL2CPP is not tested; it needs a Unity install.

## Decoding a file

Packets go in in decode order; frames come out in display order, each with the `Pts` of its packet:

```csharp
using ManagedCodecs;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;

using var demuxer = Demuxer.Open(path);
var video = demuxer.Video!;
var decoder = new H264Decoder(video);

foreach (var packet in demuxer.ReadPackets())
{
    if (packet.StreamIndex != video.StreamIndex)
    {
        continue;
    }

    foreach (var frame in decoder.Decode(packet))
    {
        Upload(frame); // frame.GetRow(plane, y) reads its samples: Y, U and V planes to convert or upload as textures
    }
}

foreach (var frame in decoder.Flush())
{
    Upload(frame);
}
```

Audio goes the same way: `new AacDecoder(demuxer.Audio!)` returns an `AudioFrame` of float samples per packet, for
an `AudioClip`'s `PCMReaderCallback` or `OnAudioFilterRead`.

Don't reference this package together with the others (`ManagedCodecs`, `ManagedCodecs.H264`, ...) in one project:
they hold the same types, and the compiler can't tell them apart.

## More

The [README](https://github.com/bisforboman/ManagedCodecs#readme) has the API in detail, and
[the decision record](https://github.com/bisforboman/ManagedCodecs/blob/main/docs/decisions/2026-10-31-unity-netstandard.md)
has this build's design and measurements.

MIT licensed; the package carries THIRD-PARTY-NOTICES.md, the notices of the projects some constant tables were
transcribed from. The license covers the code, not the formats: codec patents (H.264, AAC) apply regardless of the
language a decoder is written in.

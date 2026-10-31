# ManagedCodecs.Unity

The [ManagedCodecs](https://github.com/bisforboman/ManagedCodecs) decoders for Unity and other .NET Standard 2.1
runtimes (Mono, IL2CPP). It is the same C# as the other packages, compiled into one assembly, `ManagedCodecs.Unity.dll`:

- **H.264**: Constrained Baseline to High 4:4:4 Predictive. CAVLC and CABAC, I/P/B, interlaced, 4:0:0 to 4:4:4,
  8 to 14 bits, lossless.
- **MJPEG**: baseline and extended Huffman JPEG, interlaced AVI1.
- **AAC LC and HE-AAC** (SBR, parametric stereo), mono to 7.1.
- **Containers**: AVI, MP4 (fragmented too) and raw AAC (ADTS).

It is the scalar build. .NET Standard 2.1 has no `System.Runtime.Intrinsics`, so the SIMD paths are left out and their
scalar references run instead. The output is the same to the bit: the whole test suite, conformance against FFmpeg
included, runs against this assembly. On .NET (CoreCLR) it decodes 1 to 3.4 times slower than the other packages.
H.264 takes 1.3 to 3.4 times as long (8-bit streams 1.9 or more), AAC and HE-AAC 1.0 to 1.2. Use the other packages wherever
.NET 10 runs.

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

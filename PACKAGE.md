# ManagedCodecs

Pure managed C# media decoders: no native binaries, Native AOT friendly, and running on WebAssembly too. Every decoder
is checked against FFmpeg sample by sample.

- **H.264**: Constrained Baseline to High 4:4:4 Predictive. CAVLC and CABAC, I/P/B, interlaced (MBAFF and field
  pictures), 4:0:0 to 4:4:4, 8 to 14 bits, lossless; bit-exact with FFmpeg. Frame threading and error concealment on
  request.
- **MJPEG**: baseline and extended Huffman JPEG, interlaced AVI1; bit-exact with FFmpeg.
- **AAC LC and HE-AAC** (SBR, parametric stereo): mono to 7.1, within 2^-17 of FFmpeg's float output for AAC LC.
- **Containers**: AVI, MP4 (fragmented files and edit lists too) and raw AAC (ADTS), packets and timestamps as
  FFmpeg's demuxers give them.

The packages: `ManagedCodecs` for everything, or one of `ManagedCodecs.Containers`, `ManagedCodecs.Mjpeg`,
`ManagedCodecs.H264` and `ManagedCodecs.Aac`, each with `ManagedCodecs.Core`. They share internals, so each depends
on the others at exactly its own version.

## Decoding a file

Packets go in in decode order; frames come out in display order, each with the `Pts` of its packet. `Decode` returns
zero or more frames (H.264 can hold frames back for reordering) and `Flush` the rest:

```csharp
using ManagedCodecs;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;

using var demuxer = Demuxer.Open("video.mp4");
var video = demuxer.Video!;
IVideoDecoder decoder = video.Codec == CodecIds.H264 ? new H264Decoder(video) : new MjpegDecoder(video);

foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex))
{
    foreach (var frame in decoder.Decode(packet))
    {
        Show(frame); // frame.Pts is in video.TimeBase; frame.GetRow(plane, y) reads its samples
    }
}

foreach (var frame in decoder.Flush())
{
    Show(frame);
}
```

Publishing with Native AOT on x64: set `<IlcInstructionSet>x86-64-v3</IlcInstructionSet>` (AVX2) in the app, or the
256-bit paths are left out and AAC and HE-AAC take about a third longer ([README, Native AOT](https://github.com/bisforboman/ManagedCodecs#native-aot)).

Audio goes the same way: `new AacDecoder(demuxer.Audio!)` returns an `AudioFrame` of float samples per packet. Every
decoder and demuxer also has an async API (`ReadPacketsAsync`, `DecodeAsync`, `FlushAsync`).

## More

The [repository](https://github.com/bisforboman/ManagedCodecs) has the full documentation: the
[README](https://github.com/bisforboman/ManagedCodecs#readme) (the API in detail, threading, malformed input,
WebAssembly, speed against FFmpeg), [what each feature is checked against](https://github.com/bisforboman/ManagedCodecs/blob/main/docs/conformance.md),
and the [changelog](https://github.com/bisforboman/ManagedCodecs/blob/main/CHANGELOG.md). MIT licensed.

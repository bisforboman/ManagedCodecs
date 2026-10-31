# Thumbnails and probing without FFmpeg (2026-10-31)

A design for issue #253, with the decisions it needs from the owner (below); no code yet. The job: "give me a thumbnail
and the basic facts of this uploaded video" in a web back end, serverless function or Blazor WebAssembly app that cannot
run FFmpeg, on files nobody has vetted.

## What exists, and what is missing

The pieces up to a decoded frame are there: `Demuxer.Open` picks MP4 or AVI by content, `VideoStreamInfo` gives the
codec, size, frame rate, frame count and time base, `H264Decoder` and `MjpegDecoder` give YUV frames. Measured on
Caminandes: Gran Dillama (1920x1080, 24 fps, 3505 frames; the cloud VM, one thread, warm): opening takes 1.5 ms, the
first frame 80 ms, the frame at 1 s 245 ms (24 frames decoded). Missing:

- **Seeking.** `IDemuxer` reads packets from the start only. A thumbnail at 1 s costs 24 frames; one at 10% of a
  two-minute film, about 350 (some 3.5 s), where seeking to the keyframe before it would decode one GOP. MP4 and AVI
  both have the index ('stss' and the edited sample table; 'idx1'), so this is an API, not a parser.
- **Colour and orientation.** A thumbnail is RGB, so it needs the frame's range and matrix, and a phone video's
  rotation: all of it is the colour-metadata design for 0.5.0 (`2026-10-31-colour-metadata.md`), not yet built.
  Without it, H.264 thumbnails would be converted with a guessed matrix and come out sideways from phones.
- **Conversion and scaling.** YUV to RGB and a downscale to the thumbnail's size exist nowhere in the repository.
- **An image encoder.** The repository decodes JPEG; it writes no image format.

## The design (recommended)

- **A package of its own, `ManagedCodecs.Thumbnails`**, depending on Containers, H264 and Mjpeg (not Aac: the probe
  reads the audio stream's description, no samples). The codec packages stay what they are; a consumer who only
  decodes doesn't take on image code. The same version as every other package (one version for all, CLAUDE.md).
- **API**, small and synchronous with async twins, as the demuxers have:
  - `VideoProbe.Read(Stream)` → `VideoInfo`: duration (from the edited track, as ffprobe's `format=duration`
    reports it), width and height as displayed (after rotation and sample aspect ratio), codec, frame rate, rotation,
    whether there is audio and its codec, sample rate and channels. It throws `NotSupportedException` for what the
    library can't decode (HEVC, VP9, AV1 and so on), the exception the demuxers already throw, rather than a new type.
  - `VideoThumbnail.Create(Stream, ThumbnailOptions)` → an `Image` (width, height, RGBA bytes) with
    `Image.SavePng(Stream)`. Options: `At` (a time; default 10% of the duration, capped at 10 s, so a fade-in
    is skipped), `MaxWidth` / `MaxHeight` (aspect kept), `SkipDark` (step forward a GOP at a time past frames whose
    mean luma is below a threshold, up to a limit).
- **Seeking in the core packages**: `IDemuxer.Seek(long pts, int streamIndex)` with a default implementation that
  throws `NotSupportedException` (adding a defaulted interface member breaks no implementer), implemented by
  `Mp4Demuxer` and `AviDemuxer`: the next `ReadPackets` starts at the keyframe at or before `pts`, and the decoder is
  told to drop frames shown before it (or the thumbnail code skips them). Useful beyond thumbnails (ShotDetector's
  ranges, players), so it belongs in Core, in the minor version with the colour metadata.
- **Conversion**: YUV 4:2:0/4:2:2/4:4:4 at any bit depth to 8-bit RGBA by the frame's matrix and range, with
  rotation, then an area (box) downscale. An unspecified matrix converts as BT.601 and an unspecified range as limited,
  which is what FFmpeg's scaler does with them (it doesn't guess by picture size, nor does the colour-metadata design);
  `ThumbnailOptions` can say otherwise. A scalar reference by the definition in double precision, a vector path checked against it on
  random input (the repository's rule for fast paths), and FFmpeg as the outside check within a stated tolerance (its
  `swscale` rounds otherwise: compare with `-vf scale=w:h:flags=area,format=rgba` and the same matrix and range,
  within 2 per channel, not bit-exact).
- **Image output: PNG, written here**, with the BCL's `ZLibStream` (deflate is in .NET itself: no dependency, AOT and
  WebAssembly safe). Raw RGBA for callers who want JPEG or WebP through SkiaSharp or ImageSharp themselves. No image
  library as a dependency: SkiaSharp is native code (against the point of the library and not on every WebAssembly
  setup), ImageSharp's Six Labors Split License is not plain Apache-2.0. Adapter packages can come later if asked.
- **Untrusted input**: the point of the package is uploads nobody vetted, so it gets the same treatment as the
  decoders: a fuzz target for the whole path (`tools/fuzz.sh thumbnail`: open, probe, seek, decode one frame, scale),
  limits a caller can set (largest picture accepted, default 8192x8192; a `CancellationToken` through every call), and
  a README line on why managed code matters here (no memory corruption class of bugs; FFmpeg's parsers keep yielding
  them) without overstating it (a managed decoder can still be slow or wrong on hostile input: the limits are for that).
- **Not in scope**: other codecs (HEVC from iPhones is the obvious gap: the README says so plainly), audio waveforms,
  animated previews, and choosing a frame by shots (ShotDetector is another repository; it can call this package).

## Checking it

- Probe: against `ffprobe -show_format -show_streams` on every sample and the realworld films (duration, size,
  rotation, frame rate; `tools/realworld.py` grows a probe step).
- Seek: the packets after a seek are the tail of `ReadPackets` from that keyframe, on every MP4 and AVI sample
  (including edited, fragmented and B-frame ones); the frame decoded at a time matches `ffmpeg -ss <t> -i ... -frames:v 1
  -f framemd5` (FFmpeg's accurate seek: decode from the keyframe before, drop until the time).
- Conversion: scalar reference against vector path (bit-exact), and against FFmpeg's scaler within the tolerance above
  on every sample's frame and phone-style rotated samples (`ffmpeg -display_rotation` writes a 'tkhd' matrix).
- Fuzzing: the new target in `tools/fuzz.sh`, and its seeds in CI's fuzz smoke test.

## Work, one pull request each (after the colour metadata)

1. `IDemuxer.Seek` for MP4 and AVI, with the packet and frame checks above.
2. `ManagedCodecs.Thumbnails` with `VideoProbe` (no images yet), its package, readme and API.
3. YUV-to-RGBA conversion, rotation and area scaling, scalar reference first, then vector.
4. PNG writer and `VideoThumbnail.Create` with its options; the fuzz target; README section.
5. A Blazor WebAssembly sample (`samples/` or `tools/`) that thumbnails a file picked in the browser, also the
   WebAssembly check for the package.

## Decisions

Open, for the owner (recommendation first):

- **Own package or a sample.** Recommended: its own package, `ManagedCodecs.Thumbnails`; the alternative is a sample
  in the repository that callers copy, which spares the API commitment but leaves the conversion and seeking untested
  by the package checks.
- **Image encoder by default.** Recommended: PNG written here plus raw RGBA, no image library dependency. Alternatives:
  a SkiaSharp adapter in the box (JPEG and WebP, but native code), or ImageSharp (licence).
- **Seek in Core.** Recommended: `IDemuxer.Seek` as a defaulted interface member in 0.5.0 with the colour metadata.
  Alternative: seeking only inside the thumbnail package (no public API in Core, but nobody else gets it).
- **When.** Recommended: after the colour metadata lands (it is the thumbnails' prerequisite), so 0.5.0 carries
  colour, seek and the first thumbnail package together.

# Thumbnails and probing without FFmpeg (2026-10-31)

A design for issue #253, decided (below); no code yet. The job: "give me a thumbnail
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
  - Both take a seekable stream: an MP4 whose 'moov' comes after its 'mdat' (most camera and phone files) can't be
    read front to back, and `Mp4Demuxer` needs `CanSeek`. An ASP.NET request body or a serverless upload usually isn't
    seekable, so callers buffer it (a `MemoryStream`, or a temporary file for large uploads) or pass a path; the
    package's readme says so first.
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
  `Mp4Demuxer` and `AviDemuxer`: the next `ReadPackets` starts at the keyframe at or before `pts`. Decoding then
  starts afresh (a new decoder, or a flushed one) and drops the frames shown before `pts`, as FFmpeg's `-ss` does. A
  sync sample in 'stss' need not be IDR: in an open GOP, the B frames after a recovery-point I frame refer to
  pictures before the seek, which the decoder refuses (`InvalidDataException`) unless `ConcealErrors` is on. The
  thumbnail path takes the first frame at or after `pts` that decodes without a missing reference (with an open GOP,
  the I frame itself or a picture after the leading B frames), and the seek check below says what FFmpeg gives there. Useful beyond thumbnails (ShotDetector's
  ranges, players), so it belongs in Core, in the minor version with the colour metadata.
- **Conversion**: YUV 4:2:0/4:2:2/4:4:4 at any bit depth to 8-bit RGBA by the frame's matrix and range, with
  rotation, then an area (box) downscale. An unspecified matrix converts as BT.601 and an unspecified range as limited,
  which is what FFmpeg's scaler does with them (it doesn't guess by picture size, nor does the colour-metadata design);
  `ThumbnailOptions` can say otherwise. A scalar reference by the definition in double precision, a vector path checked against it on
  random input (the repository's rule for fast paths), and FFmpeg as the outside check within a stated tolerance.
  *Built (pull request 3), as measured rather than as first assumed here:* the frame is converted at its own size
  (nearest chroma sample, as FFmpeg's fast path), then scaled by area in RGB, then rotated, the order of FFmpeg's
  `format=rgba,scale=W:H:flags=area+accurate_rnd+full_chroma_int+full_chroma_inp`; scaling the YUV planes first
  blends chroma before the matrix. MJPEG's unspecified range is full (JPEG's). The fixed-point converter is within one
  level of the definition, the scaling exact, FFmpeg 0 to 3 levels below and 1 above (docs/conformance.md, where its
  exceptions are). No vector path yet: at 720p the scalar conversion and scaling take about 13 ms, against about 33 ms
  to decode a frame on the same machine.
- **Image output: PNG, written here**, with the BCL's `ZLibStream` (deflate is in .NET itself: no dependency, AOT and
  WebAssembly safe). Raw RGBA for callers who want JPEG or WebP through SkiaSharp or ImageSharp themselves. No image
  library as a dependency: SkiaSharp is native code (against the point of the library and not on every WebAssembly
  setup), ImageSharp's Six Labors Split License is not plain Apache-2.0. Adapter packages can come later if asked.
- **Untrusted input**: the point of the package is uploads nobody vetted, so it gets the same treatment as the
  decoders: a fuzz target for the whole path (`tools/fuzz.sh thumbnail`: open, probe, seek, decode one frame, scale),
  limits a caller can set (a pixel cap below the decoders' own, which already refuse an H.264 frame over 139,264
  macroblocks; a `CancellationToken` through every call), and
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
- Conversion: the fixed-point converter against its definition in double precision (within one level), the scaling
  against the exact overlap mean, and both against FFmpeg's scaler on every sample's first frame and the rotated
  samples (`ffmpeg -display_rotation 90 -i in.mp4 -c copy out.mp4`: the input option reaches the output's 'tkhd'
  matrix through the copy), FFmpeg 0 to 3 levels below and 1 above (docs/conformance.md). A vector path, if one comes,
  bit-exact against the scalar one.
- Thumbnails: `VideoThumbnail.Create` against the same FFmpeg chain at the start of every sample and at times into a
  few (`ffmpeg -ss <t>`, counted from the start time as `ThumbnailOptions.At` is); the PNG writer by a reader written
  from the spec and by FFmpeg's PNG decoder.
- Fuzzing: the `thumbnail` target in `tools/fuzz.sh`; there is no CI fuzz job, the committed findings are replayed by
  `MalformedInputTests` (through this path too).

## Work, one pull request each (after the colour metadata)

1. `IDemuxer.Seek` for MP4 and AVI, with the packet and frame checks above.
2. `ManagedCodecs.Thumbnails` with `VideoProbe` (no images yet), its package, readme and API.
3. YUV-to-RGBA conversion, rotation and area scaling, scalar reference first, then vector.
4. PNG writer and `VideoThumbnail.Create` with its options; the fuzz target; README section. *Built:* the result type
   is `Thumbnail`, not `Image` (the owner's choice: ImageSharp's, System.Drawing's and MAUI's `Image` would clash in
   the files that pass its pixels on); `SkipDark` is off by default; `MaxPixels` 16,777,216; the identity matrix
   (G, B and R planes) converts too. `VideoProbe` has no async twin: the demuxers open and read their indexes
   synchronously, and a probe reads nothing else; `CreateAsync` reads the packets asynchronously.
5. A Blazor WebAssembly sample (`samples/` or `tools/`) that thumbnails a file picked in the browser, also the
   WebAssembly check for the package.

## Decisions

Decided by the owner (2026-10-31), each as recommended:

- **Own package or a sample. Decided:** its own package, `ManagedCodecs.Thumbnails`, at the version every package has.
  A sample callers copy would spare the API commitment but leave the conversion and seeking untested by the package's
  checks.
- **Image encoder by default. Decided:** PNG written here plus raw RGBA, no image library dependency. Not a SkiaSharp
  adapter in the box (JPEG and WebP, but native code) nor ImageSharp (its licence); adapters can come later if asked.
- **Seek in Core. Decided:** `IDemuxer.Seek` as a defaulted interface member in 0.5.0, with the colour metadata,
  implemented by `Mp4Demuxer` and `AviDemuxer`, rather than seeking inside the thumbnail package only. Package
  validation may flag the new interface member even with a default body; the 0.5.0 PR's `dotnet pack` shows it, and
  the suppression file covers a deliberate change in a minor version.
- **When. Decided:** after the colour metadata lands (the thumbnails' prerequisite), so 0.5.0 carries colour, seek and
  the first thumbnail package together.

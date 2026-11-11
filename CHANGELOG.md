# Changelog

Versions are git tags (`vX.Y.Z`); each one publishes every package (ManagedCodecs.Core, .Containers, .Mjpeg, .H264,
.Aac, .Thumbnails, .Playback, .MonoGame, .Unity and the ManagedCodecs meta-package) to nuget.org at that version, and a GitHub release with this file's section
as its notes. `release.yml` releases on the push to main that brings a version heading not yet tagged, so a release
is a pull request that turns "Unreleased" into "## X.Y.Z – date" and runs `tools/ship-public-api.py`. From 0.1.0 on, the
public API changes only in a new minor version (0.2.0, 0.3.0, ...), with a "Breaking changes" entry; a patch version
(0.1.1) only fixes. Package validation enforces it: every pull request packs the packages and compares them with
the last baseline release on nuget.org (Directory.Build.props). 1.0 is a later decision. The packages
share internals, so each depends on the others at exactly its own version (`[X]`; Directory.Build.targets): NuGet
then reports a mix of versions (NU1107, or NU1608 for a newer one referenced directly) rather than letting it fail at
run time.

## Unreleased

### Added

- Samples: mcplay, the Godot node and the Unity sample play a file without video (.wav, raw MP3 or AAC) through
  `AudioPlayer`, its sound alone; `VideoTexture` stays video-only (docs/decisions/2026-11-11-audio-player.md).
- Playback: `AudioPlayer` plays a file's audio alone, as `MediaPlayer` plays it beside video (which `MediaPlayer`
  needs): `.wav`, raw MP3 and AAC, or the audio of any file the demuxers open, decoded on a thread of its own into
  float stereo for `TakeAudio()`, with `Loop`, `IsFinished` and `Error`. For `Loop`, `Mp3Demuxer` and `AdtsDemuxer`
  now seek, to the frame holding the time (MP3's encoder delay skipped from its first frame only)
  (docs/decisions/2026-11-11-audio-player.md).
- Containers: `MxfDemuxer` reads MXF files (SMPTE ST 377-1), and `Demuxer.Open` detects them: OP1a with frame-wrapped
  essence in the generic container, the first picture and sound tracks, packets as FFmpeg's MXF demuxer gives them
  (times from the edit units and the index table, keyframes from it), and `Seek`. H.264 is `CodecIds.H264`, which
  `H264Decoder` decodes exactly as FFmpeg (as in FFmpeg's, the packets' times are decode times where the index gives no
  temporal offsets, and the frames are timed as an AVI's), and PCM (AES3 and Broadcast Wave mappings)
  `CodecIds.PcmS16Le`, `PcmS24Le` or `PcmS32Le`, which `PcmDecoder` decodes exactly as FFmpeg, so `MediaPlayer` plays
  them; ProRes, DNxHR and MPEG-2 are named (`prores`, `dnxhd`, `mpeg2video`) but not decoded (issue #469).
- `MsAdpcmDecoder` (ManagedCodecs.Core) and `CodecIds.AdpcmMs`: Microsoft ADPCM (the WAVE format tag 0x02), mono and
  stereo, exactly FFmpeg's output (its standard coefficient table whatever the file lists, and its cap on the step).
  `.wav` and AVI files name such streams `CodecIds.AdpcmMs` where they named them `"0x0002"`, and `MediaPlayer` plays
  them (docs/decisions/2026-11-10-ms-adpcm.md).
- Live cameras over RTSP: `RtspSource` and a `LivePlayer` constructor taking it show H.264 over RTSP 1.0 (Digest or
  Basic, RTP over the RTSP connection), decoding every picture with concealment for lost packets and skipping to the
  next IDR picture when decoding falls `MaxLag` behind. `LiveStatistics.LostPackets` counts lost RTP packets, and
  `LivePlayer.RtspSource` gives the source. `LivePlayer.Source` is now nullable (null for an RTSP player), which
  callers that dereference it see as a nullable warning; it is binary compatible (issue #462,
  docs/decisions/2026-11-10-live-rtsp-h264.md).
- G.711 A-law and μ-law: `PcmDecoder` decodes them exactly as FFmpeg, named by new `CodecIds.PcmAlaw` and
  `CodecIds.PcmMulaw`. `.wav` and AVI files (format tags 6 and 7, where they gave `"0x0006"` and `"0x0007"`) and
  QuickTime ('alaw', 'ulaw') name them so, and `MediaPlayer` plays them.

## 0.12.0 – 2026-11-10

PCM everywhere: `PcmDecoder` takes every integer and float PCM format in both byte orders, from AVI, QuickTime, ISO
MP4, Matroska and the new `WavDemuxer` for `.wav` files, exactly as FFmpeg decodes it. IMA ADPCM from cameras' AVIs
(`ImaAdpcmDecoder`), raw H.264 files with seeking (`H264Demuxer`), and live cameras over HTTP (`LivePlayer`). New public
API, hence a minor version; code built against 0.11 keeps working.

### Added

- Containers: `WavDemuxer` reads `.wav` files (RIFF WAVE), and `Demuxer.Open` detects them: integer and float PCM
  (WAVE_FORMAT_EXTENSIBLE too), IMA ADPCM and MP3, packets as FFmpeg's wav demuxer gives them; `Seek` starts at the
  packet holding the time; `mcdec` decodes them.
  AVI names its 24- and 32-bit and float PCM by the same rules (`CodecIds.PcmS24Le`, `PcmS32Le`, `PcmF32Le`,
  `PcmF64Le`), where it gave the format tag (docs/decisions/2026-11-10-pcm-containers.md).
- PCM in ISO MP4 files: `Mp4Demuxer` names 'ipcm' and 'fpcm' (ISO/IEC 23003-5, as FFmpeg's mp4 muxer writes PCM) by
  their 'pcmC' box, so `PcmDecoder` decodes them, exactly as FFmpeg does.
- Live cameras: `LivePlayer` (Playback, and the Unity package) shows a camera serving MJPEG over HTTP
  (`MjpegHttpSource`), handing the UI the newest decoded frame each render (`TakeLatest`), dropping older ones, and
  connecting again after a drop or a stall, with `State`, `Error` and `LiveStatistics` to say what is happening
  (issue #462). Samples for Avalonia (`tools/ManagedCodecs.Avalonia.Sample`) and WPF (`tools/ManagedCodecs.Wpf.Sample`).
- PCM in QuickTime and Matroska files: `PcmDecoder` decodes signed 16-, 24- and 32-bit PCM in either byte order and
  32- and 64-bit float PCM, exactly as FFmpeg does, named by new `CodecIds` constants (`PcmS16Be`, `PcmS24Le`,
  `PcmS24Be`, `PcmS32Le`, `PcmS32Be`, `PcmF32Le`, `PcmF32Be`, `PcmF64Le`, `PcmF64Be`). `Mp4Demuxer` names QuickTime's
  'raw ', 'sowt', 'twos', 'in24', 'in32', 'fl32' and 'fl64' entries by them (the byte order from 'enda'), and
  `MatroskaDemuxer` the A_PCM Codec IDs by their BitDepth; `PcmDecoder.CanDecode` says whether a codec is one. So
  `MediaPlayer` plays them and `mcdec` decodes them (docs/decisions/2026-11-10-pcm-containers.md).
- Containers: `H264Demuxer` reads raw H.264 files (Annex B elementary streams, `.h264` and `.264`), and
  `Demuxer.Open` detects them: a packet an access unit, as FFmpeg's parser cuts them (IDR pictures keyframes), a tick
  each at the SPS's VUI frame rate (else 25), the size from the SPS; `H264Decoder` numbers their frames from 0, as
  FFmpeg's command line does. `Seek` starts at an IDR picture, so `MediaPlayer`'s `Loop` and thumbnails at a
  time work too (docs/decisions/2026-11-09-raw-h264.md).
- `ImaAdpcmDecoder` (ManagedCodecs.Core) and `CodecIds.AdpcmImaWav`: AVI's 4-bit IMA ADPCM (format tag 0x11), as
  cameras write it beside MJPEG, exactly FFmpeg's output. `AviDemuxer` names such streams `CodecIds.AdpcmImaWav`
  where it named them `"0x0011"`, and `MediaPlayer` plays them (docs/decisions/2026-11-10-ima-adpcm.md).

### Changed

- H.264: the chroma motion compensation filter's rows are inlined, no longer a call each: about 1.7% faster under the
  JIT on a 1080p High profile stream with B pictures. The output is unchanged.
- H.264: weighted prediction (libx264's default weighted P and implicit B weights) multiplies both predictions'
  samples by their weights in one step and clips by saturating packs: about 1% faster under the JIT on the same
  stream. The output is unchanged.
- H.264: the luma interpolation filter sums each row's six taps by multiply-adds of paired samples (SSSE3) and clips by
  saturating packs: about 1.5% faster under the JIT on the same stream. The output is unchanged.

## 0.11.0 – 2026-11-09

AVI audio: `AviDemuxer` gives an AVI's audio stream, and `MediaPlayer` plays its MP3, AAC and 8- and 16-bit PCM (the new
`PcmDecoder`), so cameras' MJPEG AVIs have sound. H.264 in AVI decodes and seeks. Raw MP3 files frame as FFmpeg's in
more cases. New public API, hence a minor version; code built against 0.10 keeps working.

### Added

- `PcmDecoder` (ManagedCodecs.Core) and `CodecIds.PcmU8` and `CodecIds.PcmS16Le`: AVI's 8-bit unsigned and 16-bit
  signed PCM, exactly FFmpeg's float output. `MediaPlayer` plays it, so cameras' MJPEG AVIs have sound, and `mcdec`
  decodes it (docs/decisions/2026-11-09-pcm-decoder.md).

### Changed

- Containers: `AviDemuxer.Audio` is the file's first audio stream, where it was always null, and `ReadPackets` gives
  its packets with the video's: MP3 (both as FFmpeg's muxer writes it and in the CBR layout of VirtualDub and older
  muxers, re-split into frames), AAC, and other audio named by its format tag (8- and 16-bit PCM as
  `CodecIds.PcmU8` and `CodecIds.PcmS16Le`, other PCM as `"0x0001"`; PCM and ADPCM in pieces of chunks, as FFmpeg's
  demuxer cuts them). So `MediaPlayer` plays an AVI's MP3, AAC and PCM, `Seek` takes the audio stream too, and `VideoProbe`'s duration counts the audio, as
  ffprobe's does: audio with a frame a chunk (MP3, AAC), not PCM, whose stream has no duration in FFmpeg
  (docs/decisions/2026-11-08-avi-audio.md).

### Fixed

- H.264 in AVI decodes: its Annex B stream (start codes, the SPS and PPS among the packets, no avcC) made
  `H264Decoder` throw. A decoder made without a configuration record (`codecPrivate` empty, as `AviDemuxer` gives it)
  now reads Annex B, and its frames take the packets' decoding-order times as FFmpeg's do (the k-th frame shown the
  time of the packet the reorder depth later).
- Containers: an AVI's H.264 seeks, by the keyframes of its 'idx1' index (from at least 16 frames before the time, as
  its packets carry decoding times), and its packets' `IsKeyframe` is the index's, where every packet was a keyframe;
  so thumbnails of such a file no longer decode it from the start.
- Containers: `Mp3Demuxer` frames a raw MP3 file as FFmpeg's demuxer in three more cases: a first frame whose header
  differs from the next one's (mode, copyright, original, emphasis) is skipped; a Xing or Info frame that counts neither
  frames nor bytes is a packet; and a packet the LAME padding covers whole skips all its samples
  (`SkipEndSamples`) rather than being discarded (the same samples decoded).
- Containers: an MP4 audio track's last packet, where the edit list starts late in the media (MP3 remuxed from a raw
  file, 1105 samples in), lasted to the media's end rather than to the edit's, as FFmpeg's: 576 samples where FFmpeg
  gives 578.

## 0.10.0 – 2026-11-08

MP3: the new package `ManagedCodecs.Mp3` decodes MPEG audio Layer III, the containers name MP3 tracks
(`CodecIds.Mp3`) and read raw `.mp3` files gapless (`Mp3Demuxer`), and `MediaPlayer` plays MP3 sound as it plays AAC
(and names the audio it skips). H.264 MBAFF frames decode faster: intra macroblocks in place, and fewer deblocking
strength lookups. New public API, hence a minor version; code built against 0.9 keeps working.

### Added

- Playback: `MediaPlayer.SkippedAudioCodec` names a file's audio when it isn't played (only AAC and MP3 are decoded),
  such as Opus, so a video playing silent can say why. The Godot node, the Unity sample and mcplay print a warning with it
  (#415).
- `CodecIds.Mp3`: MP3 (MPEG-1 and MPEG-2 audio) in MP4 (object types 0x6B and 0x69, QuickTime's `.mp3` and `ms\0U`
  entries) and Matroska (`A_MPEG/L3`). Demuxed, not decoded.
- MP3: the new package `ManagedCodecs.Mp3`, `Mp3Decoder` (MPEG audio Layer III at every MPEG-1, MPEG-2 and MPEG-2.5
  sample rate; mono, stereo, dual channel and joint stereo; the bit reservoir), from MP4 and Matroska, within 2^-17 of
  FFmpeg's float output (measured: under 2e-6). Also in `ManagedCodecs.Unity` and the meta-package; `mcdec` decodes
  it (#415).
- Containers: `Mp3Demuxer` reads raw `.mp3` files (Layer III frames, ID3 tags skipped), and `Demuxer.Open` detects
  them; gapless as FFmpeg plays them, its LAME tag's encoder delay and padding trimmed (#415).
- Playback: `MediaPlayer` plays MP3 audio as it plays AAC, and with it `VideoTexture`, the Godot node and the Unity
  sample: Tears of Steel, one of their test clips, now has sound (#415).

### Changed

- Containers: an MP3 track's `Codec` is now `CodecIds.Mp3` (`"MP3"`), where it was the sample entry's name in MP4
  (`mp4a`, `.mp3`) and `A_MPEG/L3` in Matroska.
- H.264: in MBAFF frames and field pictures, a macroblock edge between two macroblocks of one kind, each with one
  motion and no coefficients, takes one bS for its four segments, as in frames, and the top edges are found without an
  allocation per macroblock: about 3.7% fewer instructions in MBAFF and field streams, 1 to 2% of their time under the
  JIT. Motion vector prediction and the deblocking filter's top edge find their neighbours there without the general
  neighbour lookup: about 1% more.
- H.264: MBAFF frames reconstruct intra macroblocks in place, as frames do, instead of in a scratch block with their
  neighbouring samples gathered and the result copied back: 14 to 15% fewer instructions in intra MBAFF streams
  (4 to 6% of their time under the JIT), 3 to 4% in mixed ones.

### Fixed

- MonoGame: `VideoTexture` opened its sound (and OpenAL's device) only at the first frame, which once delayed the
  sound by 234 ms on a cold start. It now opens it as soon as the first audio is decoded and starts it at the first
  frame: within about 10 ms of the picture.
- Thumbnails: `VideoProbe`'s duration counted an MP4 audio track from its first packet's start when the edit list
  starts inside it (MP3's encoder delay, 1105 samples), 25 ms too long; it now starts where the edit does, as ffprobe's.

## 0.9.1 – 2026-11-08

Fixes to the new playback packages: `MediaPlayer` no longer drops frames when a game ticks at the video's own rate,
and `VideoTexture`'s sound starts with the picture. H.264 intra prediction is faster (about 2% of a High profile
frame under the JIT), with the same output. No public API changes.

### Changed

- H.264: intra prediction's vertical, horizontal and DC modes (4x4, 8x8, chroma DC) write a row at a time: 10% fewer
  instructions in an 8-bit intra-only stream under Native AOT, about 1% of its time under the JIT. The output is
  unchanged.
- H.264: in 4:2:2 MBAFF frames and field pictures, the deblocking filter takes chroma's left edge between pairs of one
  kind, and the top edge where its rows meet, as plain edges instead of line by line: 17 to 18% fewer instructions
  in 4:2:2 MBAFF streams, about 11% of their time under the JIT.
- H.264: in MBAFF frames and field pictures, the deblocking filter finds a macroblock edge's bS for its four segments
  at once, and chroma reuses luma's for the top edges instead of finding them again: 5 to 7% fewer instructions in
  MBAFF and field streams, about 2.5% of their time under the JIT. The left edge between a field and a frame pair
  finds its lines' bS from what holds for the edge too (up to 0.5% more).
- H.264: 16x16 and chroma intra prediction (vertical, horizontal, DC, plane) also write a row at a time: 1 to 5% fewer
  instructions in intra-only streams under Native AOT, within noise under the JIT. The output is unchanged.
- H.264: Intra_8x8 vertical, horizontal and DC filter only the edge they predict from, straight from the picture: about
  2% faster under the JIT on High profile streams, intra-only or not (2.5 to 3.4% fewer instructions under Native
  AOT). The output is unchanged.

### Fixed

- Playback: `MediaPlayer.TakeFrame` dropped about 1 frame in 5 when the game ticks at the video's own rate (24 fps
  video at 24 Hz, 60 at 60 Hz): its clock, a sum of frame times, falls a rounding error short of some frames, which
  were then shown a tick late and dropped. A frame is now due from half the interval before its time, so each call
  shows the frame nearest the clock.
- MonoGame: `VideoTexture` started the sound before the picture (about 280 ms with Big Buck Bunny), since the first
  audio is decoded ahead of the first frame. The sound now starts with the first frame.

## 0.9.0 – 2026-11-07

Playback for game engines: the new packages `ManagedCodecs.Playback` (`MediaPlayer`, also in ManagedCodecs.Unity) and
`ManagedCodecs.MonoGame` (`VideoTexture`). H.264 decodes faster in 10-bit, 4:2:2, MBAFF and field streams, AAC LC and
HE-AAC much faster, MJPEG a little, and the Unity build on Mono faster still. New public API, hence a minor version;
code built against 0.8 keeps working.

### Added

- New package `ManagedCodecs.Playback`: `MediaPlayer` plays a video file (H.264 or MJPEG with AAC; MP4, Matroska, WebM
  or AVI) for a game engine, decoding on a thread of its own into RGBA frames (`RgbaFrame`) and float stereo
  (`AudioChunk`), and gives the game loop the frame due by its clock (`TakeFrame`), dropping late ones; with
  `PlaybackOptions` (decoder threads, matrix and range, loop) and `PlaybackStatistics`. `ManagedCodecs.Unity` holds it
  too. The MonoGame, Godot and Unity samples now use it.
- New package `ManagedCodecs.MonoGame`: `VideoTexture` plays a file into a `Texture2D` and a
  `DynamicSoundEffectInstance` with the game's clock (`Update`, `Play`, `Pause`, `Volume`) and draws it letterboxed, at
  its sample aspect ratio and turned upright (`Draw`). Any MonoGame 3.8 platform package, on .NET 10.

### Changed

- HE-AAC decodes faster: the QMF banks' transforms (their FFTs, DCT-IVs and rotations, between windows still in double)
  run in single precision, four values a vector: 7 to 10% off a 5.1 HE-AAC frame and 4 to 7% off HE-AAC v2, on ARM64
  and x64. The output moves against FFmpeg's (single precision too) in the third or fourth digit of its differences.
- H.264: about 24 small functions on the hot paths are inlined, as the runtime's dynamic PGO does (#346): 1.5 to 3.8%
  less time a frame in the steady state on ARM64 and 0.4 to 2.6% on x64 with tiering off (as mcdec runs) or
  `TieredPGO=false`, and up to 5% more JIT time at startup for short files. Native AOT builds (no JIT at startup)
  execute 3.1 to 5.6% fewer instructions a frame.
- H.264: the direct motion compensation path for frame macroblocks also takes samples above 8 bits and 4:2:2: about 2%
  fewer instructions in 10-bit and 4:2:2 streams.
- H.264: MBAFF frames and field pictures take the direct motion compensation path too: about 2.2% fewer instructions
  in MBAFF frames and 3.4% in field pictures.
- AAC: the IMDCT's FFT does its bit reversal and first stage in one vector pass, and its post-twiddle runs in
  vectors: 15 to 23% off an AAC LC frame on x64 and ARM64, and 3% off 5.1 HE-AAC. The output is unchanged.
- AAC: the IMDCT (its FFT, DCT-IV and fold) runs in single precision, four values a vector where it ran two
  doubles: on top of the line above, 13 to 15% off an AAC LC frame on ARM64 and 5 to 10% on x64 (2% off 5.1 HE-AAC).
  Output moves by about 1e-8 on average; the largest difference from FFmpeg's on real films goes from 3.6e-7 to
  4.8e-7 (the bound is 2^-17, 7.6e-6).
- H.264: above 8 bits, the bi-predicted average, weighted prediction and the inverse transform adds run in vectors
  instead of a loop per sample: about 9 to 13% fewer instructions in 10-bit streams (3% of the frame under the JIT).
- HE-AAC v2: parametric stereo's hybrid filters, its transient detector's smoothing and the hybrid sub-subbands'
  all-pass filter run in vectors: about 6% off a 20-band frame and 11% off a 34-band one on x64. The output is unchanged.
- H.264: in MBAFF frames and field pictures, motion vector prediction finds its neighbours A, B and C in one call:
  about 1.8% fewer instructions in MBAFF frames, 1.6% in field pictures and 1.1% in progressive CAVLC frames.
- H.264: in MBAFF frames and field pictures, CABAC's context selection reads a macroblock's left and above neighbours
  from a table found once a macroblock instead of a call each: about 1.6% fewer instructions in CABAC MBAFF frames
  and 1.7% in field pictures.
- AAC: spectral data is read through packed Huffman tables, all of a codeword's sign bits at once: AAC LC at film
  bitrates decodes 14 to 21% faster (Elephants Dream's 192 kbit/s stereo, Sintel's trailer).
- AAC: inverse quantization runs a band without branches or calls: AAC LC at film bitrates decodes another 26 to 29%
  faster.
- H.264: spatial direct prediction finds a macroblock's neighbours A, B and C once for both its reference indices and
  its motion vectors, in MBAFF frames and field pictures in one call: about 2.2% fewer instructions in MBAFF frames
  with B pictures and 1.2% in field pictures.
- H.264: intra prediction in MBAFF frames and field pictures gathers a left pair's samples with each macroblock's
  place found once rather than once a row, and its mode prediction reads the left and above neighbours from the
  per-macroblock table: about 3.2% fewer instructions in intra MBAFF frames and 0.9% in mixed ones.
- MJPEG: the Huffman decoder keeps its bit buffer in registers, and a short code with long magnitude bits takes one
  table lookup instead of two: about 6 to 7% off a frame.
- ManagedCodecs.Unity on Mono: motion compensation filters whole rows of a block instead of every sample by the
  definition, MJPEG's IDCT indexes rather than slicing, and the bit-count polyfills search rather than loop: on Mono
  6.12, 720p H.264 about twice as fast (79 to 38 ms a frame), 320x240 H.264 1.3 to 1.5 times, MJPEG 1.45 times.
  The other packages' code is unchanged where it runs (docs/decisions/2026-10-31-unity-netstandard.md, Mono's
  profile).
- ManagedCodecs.Unity on Mono: HE-AAC's QMF banks run their scalar FFT and windows by reference rather than through
  slices: on Mono 6.12, 5.1 HE-AAC 1.39 times as fast, HE-AAC v2 1.30 times. The output is unchanged.
- ManagedCodecs.Unity on Mono: CABAC's and deblocking's tables are plain arrays in this build, which Mono reads
  directly where it called a property's getter at every use: CABAC streams 1.09 to 1.15 times as fast on Mono 6.12.
  The output, and the other packages' code, are unchanged.
- MJPEG: a flat block's rows are written as one 8-byte store each instead of a call each: about 7 to 10% off a frame of
  a test pattern, nothing on noise.
- HE-AAC: SBR's gains smoothed over time (a header with bs_smoothing_mode 0) and parametric stereo's mixing run in
  vectors, to the same bits: SBR's envelope adjuster takes about half its share of such a frame, about 8% of it, and
  the mixing about a fifth less of an HE-AAC v2 frame's 5 to 8%.
- MJPEG: the bit reader's refill takes eight bytes inline, a call only at stuffing, markers and the data's end:
  about 2 to 5% off a frame.

## 0.8.1 – 2026-11-07

H.264 decodes a little faster. No change to the public API, hence a patch version.

### Changed

- H.264: inter macroblocks without coded residual skip the residual add, and coded ones visit only their coded 8x8
  blocks: about 2% off a CAVLC frame, 1% off a CABAC one.

## 0.8.0 – 2026-11-04

Matroska and WebM: `MatroskaDemuxer` reads them (and `Demuxer.Open` picks it), thumbnails take them, and
`Packet.SkipEndSamples` carries an encoder's padding at a packet's end, which the AAC decoder drops; an MP4 stream's
`Duration` without an edit list is now FFmpeg's. New public API (a demuxer, two properties), hence a minor version;
code built against 0.7 keeps working.

### Added

- Containers: `MatroskaDemuxer` for Matroska and WebM files, also picked by `Demuxer.Open`: the first video and audio
  track, SimpleBlocks and BlockGroups, Xiph, EBML and fixed-size lacing, Clusters and Segments of unknown size (live
  files), header stripping, `ReadPacketInfo` and `Seek` (with Cues or without). H.264, MJPEG and AAC map to `CodecIds`;
  other codecs (VP9, AV1, Opus, Vorbis) keep their Matroska Codec IDs. Checked packet by packet against FFmpeg (#305;
  docs/decisions/2026-11-03-matroska-demuxer.md).
- Core: `Packet.SkipEndSamples` and `PacketInfo.SkipEndSamples`, samples to drop at a packet's end (an encoder's
  padding), which `AacDecoder.Decode(Packet)` drops; the Matroska demuxer sets them, and `SkipSamples`, from a block's
  DiscardPadding, as FFmpeg's skip-samples side data.
- Containers: Matroska's zlib-compressed tracks (frames and CodecPrivate) and display rotation (a Projection's roll,
  `VideoStreamInfo.Rotation`).
- Thumbnails: `VideoProbe` and `VideoThumbnail` on Matroska files, checked against ffprobe and FFmpeg's `-ss`: the
  duration is the Segment's, as FFmpeg's, from the earliest packet.

### Fixed

- Containers: an MP4 stream's `Duration` without an edit list is the shorter of the track's 'mdhd' duration and its
  samples' summed durations, as FFmpeg's (it was the longer: an Android phone's video came out 55 ms too long; #317).

## 0.7.0 – 2026-11-01

Thumbnails as JPEG: `Thumbnail.SaveJpeg` and `ToJpeg` write a baseline JPEG in managed code, beside the PNG. New public
API (two `Thumbnail` methods), hence a minor version; code built against 0.6 keeps working.

### Added

- Thumbnails: `Thumbnail.SaveJpeg(Stream, int quality = 85)` and `Thumbnail.ToJpeg(int quality = 85)`, a baseline
  JPEG written here (JFIF, 4:2:0, Annex K's tables, `quality` as libjpeg's), beside the PNG
  (docs/decisions/2026-11-01-thumbnails-jpeg.md).

## 0.6.0 – 2026-11-01

MJPEG from real cameras: the 4:1:1 and 4:4:0 layouts cameras write, as two new pixel formats, damaged images decoded
as FFmpeg decodes them, the other AVI FourCCs for MJPEG, and old QuickTime files that start with a preview. New public
API (two `PixelFormat` values), hence a minor version; code built against 0.5 keeps working.

### Added

- MJPEG decodes 4:1:1 and 4:4:0 images (camera layouts: luma 4x1 against chroma 1x1, and 1x2 against 1x1 or 2x2
  against 2x1), as the new `PixelFormat.Yuv411P` and `PixelFormat.Yuv440P`, where it threw NotSupportedException.
  `RgbaConverter` and `VideoThumbnail` convert them. Code that switches over `PixelFormat` sees two new values.

### Changed

- MJPEG decodes damaged images as FFmpeg does, where it threw InvalidDataException: bytes before the SOI marker are
  skipped, a missing EOI marker is taken as there, and a scan cut short or damaged gives the frame decoded as far as
  its data goes, the rest 0 and `VideoFrame.IsDamaged` set. A packet too small for its image (under a quarter byte an
  8x8 block) is refused, as FFmpeg gives no frame for it, and an interlaced packet whose first field is cut short is
  InvalidDataException, not NotSupportedException. `VideoThumbnail` and `mcdec --dump` skip a refused packet
  (docs/conformance.md, MJPEG).

### Fixed

- `Demuxer.Open` recognises old QuickTime files that start with a `pnot` preview box and no `ftyp` (a Nikon Coolpix's
  MJPEG, for one), where it threw NotSupportedException.
- AVI: MJPEG under the other FourCCs capture cards, editors and cameras write (AVI1, dmb1, JPGL, LJPG, IJPG, ACDV,
  QIVG, SLMJ, CJPG, JPEG, MMJP, MVJP, MJPA; any case) is read as MJPEG, as FFmpeg reads it, where only MJPG was.

## 0.5.0 – 2026-11-01

Two new packages: ManagedCodecs.Thumbnails (a video file's facts as ffprobe gives them, and a thumbnail) and
ManagedCodecs.Unity (every decoder and demuxer, and the thumbnails, for .NET Standard 2.1, which Unity's Mono and
IL2CPP load). Also seeking in demuxers, each frame's colour metadata, stream durations, and faster AAC, HE-AAC and
deep H.264. New public API, hence a minor version; code built against 0.4 keeps working.

### Added

- `ManagedCodecs.Thumbnails`, a new package: `VideoProbe.Read` gives an MP4 or AVI file's duration, displayed size,
  codecs, frame rate, rotation and audio facts as ffprobe reports them, without decoding (README, Probing a video;
  docs/decisions/2026-10-31-thumbnails.md, pull request 2).
- `VideoThumbnail.Create` and `CreateAsync` (ManagedCodecs.Thumbnails): one frame of a video file as a small upright
  RGBA `Thumbnail`, with `SavePng`; `ThumbnailOptions` sets the time, the largest size, skipping dark frames, a pixel
  limit for untrusted uploads and the colour conversion (README, Thumbnails).
- `VideoStreamInfo.Duration` and `AudioStreamInfo.Duration`: a stream's length as the container gives it, as FFmpeg's
  stream duration (MP4's edits, capped at the samples they take, else the track's; AVI's stream header length).
- `ManagedCodecs.Unity`, a new package: every decoder and demuxer in one assembly for .NET Standard 2.1, which Unity's
  Mono and IL2CPP load. It is the scalar build (no `System.Runtime.Intrinsics` there), with the same output to the bit
  and on Mono 6.12 4 to 19 times the time of the other packages on .NET 10 (audio, MJPEG and small
  cutscenes play; 720p H.264 runs at 12.5 frames a second on one thread). It holds the same types as the other packages, so reference
  one or the other (README, Unity; docs/decisions/2026-10-31-unity-netstandard.md). It includes ManagedCodecs.Thumbnails
  (`VideoProbe`, `VideoThumbnail`), its PNG written with .NET Standard's `DeflateStream` and its own zlib header and
  Adler-32.
- `IDemuxer.Seek(pts, streamIndex)`: the next read starts at the keyframe at or before a time, the other stream at the
  same instant, its packets those a read from the start gives from there (README, Decoding a file). `Mp4Demuxer` seeks in
  every file, `AviDemuxer` in MJPEG; other demuxers throw `NotSupportedException` (a default interface member, so an
  implementation of `IDemuxer` outside this library still compiles). New public API: the next release is a minor
  version (docs/decisions/2026-10-31-thumbnails.md, pull request 1).
- Every package carries THIRD-PARTY-NOTICES.md: the BSD-2-Clause and Apache-2.0 notices of OpenH264 and libxaac, from
  which some of the specifications' constant tables were transcribed (H.264's CABAC tables; HE-AAC's QMF window, SBR
  noise table and parametric stereo's 34-band tables). The code and its license are unchanged.
- What each decoded frame's samples mean: `VideoFrame.ColorRange`, `ColorPrimaries`, `ColorTransfer`, `ColorMatrix`
  and `ChromaLocation` (new enums with ITU-T H.273's values, FFmpeg's `color_range`, `color_primaries`,
  `color_transfer`, `color_space` and `chroma_location`) and `SampleAspectRatio`, from H.264's VUI as FFmpeg reads it
  (compared with ffprobe on every H.264 sample). New public API: the next release is a minor version. MJPEG frames
  too, as FFmpeg gives them: BT.601, centred chroma, full range (limited after FFmpeg's encoder's "CS=ITU601"
  comment), the JFIF density's aspect ratio. And the MP4 container's: `VideoStreamInfo.ColorRange`, `ColorPrimaries`,
  `ColorTransfer` and `ColorMatrix` from 'colr', `SampleAspectRatio` from 'pasp' and `Rotation` (0, 90, 180 or 270,
  from the track and movie matrices), which a decoder made from the stream gives frames where the bitstream says
  nothing (the ratio wherever the container has one, as ffprobe shows frames), and AVI's `SampleAspectRatio` from
  'vprp' (docs/decisions/2026-10-31-colour-metadata.md).
- `AviDemuxer.ReadPacketInfo()` and `AdtsDemuxer.ReadPacketInfo()`, as `Mp4Demuxer`'s (0.4.0): the packets
  `ReadPackets` gives, without their data. ADTS's comes from the frames found when the file is opened, with no read;
  AVI's walks the 'movi' list reading each chunk's 8-byte header and skipping its data. New public API: the next release
  is a minor version.

### Changed

- MJPEG decodes faster on ARM64, bit for bit as before: 2.9% off `testsrc-420.avi`'s steady frame, 3.4% off
  `testsrc-444.avi` and 2.4% off `noise-q1.avi` on GitHub's ARM64 runner (`compare.yml` against main; x64 runs the
  same code as before). The transposes' interleaves are ARM64's zip1 and zip2 there, where they were two table
  lookups and a select; the H.264 deblocking transposes take them too, within 1% on its samples.
- HE-AAC decodes faster, bit for bit as before: 5.9% off `heaac/heaac-5.1.mp4`'s steady frame on GitHub's x64
  runner and 4.9% on its ARM64 runner, 3.3% and 3.0% off `heaac/heaacv2-stereo.mp4` (`compare.yml` against main).
  The QMF banks sum their windows' terms in registers, four vectors at a time, in the same order as before.
- AAC decodes faster, bit for bit as before: 10.8% off `aac-stereo-48k.mp4`'s steady frame on GitHub's x64 runner
  and 9.3% on its ARM64 runner, 9.8% and 8.5% off `aac-5.1.mp4`, 3.4% and 1.9% off `heaac/heaac-5.1.mp4`
  (`compare.yml` against main). The IMDCT's pre-twiddle and fold take two samples a vector.
- H.264 deeper than 8 bits decodes faster, bit for bit as before: 1.6 to 2.5% off `h264-10bit-b.mp4`'s steady frame
  on GitHub's x64 and ARM64 runners in two `compare.yml` runs against main, `h264-10bit-motion.mp4` (mostly CABAC
  residuals) unchanged. Motion compensation's reference window widens eight samples a vector.
- H.264 deeper than 8 bits deblocks faster, bit for bit as before: 4.1 to 6.4% off `h264-10bit-b.mp4`'s steady frame
  and 2.5 to 3.3% off `h264-10bit-motion.mp4` on GitHub's x64 and ARM64 runners in two `compare.yml` runs against
  main. Vertical edges load eight rows a vector and transpose them, where they read one sample at a time.
- H.264 deeper than 8 bits decodes faster on x64 with AVX2, bit for bit as before: 3.1 to 5.3% off
  `h264-10bit-b.mp4`'s steady frame on GitHub's x64 runner in two `compare.yml` runs against main (ARM64 runs the same
  code as before). Motion compensation's interpolation takes eight lanes a vector there, where it took four.
- H.264 deeper than 8 bits deblocks faster again, bit for bit as before: 3.2 to 4.3% off `h264-10bit-b.mp4`'s
  steady frame and 2.3 to 2.8% off `h264-10bit-motion.mp4` on GitHub's x64 and ARM64 runners in two `compare.yml` runs
  against main. An edge's samples and filter parameters stay in vectors, where they went through buffers on the stack.
- H.264 deeper than 8 bits decodes faster on full-sample motion, bit for bit as before: 4.6 to 5.1% off
  `h264-10bit-b.mp4`'s steady frame on GitHub's x64 runner and 8.1 to 8.6% on its ARM64 runner in two `compare.yml`
  runs against main (`h264-10bit-motion.mp4`, with few full-sample vectors, unchanged). A block whose vector points at
  whole samples inside the picture copies the reference's rows, where it went through the interpolation's window.

### Fixed

- WebAssembly compiled ahead of time (`RunAOTCompilation`): a malformed MP4 file could bring the .NET runtime down (a
  Mono assertion) instead of throwing `InvalidDataException`. Mono's AOT code read a method's arguments wrong after an
  exception filter (`catch ... when`) had run, and `Demuxer.Open` disposed a garbage stream in one; the libraries use no
  exception filters now (the demuxers' malformed-input handling, `AdtsDemuxer.Probe`, the AAC decoder's threads, H.264's
  lost references when concealing errors), and the WebAssembly check runs the fuzz findings too.
- H.264: a stream that switches to an SPS of another bit depth, size or chroma format at a picture that is not IDR
  (which the standard forbids) no longer throws InvalidCastException or reads its references as the new format: they
  are dropped, so the picture's inter slices fail with InvalidDataException, or are concealed with `ConcealErrors`.
  Found by fuzzing.
- MP4: a track whose edit list starts before its first composition offset (media time 0 and B frames, as some muxers
  write it, not FFmpeg's) gives the timestamps FFmpeg does: they move back so that the first pts shown is 0, where
  they started at the offset (#252).
- AAC: parametric stereo that keeps fine IID indices (a frame without envelopes) under a header with coarse ones
  throws InvalidDataException, as FFmpeg rejects it, rather than IndexOutOfRangeException. Found by fuzzing.

## 0.4.0 – 2026-10-30

Each decoded frame's interlacing as FFmpeg reports it, and an MP4 demuxer that reads one track at a time and lists
packets without their data; CAVLC decoding is faster. New public API, hence a minor version; code built against 0.3
keeps working.

### Added

- Each decoded frame's interlacing as FFmpeg reports it: `VideoFrame.IsInterlaced` and `IsTopFieldFirst` (ffprobe's
  `interlaced_frame` and `top_field_first`), `FieldOrder` (the order FFmpeg's H.264 parser gives the picture, a new
  `FieldOrder` enum with `AVFieldOrder`'s values) and `IsCodedAsFields` (two field pictures), and
  `H264Decoder.FieldOrder`, the stream's as ffprobe prints it for MP4. H.264 follows the picture timing SEI's
  pic_struct and ct_type where there is one, else MBAFF or field coding and the field order counts; MJPEG frames are
  not flagged, as in FFmpeg (#225).
- `Mp4Demuxer`: a file's tracks can be read one at a time, `new Mp4Demuxer(stream, MediaTypes.Video)` (a new
  `MediaTypes` flags enum); the other track is left out as if the file had none, and builds no sample table and gives
  no packets. And `ReadPacketInfo()`, the packets `ReadPackets` gives without their data (a new `PacketInfo`: the
  size, flags and timestamps), from the sample tables alone, without reading the file or allocating an array a packet,
  for a duration pass, a packet list or a bitrate graph (#211). `Mp4Demuxer(Stream, bool leaveOpen = false)` becomes
  two constructors, `(Stream)` and `(Stream, bool)`, so that the new one can take `leaveOpen` as an optional
  parameter; calls compile and run as before.

### Changed

- H.264 decodes CAVLC streams faster, bit for bit as before: 3.2% off the 720p benchmark stream's steady frame
  (`h264-bench-720p.mp4`, Constrained Baseline) on GitHub's x64 runner, 1.8% off `h264-p-refs.mp4`, the CABAC
  samples unchanged (`compare.yml` against main). The residual reader takes the trailing ones' signs in one read and
  run_before from one lookup.

### Fixed

- `AviDemuxer`: an empty video chunk (a dropped frame) now takes its frame's tick, as in FFmpeg, so the packets after
  it have FFmpeg's pts rather than coming a frame early; it still gives no packet (#226).

## 0.3.0 – 2026-10-30

Each decoded frame's picture type (I, P, B), as FFmpeg reports it. A new public API, hence a minor version; nothing
is removed or changed, so code built against 0.2 keeps working.

### Added

- `VideoFrame.PictureType` and the `PictureType` enum (FFmpeg's `AVPictureType`, the same values): how each decoded
  frame was coded, as `ffprobe -show_frames` prints its `pict_type`. H.264 gives the type of the picture's first slice
  (of its first field, for a frame of two field pictures), MJPEG gives I, and a frame no decoder made None (#221).

## 0.2.5 – 2026-10-30

Faster H.264 decoding, the same output. The public API is that of 0.2.0, unchanged.

### Changed

- H.264 decodes faster on one thread, bit for bit as before: 2.7% on the 720p benchmark stream
  (`h264-bench-720p.mp4`) on GitHub's x64 runner and 0.8% on its ARM64 runner, the other H.264 samples within about
  1.5% either way (`compare.yml` against 0.2.4; the 4.5% first given here added up per-change profile estimates that
  overstated the whole). Each partition's reference picture is a byte handle rather than an object reference, Cb and Cr share one
  setup with a multiply-add chroma filter (SSSE3), 8-bit 4x4 and 8x8 inverse transforms run in 16-bit lanes (SSE2;
  32-bit for coefficients too large for them), CAVLC residual blocks read through a bit cursor in locals, `ReadUe`
  is inlined and a P_Skip macroblock no longer reads its neighbours twice (docs/conformance.md, Performance notes).

## 0.2.4 – 2026-10-30

H.264 concealment guesses motion in MBAFF frames too. The public API is that of 0.2.0, unchanged.

### Changed

- H.264 concealment (`ConcealErrors`) guesses the motion of missing macroblocks in MBAFF frames too, converting a
  neighbour's motion between field and frame macroblocks, where they copied their place in a reference
  (docs/conformance.md has what it gains and where it does not).

## 0.2.3 – 2026-10-29

H.264 right again under the WebAssembly interpreter (wrong from 0.2.0), a better guess for concealed macroblocks, and
less CPU with frame threading. The public API is that of 0.2.0, unchanged.

### Fixed

- H.264 under the WebAssembly interpreter (browser-wasm without AOT, SIMD on, the default): frames came out wrong from
  0.2.0 on, and differently from one decode to the next, as the interpreter narrows the deblocking strengths'
  comparison masks with saturation once it has optimized the function (#206). The masks are now narrowed as signed
  lanes, which every runtime narrows alike; CI's `wasm` job checks the interpreted build too.

### Changed

- H.264 concealment (`ConcealErrors`) guesses the motion of a missing macroblock from its neighbours (the candidate whose
  prediction matches their edges best), where it copied its place in one reference: on streams cut in packet after
  packet, the concealed frames come 0.5 to 15 dB closer to the clean decode (mean PSNR; docs/conformance.md), ahead of
  FFmpeg's on five of the eight measured. MBAFF frames still copy.
- H.264 frame threading spends 2 to 5% less CPU for the same speed: a picture waiting on its reference's rows waits for
  32 rows more once it has to block, and a reference's progress wakes only the waits it satisfies, which halves the
  context switches (GitHub's x64 and ARM64 runners, 4 threads).

## 0.2.2 – 2026-10-29

Faster MJPEG and HE-AAC on ARM64 (and x64 without AVX2). The public API is that of 0.2.0, unchanged.

### Changed

- MJPEG decodes about 3% faster on ARM64 (and x64 without AVX2): the 128-bit IDCT's butterflies are inlined, where
  they were a call a pass with their results through memory. GitHub's ARM64 runner, `compare.yml`; x64 with AVX2 is
  unchanged; the output is the same.
- HE-AAC decodes about 4% faster on ARM64 (and x64 without AVX2): SBR's patch filter, covariances and gains, and
  parametric stereo's all-pass filter have 128-bit paths, where they had only 256-bit ones and ran scalar there.
  GitHub's ARM64 runner, `compare.yml` (5.1 and v2 stereo); x64 with AVX2 runs the same code; the output is the same.

## 0.2.1 – 2026-10-29

Faster HE-AAC (the QMF banks by smaller transforms) and CABAC H.264, and memory management operation 5 in H.264 field
pictures. The public API is that of 0.2.0, unchanged.

### Added

- H.264: memory management operation 5 in field pictures, which threw `NotSupportedException`.

### Changed

- HE-AAC decodes faster: the full QMF synthesis bank takes two DCT-IVs of 64 points, each by a complex FFT of 32, where
  it took a complex FFT of 128, and the analysis bank a complex FFT of 32 for its 64 real values, where it took one of
  64, both with their twiddles in vectors. Together 14 to 16% faster on 5.1 HE-AAC and 8 to 10% on HE-AAC v2, on
  GitHub's x64 and ARM64 runners (`compare.yml`); the output moves only in its last bits (to 1e-12 of the peak).
- H.264 decodes CABAC streams faster: the deblocking filter's edge strengths of bi-predicted blocks in vectors, CABAC's
  context selection reading neighbours inside the macroblock directly, and its significance map without per-bin
  bounds checks. About 3% fewer instructions a frame on CABAC streams with B frames, 1 to 2% faster on the cloud VM
  measured (docs/notes/h264-speed-gap-2026-10.md); CAVLC streams are unchanged; the output is the same.

## 0.2.0 – 2026-10-28

Parametric stereo complete (mixing procedure Rb, phase parameters, 34 bands), long-term references in H.264 field
pictures, and faster H.264 motion compensation. The public API is that of 0.1.0, unchanged.

### Added

- H.264: long-term references marked in field pictures (an IDR field pair marked long-term, memory management
  operations 2, 3, 4 and 6 in field pictures), which threw `NotSupportedException`.
- HE-AAC v2: parametric stereo with mixing procedure Rb (icc_mode 3 to 5), which threw `NotSupportedException`.
- HE-AAC v2: parametric stereo in 34 bands (iid_mode or icc_mode 2 or 5), which threw `NotSupportedException`.
- HE-AAC v2: parametric stereo's phase parameters (IPD and OPD), which were ignored: a stream that sends them decoded
  without error but up to its whole peak off FFmpeg's output.

### Changed

- H.264 decodes faster: motion compensation's common partitions (one reference, bi-predicted, weighted) take direct
  paths to the kernels, which copy by rows at fixed widths. On GitHub's runners, timed in one run against the code
  before these changes: 4% (x64) to 5% (ARM64) faster on the 720p sample, about 3.5% on P-frame streams, 1 to 3% with
  B frames, unchanged at 10 bits; the output is the same.

### Documentation

- Native AOT on x64: set `IlcInstructionSet` (x86-64-v3) in the app, or the AVX2 and 256-bit paths are left out (AAC
  and HE-AAC take about a third longer); README's Native AOT section has the timings.
- HE-AAC v2 with downsampled SBR (parametric stereo at the core's rate) is checked against FFmpeg now, a sample from a
  patched FDK encoder; it was untested.

### Fixed

- Native AOT for the default x64 target: MJPEG decoded at half speed and H.264 6% slower, the portable byte shuffle
  being a software call there; x64 now uses SSE2's unpacks and SSSE3's pshufb directly.

## 0.1.0 – 2026-10-27

The first stable version: the public API of 0.1.0-preview.3, documented, and from here on changed only in a new minor
version (see the top of this file).

### Added

- The packages carry their XML documentation (`lib/net10.0/*.xml`), so IntelliSense shows it; every public member has
  a doc comment, and the build fails on one without (CS1591).
- nuget.org shows a readme for every package (`PACKAGE.md`): what they decode, an example, links to the documentation.

### Changed

- The packages depend on each other at exactly one version (`[0.1.0]`, not `0.1.0` or later): they share internals,
  so a mix of versions could fail at run time, and NuGet now reports one instead (NU1107 or NU1608).

## 0.1.0-preview.3 – 2026-10-27

### Breaking changes

- `H264Decoder(VideoStreamInfo)` crops frames to the container's picture size where it takes only columns and rows
  off the right and bottom within the last macroblock, as FFmpeg's decoder does: an Apple-encoded 640x360 MP4 that
  stores 640x359 now decodes to 640x359. The span constructor is unchanged.

### Fixed

- H.264: a field picture's packet that goes on with a slice of the other field or of a frame is refused (as a packet
  holding more than one picture) rather than decoded into the field, where concealing errors could throw
  `ArgumentOutOfRangeException`; and frames larger than any level allows (over 139,264 macroblocks) are refused with
  `NotSupportedException` rather than decoded in time and memory without bound. Both from fuzzing.
- H.264, from a longer fuzzing round: a slice with more reference list modifications than its list has entries, SPS
  crop offsets that overflow when scaled, and a first_mb_in_slice of 2^31 or more are `InvalidDataException`s
  rather than `ArgumentOutOfRangeException`, `OverflowException` and `IndexOutOfRangeException`.

## 0.1.0-preview.2 – 2026-10-26

The public API before it freezes ([docs/decisions/2026-10-26-api-before-0.1.0.md](docs/decisions/2026-10-26-api-before-0.1.0.md)).

### Breaking changes

- `Mp4Demuxer`, `AviDemuxer` and `AdtsDemuxer` are in `ManagedCodecs.Containers`, beside `Demuxer` (they were in
  `ManagedCodecs.Containers.Mp4`, `.Avi` and `.Adts`): drop those `using` lines.
- No longer public, as implementation details: MJPEG's JPEG parser (`JpegComponent`, `JpegFrameHeader`,
  `JpegMarker`, `JpegMarkerReader`, `JpegSegment`), `PixelFormatExtensions.Of`, `AudioFrame.DropStart`,
  `VideoFramePool`, and `VideoFrame.IsDamaged`'s setter (the decoder sets it).

### New

- `AudioFrame.Pts`: the frame's first sample's time, in the stream's time base (the packet's, moved by the samples
  dropped from its start), as FFmpeg gives it.

## 0.1.0-preview.1 – 2026-10-26

The first preview: what the README's Status section lists, as it stands.

### New

- MJPEG decoding, bit-exact with FFmpeg: baseline and extended Huffman JPEG in 4:2:0, 4:2:2, 4:4:4 and gray, restart
  markers, interlaced AVI1.
- H.264 decoding, bit-exact with FFmpeg: Constrained Baseline, Main, High, High 10, High 4:2:2 and High 4:4:4
  Predictive; CAVLC and CABAC, I, P and B, weighted prediction, the 8x8 transform, scaling matrices, MBAFF and field
  pictures, monochrome to 4:4:4 (also separate colour planes), 8 to 14 bits, lossless; frame threading
  (`H264Decoder.Threads`), error concealment (`ConcealErrors`), borrowed frames.
- AAC decoding: AAC LC from mono to 7.1, within 2^-17 of FFmpeg's output; HE-AAC (spectral band replication and
  parametric stereo).
- AVI, MP4 (QuickTime, fragmented MP4, edit lists as FFmpeg applies them) and ADTS demuxing; `Demuxer.Open` picks one by
  content. Asynchronous reads (`ReadPacketsAsync`).
- Pure managed, no native binaries: Native AOT, ARM64 and WebAssembly (browser-wasm) run the same code.

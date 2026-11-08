# Containers: notes for working on the AVI, MP4, Matroska, ADTS and MP3 demuxers

The repository's CLAUDE.md (process, core idea, cross-cutting gotchas) applies too.

## Gotchas (each cost time once)

- A dts after its pts (a 'ctts' whose last two entries are more negative than the shift; docs/conformance.md, MP4):
  FFmpeg reports "Invalid timestamps" (`-v debug`) and keeps it for H.264, but fills a dts in from the pts for a codec
  without reordering (MJPEG, audio), which Mp4Demuxer doesn't. Its command line then drops the frame whose pts goes
  back, so such a sample has packet references only.
- FFmpeg trims MP4 audio by the edit list asymmetrically: exactly media_time samples at the start (skip-samples side
  data, across packets), but at the end it drops whole packets that start past the edit, cuts the last one's
  duration (rounded to nearest) and still decodes all its samples. With several edits (docs/conformance.md, MP4) a
  later audio edit starts at the next whole packet, every edit is preceded by discarded packets (a second of audio;
  video from the keyframe before), and an empty edit after a real one replays the media's start. `ffmpeg -c copy -f
  framemd5` rewrites decode timestamps that go back (an edit's discarded packets): compare packets with
  `ffprobe -show_packets -show_data_hash MD5` (make-samples.sh's `.packets.csv`).
- In fragmented MP4, FFmpeg's packet durations are not the file's: its demuxer leaves them unset and libavformat
  fills in the codec frame size (1024 for AAC). Compare with FFmpeg's next dts instead (Mp4ConformanceTests does).
- Matroska: FFmpeg's muxer writes only SimpleBlocks, never lacing or BlockGroups, and no DefaultDuration for audio;
  `tools/mkv-rewrite.py` rewrites its files (`--lace xiph|ebml|fixed`, `--block-groups`, `--unknown-size`, `--zlib`,
  `--discard-padding NS`; `--dump` prints a file's elements). FFmpeg's muxer writes DiscardPadding for Opus only (from
  the encoder's side data), and a rotation as a Projection's roll. Fixed-size lacing needs frames of one size: PCM.
  FFmpeg's own Matroska reading shows only under `ffprobe -fflags +nofillin+noparse`: by default libavformat fills in durations (AAC's from the frame size) and
  decode times, and marks every audio packet a keyframe, and the Opus parser reads durations from the packets.
- Matroska decode times: FFmpeg's come from the H.264 SPS's reorder depth (`has_b_frames`), not from the file; the
  demuxer doesn't parse the SPS and takes the fewest frames that work, so its dts can be later than FFmpeg's.
- .NET's DeflateStream returns what it inflated from a zlib frame cut short, without an error: the Adler-32 after the
  data is what catches it (as zlib's own inflate does).
- Raw MP3: FFmpeg's encoder writes its LAME tag with "Lavc" (not "LAME") as the encoder, and FFmpeg's demuxer takes it as
  LAME's; its info frame counts the frames after it, not itself. `ffprobe -show_packets` shows the gapless trim as
  "Skip Samples" side data (1105 on the first packet, the padding less 529 on the last).
- AVI: FFmpeg's muxer drops the second video frame (an empty chunk) while interleaving audio, so every FFmpeg AVI with
  audio has a pts gap (0, 2, 3, ...): tests that tick at the first frames' spacing can't take them. It writes the
  audio's chunk id ('01wb') in the header's OpenDML 'indx' too: search the 'movi' list for chunks. FFmpeg's muxer
  never writes the CBR MP3 layout: `tools/avi-mp3-cbr.py` rewrites its files into it, and FFmpeg's packet times there
  are its parser's interpolation, not the frames' (docs/decisions/2026-11-08-avi-audio.md).

# Containers: notes for working on the AVI, MP4 and ADTS demuxers

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

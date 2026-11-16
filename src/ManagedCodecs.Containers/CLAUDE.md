# Containers: notes for working on the AVI, MP4, Matroska, ADTS, MP3 and MXF demuxers

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
  "Skip Samples" side data (1105 on the first packet, the padding less 529 on the last; a packet the padding covers
  whole skips all its 1152, not a discarded packet).
- Raw MP3, more of FFmpeg's demuxer (found by altering libmp3lame's files and reading ffprobe): a first frame whose
  header differs from the next one's in the mode, copyright, original or emphasis bits is no packet, nor the next while
  it differs from its own next (`MpegAudioHeader.SameStream`); differences later in the file don't count. A Xing, Info
  or VBRI frame is no packet only when it counts the frames or the bytes (an Info frame with neither is one). The LAME
  tag's CRC is not checked. libmp3lame's info frame lacks the original bit its audio frames have, so it differs from
  them too: alter one rule at a time when testing another.
- The last MP4 audio packet's duration, cut or stretched to the edit's end, stops at the media's end as FFmpeg compares
  them: the packet's presentation time against the media's end in media time, without the edit's media time taken
  off, so an edit from media time t may run up to t past the media (a raw MP3 remuxed into MP4, whose 1105-sample
  start FFmpeg's muxer copies into the edit: mp3-vbr-22k-remux; aac-fdk-pce-remux starts at 0 and stops at the media).
  Found by rewriting edit lists (`tools/mp4-edit-list.py`, media times 0, 1, 20 and 1105) and reading ffprobe.
- QuickTime PCM ('sowt', 'twos', 'in24', ...): 'stts' gives one tick a sample frame and 'stsz' a sample a frame, so
  read sample by sample it is one packet per frame; FFmpeg reads such audio by chunk, in pieces of up to 1024 frames
  (`Mp4Demuxer.PcmPieces`). Its muxer writes 1024-frame chunks, which hides the cutting: a hand-built file shows it.
- AVI: FFmpeg's muxer drops the second video frame (an empty chunk) while interleaving audio, so every FFmpeg AVI with
  audio has a pts gap (0, 2, 3, ...): tests that tick at the first frames' spacing can't take them. It writes the
  audio's chunk id ('01wb') in the header's OpenDML 'indx' too: search the 'movi' list for chunks. FFmpeg's muxer
  never writes the CBR MP3 layout: `tools/avi-mp3-cbr.py` rewrites its files into it, and FFmpeg's packet times there
  are its parser's interpolation, not the frames' (docs/decisions/2026-11-08-avi-audio.md).
- AVI's H.264 packets carry decoding times (FFmpeg's have no pts at all), and FFmpeg's frames are shown at the packet
  times the reorder delay later: frame k at k + 2 for libx264's B pictures. Seek by the index's keyframes therefore
  backs off H.264's largest delay (16 frames); a seek-frame reference shows the frame FFmpeg's `-ss` gives, from the
  keyframe before the one at the time.
- MXF clip wrapping: FFmpeg's `-f mxf_opatom` writes the whole clip as one element (H.264 with index entries, DNxHR
  and DV with a constant edit unit size, PCM 48 kHz mono only, with an edit unit a sample) but labels its essence
  container frame-wrapped: the index, not the label, says an element holds several edit units. Its demuxer reads
  clip-wrapped sound in 1920-sample pieces whatever `-mxf_audio_edit_rate`. `-f mxf_d10` (D-10, MPEG-2 IMX) takes
  only 30, 40 or 50 Mb/s and a whole number of bytes a frame (50 Mb/s at 29.97 isn't: the 525 sample is 30 Mb/s);
  FFmpeg's demuxer gives its 8-channel AES3 sound elements as PCM, so their packets' MD5s are the PCM's.
- MXF files cut short: compare with FFmpeg's demuxer under `ffprobe -fflags +nofillin+noparse`. By default
  libavformat's H.264 parser cuts a clip-wrapped H.264 file's one partial packet (its index went with the footer) into
  pictures, which the demuxer doesn't. Without the index, the keyframes there are libavformat's parsers' (MPEG-2's I
  pictures by their picture headers, which `MxfDemuxer` reads too; mxf-mpeg2-b-cut). Cut by the source's packet positions (make-samples.sh's `mxf_cut`), not by a share
  of the file: generated samples differ between machines, and the cut has to fall in the same kind of element.
- MXF: FFmpeg's `-f mxf` muxer writes OP1a, frame-wrapped (`mxf_opatom` and `mxf_d10` are muxers of their own), audio
  at 48 kHz only ("only 48khz is implemented"), and always a random index pack; written to a pipe, the header and body partition packs leave FooterPartition 0. Its demuxer logs
  each partition pack it reads under `ffprobe -v trace` (make-samples.sh's `.partitions.csv`), but never reads the
  footer of a pipe-written file whose random index pack is gone: `tools/mxf-rewrite.py` files take their source's
  reference instead (`--strip-rip`, `--run-in N`).
- MXF stream facts: `ffprobe -find_stream_info` keeps decoder probing on (it is a boolean, on by default); only
  `-nofind_stream_info` shows what FFmpeg's demuxer reads from the header metadata: the stored height (128 for H.264's
  120 coded in macroblocks), no sample aspect ratio and no field order for separate fields, which come from the
  decoder otherwise (make-samples.sh's `.headers.json`). Its codec names follow the descriptor's coding label, else the
  essence container's; PCM in the AES3 and Broadcast Wave mappings is `pcm_s<bits>le` (ST 382: little-endian).
- MXF colour: FFmpeg's muxer writes the descriptor's labels and reference levels from `-color_primaries`,
  `-color_trc`, `-colorspace` and `-color_range`, with `-c copy` too (remux raw H.264 to make the descriptor differ
  from the VUI), but one label for several values (smpte170m's transfer reads back bt709, its matrix bt470bg).
  `ffprobe -nofind_stream_info` shows the descriptor's; with probing the stream shows the decoder's. To see what
  FFmpeg reads for a label or a level its muxer never writes, patch the value's bytes in a sample and run ffprobe
  (the expectations of `MxfHeaderTests` were made that way); the SMPTE registers (registry.smpte-ra.org, Labels.xml
  and Elements.xml) answer from the cloud sandbox.
- MXF reordering: FFmpeg's `-f mxf` muxer writes temporal offsets only for MPEG-2 (`-bf 2`: the picture's delta
  entry, the second after the system item's, has PosTableIndex -1), never for H.264. The offsets lead from display
  order to stored order (the picture shown n-th is stored at n + offset), and FFmpeg's demuxer's decode times then
  start at minus the largest offset. An open GOP's I picture has no random access flag in the index, though
  libavformat's parser marks it a keyframe; `MxfDemuxer` does too, for MPEG-2, by the entry's prediction flags.
- MXF from other writers: none can be downloaded in the cloud sandbox (archive.org's data hosts are refused), so they
  are checked in CI only: `tools/realworld.py`'s PACKET_FILES, `mcdec --packets` against `ffprobe -fflags
  +nofillin+noparse`. Run `realworld.yml` on the branch (workflow_dispatch) and read the job's log; a difference in
  times prints both sides' around the packet. `tools/realworld.py --packets <file>` runs the comparison on a local
  file (every samples/mxf-*.mxf passes it). What the first files showed is in
  docs/decisions/2026-11-15-mxf-colour-and-what-is-left.md.
- MXF packets: FFmpeg's demuxer itself gives no keyframe flags (libavformat's parsers and its intra-only rule add
  them), no durations, and no video times where it reads no index entries (DNxHR's constant-size index; the footer's
  index of a pipe-written file without a random index pack, which it never reaches); compare against `.packets.csv`
  and the demuxer's own `.packets.nofillin.csv` as `MxfConformanceTests` does. Its H.264 packets' pts are their dts
  (FFmpeg's muxer writes no temporal offsets), so `ffmpeg -f framemd5` drops a frame and retimes the rest; with
  `-fps_mode passthrough` it keeps every frame (`.h264.framemd5`). Its frame times there are a guess, not a reference:
  k + D on the 2-second samples, 0 2 3 5 6 7 7 7 on mxf-h264-small, and from 0 where its packets have no times;
  compare the MD5s only.
- MXF sound tracks: `_packets` lists every sound track's packets, selected or not (SelectAudio is a filter, Refresh
  and Seek work on all of them). FFmpeg's muxer writes a sound element a track after each frame's picture, mono for a
  mono input (`-map` each), and its demuxer numbers the streams as the material package's tracks; `.packets.csv` holds
  every stream, so filter it by the selected ones. Don't edit make-samples.sh while it runs: bash reads the script as
  it goes, and the run fails at a shifted line.

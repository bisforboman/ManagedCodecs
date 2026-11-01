# MJPEG: notes for working on the MJPEG decoder and its samples

The repository's CLAUDE.md (process, core idea, cross-cutting gotchas) applies too.

## Gotchas (each cost time once)

- Interlaced MJPEG: FFmpeg's encoder refuses interlacing (make-samples.sh encodes fields as images and pairs them
  into AVI1 packets in Python). Its decoder takes an image of less than 3/4 of the stream's height (integer division)
  as a field, ignores the AVI1 field byte, and puts the first field on the odd rows unless the decoder's field order
  is tt, tb or bt (`-field_order` as an input option shows it); one field a packet it does not decode at all.
- FFmpeg's MJPEG encoder writes no gray: `-pix_fmt gray` silently encodes yuvj444p. A gray sample comes from
  ImageMagick (`gray.avi`).
- Damaged images (docs/conformance.md, MJPEG): FFmpeg is the oracle, found by measuring, as T.81 says nothing. Its
  undecoded part is its buffer's old contents, 0 only in a buffer it has not used: compare a cut frame as the
  second of three (`[clean, cut, clean]`, made with image2: `ffmpeg -framerate 25 -i f%02d.jpg -c:v copy`, the first
  file intact, which image2 probes); in a longer file the cut frames reuse buffers and differ. A single-frame AVI of
  a cut image doesn't open at all (image2's probe). Its debug log (`-v debug`) gives "escaping removed N bytes" and
  "marker parser used N bytes" for the SOS: the unescaped scan's length, which is how the bytes it reads past the data
  were found. Its "overread N" is at most 8 (its reader stops counting there), where the decoder counts every bit.
- FFmpeg refuses a packet too small for its image's size silently ("Invalid data found", no frame, no message): a
  quarter byte for each 8x8 block, what comes before SOI included, so padding before SOI turns a refused frame into a
  decoded one. Not the scan's content: the threshold moved with the size alone.
- "error dc" and "error count" in FFmpeg's log keep the frame (partly decoded); only the size rule and a cut first
  field of an interlaced packet drop one.
- ffprobe's frame `sample_aspect_ratio` is the container's where it has one (AVI's 'vprp', MP4's 'pasp'), not the
  JFIF density the decoder read: to see the decoder's, zero the container's ratio (a 'pasp' of 0:0) in a scratch copy.

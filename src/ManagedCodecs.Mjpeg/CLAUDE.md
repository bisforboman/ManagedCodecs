# MJPEG: notes for working on the MJPEG decoder and its samples

The repository's CLAUDE.md (process, core idea, cross-cutting gotchas) applies too.

## Gotchas (each cost time once)

- Interlaced MJPEG: FFmpeg's encoder refuses interlacing (make-samples.sh encodes fields as images and pairs them
  into AVI1 packets in Python). Its decoder takes an image of less than 3/4 of the stream's height (integer division)
  as a field, ignores the AVI1 field byte, and puts the first field on the odd rows unless the decoder's field order
  is tt, tb or bt (`-field_order` as an input option shows it); one field a packet it does not decode at all.

# MP3: notes for working on the MP3 decoder and its samples

The repository's CLAUDE.md (process, core idea, cross-cutting gotchas) applies too. The plan and what is left:
docs/decisions/2026-11-08-mp3-decoder.md.

## Samples: which encoder writes what

FFmpeg's libmp3lame and libshine (make-samples.sh, `mp3-*`). LAME writes MPEG-1, MPEG-2 and MPEG-2.5 at every rate,
mid/side (for noise; it picks left/right for clean tones), long, start, short and stop blocks (for transients), the
bit reservoir, count1 tables A and B. shine writes long blocks only, no reservoir, and count1 quadruples that read past
their granule's end (FFmpeg drops them; `DroppedQuadruples`). Neither writes intensity stereo, mixed blocks, CRCs,
dual channel or free format: `Mp3UnitTests` covers intensity stereo (MPEG-1's and the lower rates') and mixed blocks
by the spec's equations; CRCs by CRC-16/CMS's check value. FFmpeg's MP4 muxer refuses MPEG-2.5's rates (8, 11.025,
12 kHz): those samples are Matroska.

## Gotchas (each cost time once)

- Region 1 of a short block's big values starts after three short bands: line 36, but 72 at 8 kHz (FFmpeg's
  `init_short_region` and the spec agree; a guess of 36 everywhere decoded 8 kHz short blocks wrong from the first).
- FFmpeg's Matroska output of a LAME stream is shorter than every packet decoded: the muxer writes the encoder's end
  padding as the last block's DiscardPadding, and FFmpeg drops it (no CodecDelay: the priming plays). So Matroska
  samples have only a name.mp3.trim.f32 reference.
- References are name.mp3.f32 and name.mp3.trim.f32, not name.f32: AacConformanceTests decodes every sample with a
  name.f32.
- A stream cut with `-ss ... -c copy` starts with frames whose main_data_begin reaches before the file: FFmpeg skips
  granules (silent, the IMDCT's overlap still output) by their part2_3_length until the data is there (`mp3-cut`).
- The lower rates' illegal intensity position is a partition's largest value (13818-3, as minimp3 reads it). No
  encoder at hand writes intensity stereo, so no sample checks this, or any intensity stereo, against FFmpeg.

# MP3: notes for working on the MP3 decoder and its samples

The repository's CLAUDE.md (process, core idea, cross-cutting gotchas) applies too. The plan and what is left:
docs/decisions/2026-11-08-mp3-decoder.md (steps 2 and 3, the decoder and playback, are done; of step 4, the
filterbanks).

## Samples: which encoder writes what

FFmpeg's libmp3lame and libshine (make-samples.sh, `mp3-*`). LAME writes MPEG-1, MPEG-2 and MPEG-2.5 at every rate,
mid/side (for noise; it picks left/right for clean tones), long, start, short and stop blocks (for transients), the
bit reservoir, count1 tables A and B. shine writes long blocks only, no reservoir, and count1 quadruples that read past
their granule's end (FFmpeg drops them; `DroppedQuadruples`). Neither writes intensity stereo, mixed blocks, CRCs,
dual channel or free format: `tools/mp3-rewrite.py` sets MPEG-1 intensity stereo and mixed blocks in a raw LAME
stream (no ID3v2 tag or Xing frame: `-id3v2_version 0 -write_xing 0`; mkv-mp3-intensity, mkv-mp3-mixed);
`Mp3UnitTests` covers intensity stereo (MPEG-1's and the lower rates') and mixed blocks by the spec's equations; CRCs
by CRC-16/CMS's check value. The ISO/IEC 11172-4 streams were checked once, not committed (docs/conformance.md).
FFmpeg's MP4 muxer refuses MPEG-2.5's rates (8, 11.025, 12 kHz): those samples are Matroska.

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
- The lower rates' illegal intensity position is a partition's largest value (13818-3, as minimp3 reads it).
- FFmpeg's decoder (6.1) misses the ISO reference on the lower rates' intensity stereo (RMS 4e-2 on l3-test45): don't
  take it as the oracle there; the unit tests check the equations.
- mixed_block_flag counts in start and stop blocks too: their lowest subbands take the normal window. Intensity
  stereo's last band at the lower rates is the centre after a band the right channel codes a value in; MPEG-1's copies
  band 20's position (as FFmpeg's; the centre there was 1.6e-2 off it). Each missed the ISO streams' reference or
  FFmpeg once. A sample must have values in the top band to show the last band's rule: LAME's lowpass leaves it empty
  unless the signal has noise up there (mkv-mp3-intensity).
- A test that stops at its first failing sample reports that sample's difference, not the largest: compare the whole
  output (largest, mean, where) before calling a difference rounding.
- The fast synthesis and long IMDCT must give the definitions' doubles, not only their floats: a sum in another order
  differs in its last bits, which the rounding to float almost never shows (reversing a vector loop passed a test of
  the float output). `FastFilterbanksGiveTheDefinitionsBits` compares V and the sums before rounding.

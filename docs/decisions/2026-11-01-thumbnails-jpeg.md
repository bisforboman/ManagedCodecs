# JPEG output for thumbnails (2026-11-01)

Amends [the thumbnails decision](2026-10-31-thumbnails.md), whose "Image output" chose PNG written here and raw RGBA
for callers who want JPEG through an image library. Decided by the owner; built in the same pull request.

## Why

The first consumer outside this repository, shotdetector-videorag (a video reader for Microsoft.Extensions.DataIngestion),
takes each shot's middle frame to a vision model as `image/jpeg`. It moves its stills from FFmpeg's libraries to
ManagedCodecs.Thumbnails so that it needs no native code (as its transcripts already do, decoding AAC here). PNG
works but is the wrong format for camera and film frames sent over a network to a model, and the image libraries the
first decision ruled out as dependencies (SkiaSharp native, ImageSharp's split license) are no better for a consumer.
A baseline JPEG encoder is small: the tables are the ones the MJPEG decoder already has (Annex K), and the rest is a
forward DCT and the entropy coder of F.1.2.

## What

- `Thumbnail.SaveJpeg(Stream, int quality = 85)` and `Thumbnail.ToJpeg(int quality = 85)`, beside `SavePng`/`ToPng`.
  In ManagedCodecs.Thumbnails, not ManagedCodecs.Mjpeg: the codec packages stay decoders, and a general
  `VideoFrame` encoder would be a larger API (other pixel formats, subsampling, restart intervals) with no consumer.
- Baseline JFIF 1.02 (ITU-T T.81): RGB to YCbCr by JFIF's equations, full range; 4:2:0, each chroma sample the mean
  of the four it covers; the forward DCT of A.3.3; Annex K.1's tables scaled by `quality` as libjpeg's `-quality`
  scales them (the scaling every JPEG user knows; 50 is K.1 as printed); Annex K.3's Huffman tables (no optimized
  tables: a thumbnail is small, and K.3 is what MJPEG decoders already assume). Alpha is dropped.
- Integer arithmetic throughout (16-bit fixed-point colour conversion, a 2^15 cosine basis), so the Unity build
  (netstandard2.1, Mono or IL2CPP) writes the same bytes as .NET; a test pins one file's SHA-256, and another that no
  basis value rounds near a tie, where another runtime's `Math.Cos` could round it the other way.
- No vector path: at thumbnail sizes (320 px) the encode is well under the frame's decode.

## How it is checked

`JpegTests`: the DCT against A.3.3's definitional sum (within 0.05 on random and extreme blocks), Table F.1's
categories, the quality scaling at 50, 75, 100 and 1, byte stuffing in the scan, files read back by this repository's
MJPEG decoder (size, format, luma PSNR), and where ffmpeg is installed (CI) by FFmpeg's JPEG decoder. The PSNR floors
were set from libjpeg on the same pictures: ImageMagick at the same quality and 4:2:0 gets the same figures (26.2 dB
RGB at quality 95 on the coloured-edge picture, where FFmpeg's own MJPEG encoder at `-q:v 2` gets 27.0; 32.2 dB luma
on the 8x8 one at 85).

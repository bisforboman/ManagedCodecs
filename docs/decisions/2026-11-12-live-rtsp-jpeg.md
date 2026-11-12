# Live camera viewing, step 3: JPEG over RTP (2026-11-12)

A plan, decided on by the owner (Decided, below). Nothing here is built yet. It follows step 2, RTSP with H.264
(docs/decisions/2026-11-10-live-rtsp-h264.md, built), whose decision 5 named JPEG over RTP (RFC 2435) the likeliest
next step. Cameras that serve MJPEG over HTTP often serve the same JPEGs over RTSP too, and older or cheaper ones serve
nothing else there. A camera whose RTSP stream is JPEG now gets "The camera's SDP has no H.264 video track."

## What RFC 2435 sends

- **No JPEG headers.** Every packet has an 8-byte header: a fragment offset (24 bits, where this packet's bytes go in
  the frame's scan data), a type, a quality Q, and width and height in 8-pixel units (so at most 2040x2040). Then a
  slice of the entropy-coded scan, nothing else. The receiver rebuilds SOI, DQT, DHT, SOF0, SOS and EOI itself.
- **Types**:
  - 0 is 4:2:2 (Y sampled 2x1).
  - 1 is 4:2:0 (Y sampled 2x2).
  - 64 and 65 are the same with restart markers. A 4-byte restart header follows the main one: the restart interval
    (the DRI to rebuild), and which restart intervals the packet holds.
  - Others (2 to 63, 66 to 127) are reserved; 128 to 255 are defined out of band, which no camera seen documents.
  - Always baseline, 8-bit, three components, and always T.81 Annex K.3's Huffman tables: the stream has no way to
    send others. That is why FFmpeg refuses to packetize JPEG that wasn't encoded with `-huffman default`.
- **Quantization**:
  - Q 1 to 99: the receiver computes the tables from Annex K.1's, scaled by Q as the IJG library does (RFC 2435,
    Appendix A, gives the computation).
  - Q 128 to 255: the tables come in-band, in the first packet of a frame (offset 0), behind a quantization header.
    A later frame with the same Q may send a length of 0 and reuse them.
  - Q 0 and 100 to 127 are reserved.
- **Framing**: one RTP timestamp per frame (90 kHz), and the marker bit on its last packet. The payload type is the
  static 26, so an SDP can have `m=video 0 RTP/AVP 26` with no `a=rtpmap` at all.

## Measured (2026-11-12)

Recorded with tools/rtp-record.py, 640x480, and replayed into FFmpeg 6.1's depacketizer.

| Packetizer | Input | Type | Q |
|---|---|---|---|
| FFmpeg 6.1 (`-f rtp`) | `mjpeg -huffman default`, yuvj420p | 1 | 255, tables in every frame |
| FFmpeg 6.1 | yuvj422p | refused ("Only 1x1 chroma blocks are supported") | |
| GStreamer 1.24 (`rtpjpegpay`) | `jpegenc`, I420 | 1 | 255 |
| GStreamer 1.24 | `jpegenc`, Y42B (4:2:2) | 0 | 255 |
| GStreamer 1.24 | JPEGs with restart markers (Pillow 12, `restart_marker_rows=1`) | 65 | 255 |

- **FFmpeg's depacketizer as the oracle:** it read all three types. Its frames for the restart-marker stream had the
  same MD5 as FFmpeg's decode of the JPEG files that went in. So the oracle needs no reference of its own: a frame
  rebuilt from RTP must decode to exactly what the JPEG that was packetized decodes to.
- **Q below 128:** neither packetizer sends it, so hand-built packets cover it. FFmpeg's depacketizer computes those
  tables too, so hand-built packets replayed into it check our tables against its.

## What the decoder and the player already give

- **`MjpegDecoder`** takes a whole JPEG, uses Annex K.3's tables when there is no DHT, and reads DRI and restart
  markers. A depacketizer that rebuilds a complete JPEG for each frame needs no decoder change.
- **`LivePlayer`'s MJPEG path** already does the rest:
  - Every JPEG stands alone, so it keeps only the newest unparsed frame (newest wins, before decoding).
  - It counts a JPEG the decoder refuses as damaged and goes on.
  - There is no IDR picture to wait for, so `MaxLag` has nothing to do: nothing queues.
- **The RTSP session** (`RtspConnection`, authentication, keep-alives, reconnects) doesn't care what the track carries.

## Proposal

1. **`RtpJpegDepacketizer`** (internal, Playback), beside `RtpH264Depacketizer`: RTP packets in, a complete JPEG per
   frame out.
   - It collects a frame's packets by timestamp and ends the frame at the marker bit (or at a new timestamp).
   - It rebuilds SOI, DQT (in-band, cached by Q, or computed for Q 1 to 99), DHT (K.3), SOF0 from the type and size,
     DRI for types 64 and 65, SOS, the scan bytes, then EOI.
   - It refuses, as `NotSupportedException` at the first packet, what can't be rebuilt: reserved and dynamic types,
     and reserved Q values. A table length of 0 with no tables cached for that Q drops the frame instead.
2. **The track**: `Sdp` finds the first video track it can play, H.264 or JPEG. JPEG means payload type 26 or an
   `a=rtpmap` encoding of `JPEG`, in SDP order.
   - `RtspSource` gains nothing: the camera's SDP decides.
   - The error for an SDP with neither becomes "no H.264 or JPEG video track".
   - `LivePlayer` sends a JPEG track's frames to its MJPEG path. Their time is when the frame's last packet arrived,
     as for MJPEG over HTTP.
3. **Loss**: a sequence-number gap or a hole in the fragment offsets means the frame is incomplete. It is dropped
   (decision 1), and counts in `LostPackets` and `DamagedFrames`.
4. **Statistics**: no new members. `ReceivedFrames` counts the frames rebuilt, and `LostPackets` and `DamagedFrames`
   mean what they mean for H.264.
5. **Not in step 3**:
   - Sizes over 2040 pixels. RFC 2435 can't say them, and cameras that send them do so in ways of their own; none is
     in reach to measure.
   - Types 128 to 255.
   - Interlaced JPEG over RTP (RFC 2435's type-specific field, which neither packetizer sets).
   - UDP and RTSPS, as for H.264.

## Checks

- **Recordings, by each packetizer above**, through the depacketizer:
  - Every rebuilt JPEG must decode to the same frame as the JPEG that was packetized. For FFmpeg's, that JPEG is
    `ffmpeg -c:v mjpeg -huffman default` written to a file with the same settings, which is deterministic. For
    GStreamer's, it is the JPEG files fed to it.
  - FFmpeg's depacketizer agrees on all of them (measured above; the tests needn't run it).
- **Hand-built packets:**
  - Q 1 to 99. The tables are checked against RFC 2435 Appendix A's computation, and once against FFmpeg's
    depacketizer by replaying them (recorded in docs/conformance.md, as for the other one-off checks).
  - Q 128+ with a table length of 0 reusing an earlier frame's tables.
  - Missing first, middle and last packets, a frame without its marker bit, reserved types and Q values.
- **LivePlayer:** `LiveRtspTests` gains a JPEG track: lockstep frames against `MjpegDecoder` on the source JPEGs,
  lost packets counted, and the SDP choice (JPEG only, H.264 then JPEG, payload type 26 with no rtpmap).
- **Real servers:**
  - Nightly, `tools/rtsp-nightly.py` gains a JPEG run: mediamtx serving FFmpeg's push of an MJPEG clip, each frame
    checked with `mcdec --live` as for H.264.
  - By hand, GStreamer's RTSP server (`tools/gst-rtsp-server.py` with `jpegenc ! rtpjpegpay`).
- **Mutation entries** for the rules FFmpeg's and GStreamer's streams never reach: Q below 128, a table length of 0,
  restart headers on fragments that don't start at a restart interval.

## Decided

Asked with the multiple-choice prompt; the owner took each recommendation (2026-11-12).

1. **An incomplete frame. Decided: (a), drop it.**
   - **(a) Drop it** (recommended). The previous frame stays on screen, as when a JPEG over HTTP fails to decode.
     Simple, and nothing shown is wrong.
   - (b) Rebuild what arrived up to the first hole and let `MjpegDecoder` decode the rest as damaged (its handling
     of truncated JPEGs follows FFmpeg's). That shows a partial frame, and with restart markers the later intervals
     could even be placed. Over TCP losses are rare (the camera dropping packets under load), so this is complexity
     for little.
2. **GStreamer's recordings. Decided: (a), commit them.** the 4:2:2 and restart-marker streams can only come from GStreamer, which CI doesn't
   install.
   - **(a) Commit them** (recommended), with the JPEGs that went in, at 320x240 and 10 frames each (about 200 KB), in
     `samples/gstreamer/` beside `samples/jm/` and `samples/openh264/`. The script that made them goes in tools/.
   - (b) Install GStreamer in CI and record them in make-samples.sh. That's more CI time and a second packaged tool
     to keep working for two streams that don't change.
3. **Mixed tracks. Decided: (a), the first in SDP order.** when a camera's SDP offers H.264 and JPEG video, which plays?
   - **(a) The first in SDP order** (recommended). It is the camera's own order, and cameras put one video track per
     stream URL anyway.
   - (b) H.264 first, whatever the order. That gives less bandwidth for the same picture, but it overrides the camera
     on a case that hasn't been seen.

A consumer-visible change (a camera that was refused now plays), so a CHANGELOG line under "Unreleased" when it is
built. No public API changes.

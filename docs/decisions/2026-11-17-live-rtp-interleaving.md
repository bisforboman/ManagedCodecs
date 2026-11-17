# Live camera viewing: RTP's interleaved modes (2026-11-17)

A plan, with decisions for the owner (below). Issue #527's "Not supported": RFC 3640's interleaved AAC access units and
H.264's interleaved mode (RFC 6184, `packetization-mode=2`). Today both are refused by name: `LivePlayer` fails on an
SDP with `packetization-mode=2` (docs/decisions/2026-11-10-live-rtsp-h264.md: "for broadcasting and is out"), and
`RtpAudioDepacketizer` turns the sound off at the first non-zero AU index (2026-11-15-live-rtsp-audio.md, point 1).

## What interleaving is

Both spread a stream's units over packets out of order, so that one lost packet takes units far apart rather than a run
of them, and both make the receiver put them back in order before decoding.

- **RFC 3640 (AAC, `mode=AAC-hbr` or `AAC-lbr`).** Each AU header carries an index: the first's an AU-Index, which
  the AAC modes code 0, the others' an AU-Index-delta (the gap to the one before in decoding order, less one; above 0
  only when interleaving, 3.2.1.1). The packet's RTP timestamp is its first access unit's, and each other's is so many
  `constantDuration`s after it as the deltas place it (3.2.3.2, 3.3.6). The SDP gives the bound: `maxDisplacement`, in
  RTP clock ticks, the furthest the newest access unit to come may be past the earliest still missing (3.2.3.3), and
  sometimes `de-interleaveBufferSize`. So a receiver holds at most about `maxDisplacement` of sound (21 ms an access
  unit at 48 kHz).
- **RFC 6184, `packetization-mode=2` (H.264).** NAL units in decoding order get a 16-bit decoding order number (DON).
  Only STAP-B, MTAP16, MTAP24, FU-A and FU-B carry them: no single NAL unit packets, no STAP-A. The first fragment of a
  fragmented NAL unit is an FU-B with its DON; the rest are FU-A. The receiver sorts NAL units by DON (5.5, with DON's
  wrap) and releases them once `sprop-interleaving-depth` more are buffered, or `sprop-init-buf-time` has passed at the
  start (`sprop-deint-buf-req`, `sprop-max-don-diff` bound it). Access units are then the NAL units between picture
  boundaries in DON order, no longer the packets with one RTP timestamp.

## What is at hand

- **No sender.** FFmpeg's RTP muxer has no interleaving for either (its flags are `latm` and `rfc2190`), GStreamer's
  `rtph264pay` and `rtpmp4gpay` have no option for it, and live555 sends neither (#552). Mode 2 was made for broadcast
  (MBMS, 3GPP). No camera known here sends either.
- **No oracle.** FFmpeg's depacketizers don't take mode 2's STAP-B, MTAP and FU-B, nor an RFC 3640 index, as far as is
  known (not read: its LGPL code is not ported here, and only its behaviour is an oracle).
- So the checks would be **a packetizer of the tests' own**, written from the RFCs, that interleaves the existing
  recordings (FFmpeg's AAC and H.264 RTP): the depacketizer must give back the recordings' access units, byte for byte.
  The weakness: one reading of the RFCs on both sides, so a misreading shared by both passes. Hand-made packets for
  the edge cases (DON's wrap, an index delta across a packet, loss inside a group).

## Proposal

1. **Which** (decision 1): RFC 3640's interleaving, H.264's mode 2, both, or neither (and say why in #527).
2. **Where**: in the depacketizers, behind the same interface: units come out in order, later by the bound's latency.
   `LivePlayer` needs no change but dropping the refusals. The reorder window over UDP (50 ms) stays in front.
3. **Loss**: a lost packet's units are missing; the depacketizer releases what it holds once the bound is passed, as
   the reorder window does, and counts the loss as now.
4. **Latency** (decision 2): hold what the SDP's bound says, or a fixed cap below it, so that a sender asking for
   seconds of buffering doesn't delay a live view by that much.
5. **No public API.** A CHANGELOG line when built.

## Decided

Asked with the multiple-choice prompt; the owner took each recommendation (2026-11-17).

1. **Which. Decided: (a), RFC 3640's AAC interleaving only.**
   - **(a) RFC 3640 only** (recommended). The smaller one, and the one an audio sender might use for loss resilience.
     Mode 2 stays refused by name.
   - (b) Both. Mode 2 too: STAP-B, MTAP16 and MTAP24, FU-B, de-interleaving by DON. Larger, made for broadcast, and no
     known camera sends it.
   - (c) Neither: say why in #527 and mark both not planned.
2. **Latency. Decided: (a), the SDP's bound, capped at a quarter of a second.**
   - **(a) Capped** (recommended). Hold what `maxDisplacement` says, never more than 250 ms; past that, what comes
     late is dropped and counted. A live view stays live.
   - (b) The SDP's bound alone. Nothing out of order is lost, but a sender asking for seconds delays the view by
     seconds.

## Since

- Built (2026-11-17), `RtpAudioDepacketizer` and the SDP's `constantDuration` and `maxDisplacement`:
  - Each access unit's time is the packet's RTP timestamp, plus the AU-Index-deltas' places times `constantDuration`
    (3.2.3.2), extended past 32 bits. Units come out in that order, each with its own time, so capture times stay
    right.
  - De-interleaving is on once the SDP gives `maxDisplacement` or a packet has an AU-Index-delta above 0. Without
    either, a stream is passed on as before, with no added latency (a lost packet isn't waited for).
  - A missing unit is given up on once the newest held is further past it than the bound: the maximum displacement's
    own definition (3.2.3.3). The bound is `maxDisplacement`, at most 250 ms (12000 ticks at 48 kHz). Without a
    `constantDuration`, AAC's 1024 samples stand for it.
  - At the start nothing says which unit is due first: one before the first to come may come up to the bound after
    it. So the first is the earliest held once the bound has passed: a start-up delay of at most the bound, once.
    (The first build released the first to come at once, and a test with the earlier unit second caught it.)
  - A unit that comes after it was given up on is dropped and counted damaged. A late packet never gets that far: its
    sequence number is old, and it is dropped as before. Only a sender interleaving further than the cap is reached.
  - A first AU-Index other than 0, which no AAC mode sends (3.3.5, 3.3.6), damages its packet. It used to turn the
    sound off, and with sound only (#553) end the player: neither does now, and that rule's test and mutation entry
    became a test that the sound plays on.
  - Checks: a packetizer of the tests' own interleaves FFmpeg's one-unit-a-packet recording as 2.5 shows, and the
    units come back in order with their times; then a lost packet with the SDP's bound and with the cap, a sender
    interleaving past the cap, the timestamp's wrap, an SDP without the parameters, a fragmented unit among
    interleaved ones, and a non-zero AU-Index. Through `LivePlayer`, interleaved sound alone plays as the decoder's
    chunks in order. Six mutation entries.
  - Not checked: any real sender (none at hand); the tests' packetizer and the depacketizer share one reading of the
    RFC.

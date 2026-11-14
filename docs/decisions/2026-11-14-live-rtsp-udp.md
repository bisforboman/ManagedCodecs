# Live camera viewing, step 4: RTP over UDP (2026-11-14)

A plan, for the owner to decide on (Decisions, below). Nothing here is built yet. Steps 2 and 3 (RTSP with H.264,
then JPEG: docs/decisions/2026-11-10-live-rtsp-h264.md and docs/decisions/2026-11-12-live-rtsp-jpeg.md, built and
released in 0.13.0 and 0.14.0) take RTP only over the RTSP connection itself. A camera or server that won't do that
answers SETUP with 461 Unsupported Transport, and `LivePlayer` gives up: "RTP over UDP isn't supported yet".

## What UDP changes

- **SETUP** asks for `Transport: RTP/AVP;unicast;client_port=N-N+1`. The client binds two UDP ports first: RTP on an
  even port and RTCP on the next (RFC 3550, 11). The answer names the server's ports
  (`server_port=...`), often an `ssrc`, and sometimes a `source` address.
- **The RTSP connection stays open.** It carries the keep-alives (`GET_PARAMETER`, as now), and closing it or sending
  TEARDOWN ends the session. The packets come on the UDP ports instead of the `$` frames.
- **Packets can be lost, duplicated or reordered.** Over TCP they never are (a camera can only drop them before
  sending). The depacketizers already count a sequence-number gap as loss and ignore a packet from behind the
  expected one. Over UDP a packet that arrives late would be thrown away even though it could still be used.
- **Reaching the client:** the server sends to the client's address from the RTSP connection. That works on a LAN. Through
  NAT it works only if the client's ports were opened outward first: FFmpeg's client sends a small "punch" packet from
  each port to the server's for that. A firewall on the client (Windows asks the user) can block it too.
- **Not on every platform:** WebAssembly has no sockets at all (no RTSP there today either). Unity's Mono and Native
  AOT have `UdpClient` and `Socket`.

## Measured (2026-11-14)

- **mediamtx 1.21.3 with `rtspTransports: [udp]`** answers our SETUP for RTP over TCP with
  `461 Unsupported Transport`, so `LivePlayer` fails at once. That makes it a real UDP-only server for the checks.
- **mediamtx's answer** to a UDP SETUP is `RTP/AVP;unicast;client_port=...;server_port=8000-8001;ssrc=D93F56E4`. One
  port pair on the server serves every session.
- **A reader that sends no RTCP** (no receiver reports) and only `GET_PARAMETER` every 20 s kept getting packets from
  mediamtx for the 75 s tried, about 85 a second, with the session timeout at 55 s. So mediamtx doesn't need receiver
  reports. Cameras weren't in reach to try.
- **FFmpeg 6.1's client** asks for UDP first (`RTP/AVP/UDP;unicast;client_port=...`). Ours asks for TCP first.
- **The loopback interface** loses and reorders nothing, so the tests have to inject loss, duplicates and reordering
  themselves (Checks, below).

## Proposal

1. **When to use UDP** (decision 1): RTP over the RTSP connection first, as now. On 461 the same connection sends SETUP
   again asking for UDP. `RtspSource` needs no new member.
2. **The ports**: an even ephemeral port and the next one, bound on the address the RTSP connection uses, and closed
   with the session. If the pair can't be bound (the next port taken), it tries again a few times.
3. **NAT**: after PLAY, one small packet from each port to the server's ports, as FFmpeg sends, so a NAT in between
   lets the answers in. It is harmless where there is no NAT.
4. **Which packets count**: those from the server's address (the `source` in the answer, else the RTSP connection's).
   If the answer gives an `ssrc`, only packets with that SSRC. Anything else is ignored.
5. **Reordering** (decision 2): a small window in front of the depacketizers. A packet ahead of the next expected
   sequence number is held. The held packets go on in order once the missing one comes, or once the window has
   passed, and then the missing one counts as lost (`LostPackets`, as now). A duplicate, or one later than the window,
   is ignored.
6. **RTCP** (decision 3): what comes on the RTCP port is read and ignored, as on the TCP channel now.
7. **The rest is as for TCP**: the same depacketizers, decoding, `MaxLag`, stall watchdog (5 s with no RTP), backoff
   and statistics. A stall over UDP also covers a firewall that never lets the first packet in. The error then says
   UDP was in use, since that is the likeliest cause.
8. **Not in step 4**:
   - Multicast (`RTP/AVP;multicast`), which few viewers need.
   - RTSPS and SRTP.
   - RTSP over HTTP tunnelling.
   - Choosing UDP over TCP when both work (decision 1's (b) would add it).

## Checks

- **`RtspTestServer` grows UDP.** It answers a TCP SETUP with 461 when told to, takes `client_port`, and sends
  the recorded packets (FFmpeg's H.264 and JPEG recordings, GStreamer's JPEG ones) from its own UDP ports. A test can
  have it drop, duplicate, swap or delay chosen packets.
- **Lockstep, as for TCP:** every frame shown over UDP is the decoder's for the units so far, H.264 and JPEG.
- **Reordering:** packets swapped within the window give the same frames as in order, with `LostPackets` 0. A packet
  later than the window counts as lost and is concealed (H.264) or drops its frame (JPEG). Duplicates change nothing.
- **Rules:** packets from another address or SSRC are ignored, the punch packets reach the server's ports, the
  keep-alives still go out, and both ports are closed with the session.
- **Mutation entries** for the rules no recording reaches: the reorder window, the SSRC and address filter, and the
  fallback on 461.
- **Nightly**: `tools/rtsp-nightly.py` gains a run against mediamtx with `rtspTransports: [udp]` (the 461 above), so
  the fallback and UDP reception are checked against a real server.
- **By hand**: GStreamer's RTSP server offers UDP too (`tools/gst-rtsp-server.py`). An IP camera if one is in reach,
  listed in docs/conformance.md.

## Decisions

For the owner, with a recommendation each.

1. **When UDP is used**:
   - **(a) Only after a 461 to RTP over TCP** (recommended). No API change. TCP stays the default, since it works
     through NAT and firewalls and loses nothing, and UDP serves the servers that refuse it.
   - (b) (a) plus a public `RtspSource.Transport` (`Automatic`, `Tcp`, `Udp`), for a viewer that wants UDP's lower
     latency on a LAN, or TCP only. It is new public API, and nothing so far needs it; it can come later.
   - (c) UDP first, then TCP, as FFmpeg does. A firewall that drops UDP then costs a stall timeout (5 s) on every
     connection before TCP is tried.
2. **The reorder window**:
   - **(a) 50 ms, by arrival time** (recommended). Short enough not to be seen, and longer than the reordering a LAN or
     a Wi-Fi hop shows. A stall in the window can't hold a frame back for longer.
   - (b) None: a packet out of order is lost, as over TCP now. Simpler, but over Wi-Fi it would turn reordering into
     concealed damage.
   - (c) A count of packets (FFmpeg's approach). How long it holds a packet back then depends on the stream's
     bitrate: a 4K stream gets through the count far faster than a substream.
3. **RTCP receiver reports**:
   - **(a) None at first** (recommended). mediamtx didn't need them in 75 s, and the RTSP keep-alives keep the
     session. If a camera times out a reader that sends none, add them then, from what that camera shows.
   - (b) Send them every 5 s from the start (RFC 3550, 6.4.2: SSRC, loss and jitter): about a hundred lines more, for
     cameras none of which has been seen to need them.

A consumer-visible change (a server that answered 461 now plays), so a CHANGELOG line under "Unreleased" when it is
built. With 1 (a), no public API changes.

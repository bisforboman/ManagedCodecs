# Live camera viewing: RTP to a multicast group (2026-11-16)

Issue #527 lists multicast (`RTP/AVP;multicast`) among what the RTSP client doesn't do: a server that refused RTP both
over the RTSP connection and over UDP ended the player. Built on the owner's choice from #527's list (2026-11-15). No
public API, so nothing for the owner to decide beyond the choice.

## What multicast changes

- **One stream for every viewer:** the camera sends each track once, to a group address (224.0.0.0/4), and every
  viewer on the network joins it. A camera set to multicast only answers 461 to RTP over the connection and to RTP over
  UDP to one reader, and names the group in its answer to a SETUP asking for `RTP/AVP;multicast` (RFC 2326, 12.39:
  `destination`, `port`, `ttl`).
- **The ports are the group's**, shared with every other viewer on the machine, not ones the reader picks: they are
  bound with address reuse, to the group's address (Linux, macOS) or to any (Windows, which can't bind a group).
- **The group's packets** come from the camera, but from whichever of its addresses faces the network. Only the SSRC
  the answer names (and a `source`, if it names one) tells this camera's stream from another sender's to the group.
- **Receiver reports** go to the group's RTCP port, as every receiver's do (RFC 3550, 6.2): the camera, and every
  other viewer, hears them.

## Measured (2026-11-16)

mediamtx 1.21.3 with `rtspTransports: [multicast]`: 461 to RTP over the connection and over UDP, then to
`RTP/AVP;multicast` the answer `RTP/AVP;multicast;destination=224.1.0.1;port=8002-8003;ttl=127;ssrc=A6AA2A77`, and the
packets came from the machine's address (not 127.0.0.1) to the group. The cloud sandbox's loopback interface carries
multicast.

## Built

- **The fallback:** after the second 461, a third SETUP asks for `RTP/AVP;multicast`; a 461 to that is final. Asked
  only then, so a camera that offers unicast is never moved to multicast.
- **`RtpUdpPorts.Join`**: the group's two ports, joined on the RTSP connection's interface (and the default one too for a
  connection to this machine), the answer's `ttl` for what is sent.
- **The audio** asks for multicast too when the video got it, and joins its own group.
- **What is the same as over UDP:** the reorder window, the depacketizers, the stall watchdog, the receiver reports'
  counts and the sender reports' capture times.
- **Not in it:** choosing multicast when unicast works, SSM (a source-specific join), and IPv6 groups (the code joins
  them, untried).

## Checks

docs/conformance.md, Multicast: `LiveRtspMulticastTests` against `RtspTestServer` set to multicast only, and mediamtx by
hand. Not tried: a camera's multicast, and a network with IGMP snooping or routers between.

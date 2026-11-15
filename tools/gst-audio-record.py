#!/usr/bin/env python3
"""Makes the committed audio over RTP recordings in samples/gstreamer/ (issue #527): GStreamer's packetizers on what
its own encoders give, for what FFmpeg's packetizer never sends (docs/decisions/2026-11-15-live-rtsp-audio.md,
Measured: FFmpeg puts several AAC frames in a packet and never fragments one).

  aac-single.rtp       rtpmp4gpay (RFC 3640, AAC-hbr): one access unit a packet
  aac-fragmented.rtp   the same with mtu=200 on about 256 kbit/s of noise: every access unit over several packets
  alaw.rtp             rtppcmapay (RFC 3551, payload type 8): the encoder's 1024 samples a packet
  mulaw.rtp            rtppcmupay (payload type 0)

Each with <name>.sdp, written from the packetizer's caps as an RTSP server would describe them, and <name>.frames, what
went into the packetizer: the encoder's access units, each after its 16-bit length (AAC), or its bytes (G.711). The
depacketizer must give exactly those back (RtpAudioDepacketizerTests). The sound is pink noise, so a run makes other
recordings than the committed ones.

Needs GStreamer 1.x with its Python bindings and the libav plugin (Ubuntu: python3-gi gstreamer1.0-plugins-base
gstreamer1.0-plugins-good gstreamer1.0-libav), run by the Python they were built for:
  /usr/bin/python3.12 tools/gst-audio-record.py
"""

import os
import struct
import sys

import gi
gi.require_version("Gst", "1.0")
from gi.repository import Gst

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "samples", "gstreamer")
AAC = "audio/x-raw,rate=48000,channels=2 ! avenc_aac bitrate={bitrate} ! tee name=t ! queue ! rtpmp4gpay pt=97 {pay}"
STREAMS = {
    "aac-single": dict(buffers=40, chain=AAC.format(bitrate=128000, pay="")),
    "aac-fragmented": dict(buffers=40, chain=AAC.format(bitrate=256000, pay="mtu=200")),
    "alaw": dict(buffers=10, chain="audio/x-raw,rate=8000,channels=1 ! alawenc ! tee name=t ! queue ! rtppcmapay"),
    "mulaw": dict(buffers=10, chain="audio/x-raw,rate=8000,channels=1 ! mulawenc ! tee name=t ! queue ! rtppcmupay"),
}


def pull(sink):
    """Every buffer an appsink got, and the caps of the last."""
    buffers, caps = [], None
    while True:
        sample = sink.emit("try-pull-sample", 0)
        if sample is None:
            return buffers, caps
        caps = sample.get_caps()
        buffer = sample.get_buffer()
        buffers.append(buffer.extract_dup(0, buffer.get_size()))


def sdp(caps):
    """The media section an RTSP server would send for the packetizer's caps (RFC 4566; RFC 3640, 4.1)."""
    s = caps.get_structure(0)
    payload = s.get_value("payload")
    name = s.get_value("encoding-name")
    lines = ["v=0", "o=- 0 0 IN IP4 127.0.0.1", "s=-", "t=0 0", f"m=audio 0 RTP/AVP {payload}"]
    if payload >= 96:
        channels = s.get_value("encoding-params")
        lines.append(f"a=rtpmap:{payload} {name}/{s.get_value('clock-rate')}" + (f"/{channels}" if channels else ""))
        keys = ["streamtype", "profile-level-id", "mode", "config", "sizelength", "indexlength", "indexdeltalength"]
        lines.append(f"a=fmtp:{payload} " + ";".join(f"{k}={s.get_value(k)}" for k in keys if s.has_field(k)))
    return "\r\n".join(lines) + "\r\n"


def main():
    Gst.init(None)
    os.makedirs(OUT, exist_ok=True)
    for name, how in STREAMS.items():
        pipeline = Gst.parse_launch(
            f"audiotestsrc wave=pink-noise samplesperbuffer=1024 num-buffers={how['buffers']} ! {how['chain']} "
            "! appsink name=packets sync=false wait-on-eos=false t. ! queue ! appsink name=frames sync=false wait-on-eos=false")
        pipeline.set_state(Gst.State.PLAYING)
        message = pipeline.get_bus().timed_pop_filtered(30 * Gst.SECOND, Gst.MessageType.EOS | Gst.MessageType.ERROR)
        if message is None or message.type != Gst.MessageType.EOS:
            pipeline.set_state(Gst.State.NULL)
            sys.exit(f"{name}: the pipeline failed: {message.parse_error()[0].message if message else 'no end in 30 s'}")
        packets, caps = pull(pipeline.get_by_name("packets"))
        frames, _ = pull(pipeline.get_by_name("frames"))
        pipeline.set_state(Gst.State.NULL)
        for previous, packet in zip(packets, packets[1:]):
            if struct.unpack(">H", packet[2:4])[0] != (struct.unpack(">H", previous[2:4])[0] + 1) & 0xFFFF:
                sys.exit(f"{name}: a packet is missing or out of order")
        aac = name.startswith("aac")
        with open(os.path.join(OUT, name + ".rtp"), "wb") as f:
            for packet in packets:
                f.write(struct.pack(">H", len(packet)) + packet)
        with open(os.path.join(OUT, name + ".frames"), "wb") as f:
            for frame in frames:
                f.write((struct.pack(">H", len(frame)) if aac else b"") + frame)
        with open(os.path.join(OUT, name + ".sdp"), "w", newline="") as f:
            f.write(sdp(caps))
        print(f"{name}: {len(packets)} packets for {len(frames)} {'access units' if aac else 'buffers'}, "
              f"{max(map(len, packets))} bytes at most; {sdp(caps).splitlines()[-1]}")


if __name__ == "__main__":
    main()

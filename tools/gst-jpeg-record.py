#!/usr/bin/env python3
"""Makes the committed JPEG over RTP recordings in samples/gstreamer/ (docs/decisions/2026-11-12-live-rtsp-jpeg.md,
decision 2): GStreamer's RFC 2435 packetizer (rtpjpegpay) on JPEGs written by Pillow, for the RTP types FFmpeg's
packetizer never sends (FFmpeg's, type 1, are made by make-samples.sh). Each stream is 5 frames of 320x240:

  jpeg-422.rtp           type 0: 4:2:2
  jpeg-420-restart.rtp   type 65: 4:2:0 with a restart marker every MCU row
  jpeg-422-restart.rtp   type 64: 4:2:2 with a restart marker every MCU row

beside the JPEGs that went in (<name>/00.jpg ...), which the frames rebuilt from RTP must decode to. Pillow's JPEGs use
T.81's standard Huffman tables, as RFC 2435 requires, and quality 50 (in-band tables, Q 255).

Needs ffmpeg, Pillow 11 or later (restart markers) and GStreamer 1.x with its Python bindings (gi, run with the
interpreter given by --gst-python, /usr/bin/python3.12 on Ubuntu 24.04). Usage, from the repository root:
  tools/gst-jpeg-record.py
"""

import argparse
import os
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import threading

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "samples", "gstreamer")
STREAMS = {
    "jpeg-422": dict(subsampling=1, restart=False),
    "jpeg-420-restart": dict(subsampling=2, restart=True),
    "jpeg-422-restart": dict(subsampling=1, restart=True),
}
PIPELINE = """
import sys, gi
gi.require_version('Gst', '1.0')
from gi.repository import Gst
Gst.init(None)
p = Gst.parse_launch(sys.argv[1])
p.set_state(Gst.State.PLAYING)
m = p.get_bus().timed_pop_filtered(30 * Gst.SECOND, Gst.MessageType.EOS | Gst.MessageType.ERROR)
p.set_state(Gst.State.NULL)
sys.exit(0 if m is not None and m.type == Gst.MessageType.EOS else 1)
"""


def record(port, packets, ready):
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", port))
    sock.settimeout(30)
    ready.set()
    while True:
        try:
            data, _ = sock.recvfrom(65536)
        except socket.timeout:
            return
        packets.append(data)
        sock.settimeout(1)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--gst-python", default="/usr/bin/python3.12")
    parser.add_argument("--port", type=int, default=5096)
    args = parser.parse_args()
    os.makedirs(OUT, exist_ok=True)
    with tempfile.TemporaryDirectory() as work:
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25", "-frames:v", "5",
                        os.path.join(work, "%02d.png")], check=True)
        for name, how in STREAMS.items():
            frames = os.path.join(OUT, name)
            shutil.rmtree(frames, ignore_errors=True)
            os.makedirs(frames)
            for i in range(5):
                image = Image.open(os.path.join(work, f"{i + 1:02d}.png")).convert("RGB")
                options = dict(quality=50, subsampling=how["subsampling"])
                if how["restart"]:
                    options["restart_marker_rows"] = 1
                image.save(os.path.join(frames, f"{i:02d}.jpg"), **options)

            packets = []
            ready = threading.Event()
            receiver = threading.Thread(target=record, args=(args.port, packets, ready))
            receiver.start()
            ready.wait()
            pipeline = (f"multifilesrc location={frames}/%02d.jpg index=0 stop-index=4 caps=image/jpeg,framerate=25/1 "
                        f"! jpegparse ! rtpjpegpay ! udpsink host=127.0.0.1 port={args.port}")
            subprocess.run([args.gst_python, "-c", PIPELINE, pipeline], check=True)
            receiver.join()
            for previous, packet in zip(packets, packets[1:]):
                if struct.unpack(">H", packet[2:4])[0] != (struct.unpack(">H", previous[2:4])[0] + 1) & 0xFFFF:
                    sys.exit(f"{name}: a packet is missing or out of order")
            with open(os.path.join(OUT, name + ".rtp"), "wb") as f:
                for packet in packets:
                    f.write(struct.pack(">H", len(packet)) + packet)
            types = sorted({p[12 + 4 * (p[0] & 15) + 4] for p in packets})
            print(f"{name}.rtp: {len(packets)} packets, RTP/JPEG types {types}")


if __name__ == "__main__":
    main()

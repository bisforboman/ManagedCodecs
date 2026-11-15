#!/usr/bin/env python3
"""An RTSP server for trying LivePlayer by hand: GStreamer's (gst-rtsp-server), a live SMPTE test pattern encoded by
x264 at rtsp://127.0.0.1:8554/cam, with no authentication, Basic or Digest (user admin, password s3cret). With JPEG
set (the environment variable), JPEG over RTP instead (jpegenc, rtpjpegpay: RFC 2435, type 1 for 4:2:0 or 0 for
JPEG=422). With AUDIO set, a second track with a 440 Hz tone: aac (avenc_aac, rtpmp4gpay: RFC 3640, one access unit a
packet), aac-fragmented (the same at 256 kbit/s of noise with mtu=200: every access unit over several packets), alaw or
mulaw (rtppcmapay, rtppcmupay: the static payload types 8 and 0), or latm (rtpmp4apay, RFC 6416, which LivePlayer
names and doesn't play).

    python3 tools/gst-rtsp-server.py [none|basic|digest] [width height]
    JPEG=420 python3 tools/gst-rtsp-server.py digest
    AUDIO=aac python3 tools/gst-rtsp-server.py

Needs GStreamer's Python bindings and plugins (Ubuntu: gir1.2-gst-rtsp-server-1.0 python3-gi
gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-plugins-ugly, and gstreamer1.0-libav for AAC), run by
the Python they were built for
(/usr/bin/python3.12 on Ubuntu 24.04). docs/conformance.md, H.264 over RTSP, has what it showed.
"""
import os
import sys
import gi
gi.require_version("Gst", "1.0")
gi.require_version("GstRtspServer", "1.0")
gi.require_version("GstRtsp", "1.0")
from gi.repository import Gst, GstRtsp, GstRtspServer, GLib

Gst.init(None)
mode = sys.argv[1] if len(sys.argv) > 1 else "none"
width, height = (sys.argv[2], sys.argv[3]) if len(sys.argv) > 3 else ("640", "480")
server = GstRtspServer.RTSPServer()
server.set_service("8554")
factory = GstRtspServer.RTSPMediaFactory()
jpeg = os.environ.get("JPEG")
encoder = (f"videoconvert ! video/x-raw,format={'Y42B' if jpeg == '422' else 'I420'} ! jpegenc ! rtpjpegpay name=pay0"
           if jpeg else
           "videoconvert ! x264enc tune=zerolatency speed-preset=veryfast key-int-max=50 ! rtph264pay name=pay0 pt=96 config-interval=1")
AAC = "audio/x-raw,rate=48000,channels=2 ! avenc_aac bitrate={} ! {} name=pay1 pt=97 {}"
SOUNDS = {
    "aac": ("sine", AAC.format(128000, "rtpmp4gpay", "")),
    "aac-fragmented": ("pink-noise", AAC.format(256000, "rtpmp4gpay", "mtu=200")),
    "latm": ("sine", AAC.format(128000, "rtpmp4apay", "")),
    "alaw": ("sine", "audio/x-raw,rate=8000,channels=1 ! alawenc ! rtppcmapay name=pay1"),
    "mulaw": ("sine", "audio/x-raw,rate=8000,channels=1 ! mulawenc ! rtppcmupay name=pay1"),
}
sound = os.environ.get("AUDIO")
if sound and sound not in SOUNDS:
    sys.exit(f"AUDIO is one of {', '.join(SOUNDS)}.")
audio = f" audiotestsrc is-live=true wave={SOUNDS[sound][0]} freq=440 ! {SOUNDS[sound][1]}" if sound else ""
factory.set_launch(
    f"( videotestsrc is-live=true pattern=smpte ! video/x-raw,width={width},height={height},framerate=25/1 ! {encoder}{audio} )")
factory.set_shared(True)
if mode != "none":
    auth = GstRtspServer.RTSPAuth()
    token = GstRtspServer.RTSPToken()
    token.set_string("media.factory.role", "user")
    if mode == "basic":
        auth.add_basic(GstRtspServer.RTSPAuth.make_basic("admin", "s3cret"), token)
        auth.set_supported_methods(GstRtsp.RTSPAuthMethod.BASIC)
    else:
        auth.add_digest("admin", "s3cret", token)
        auth.set_supported_methods(GstRtsp.RTSPAuthMethod.DIGEST)
    server.set_auth(auth)
    permissions = GstRtspServer.RTSPPermissions()
    permissions.add_role("user")
    permissions.add_permission_for_role("user", "media.factory.access", True)
    permissions.add_permission_for_role("user", "media.factory.construct", True)
    factory.set_permissions(permissions)
server.get_mount_points().add_factory("/cam", factory)
server.attach(None)
print(f"rtsp://127.0.0.1:8554/cam ({mode}{', JPEG ' + jpeg if jpeg else ''}{', sound ' + sound if sound else ''})", flush=True)
GLib.MainLoop().run()

#!/usr/bin/env python3
"""An RTSP server for trying LivePlayer by hand: GStreamer's (gst-rtsp-server), a live SMPTE test pattern encoded by
x264 at rtsp://127.0.0.1:8554/cam, with no authentication, Basic or Digest (user admin, password s3cret).

    python3 tools/gst-rtsp-server.py [none|basic|digest] [width height]

Needs GStreamer's Python bindings and plugins (Ubuntu: gir1.2-gst-rtsp-server-1.0 python3-gi
gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-plugins-ugly), run by the Python they were built for
(/usr/bin/python3.12 on Ubuntu 24.04). docs/conformance.md, H.264 over RTSP, has what it showed.
"""
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
factory.set_launch(
    f"( videotestsrc is-live=true pattern=smpte ! video/x-raw,width={width},height={height},framerate=25/1 "
    "! videoconvert ! x264enc tune=zerolatency speed-preset=veryfast key-int-max=50 "
    "! rtph264pay name=pay0 pt=96 config-interval=1 )")
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
print(f"rtsp://127.0.0.1:8554/cam ({mode})", flush=True)
GLib.MainLoop().run()

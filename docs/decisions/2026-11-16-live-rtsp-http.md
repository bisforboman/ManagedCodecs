# Live camera viewing: RTSP over HTTP (2026-11-16)

Issue #527 lists RTSP over HTTP tunnelling among what the RTSP client doesn't do. Built on the owner's choice from #527's
list (2026-11-15), with the one decision below.

## What it is

- **Apple's tunnelling** (QuickTime Streaming Server's, documented by Apple; no RFC): for a viewer behind a firewall or proxy that passes only HTTP. Two HTTP connections to the server, joined by a
  cookie of the client's (`x-sessioncookie`):
  - a GET (`Accept: application/x-rtsp-tunnelled`), whose answer, 200 and then no end, carries everything the server
    sends: the RTSP responses and the RTP interleaved among them, as on an RTSP connection;
  - a POST (`Content-Type: application/x-rtsp-tunnelled`, a `Content-Length` of 32767 that never comes), whose body
    carries the RTSP requests in base64. The server never answers it.
- **Who offers it:** many cameras (from memory, not checked: Axis, Hikvision, Dahua and others, often on port 80 beside
  their web page), live555MediaServer (port 80, 8000 or 8080), and GStreamer's RTSP server (on its RTSP port). FFmpeg's
  client sends it (`-rtsp_transport http`), as live555's does.
- **RTP** comes only inside the tunnel. A server that refuses RTP over the connection (461) wants UDP beside it, which
  is what the tunnel is there to avoid.

## Decided

Asked with the multiple-choice prompt; the owner took the recommendation (2026-11-16).

1. **How an application asks for it. Decided: (a), `RtspSource.HttpTunnel`.**
   - **(a) `RtspSource.HttpTunnel`** (recommended): a bool, false by default, as `Audio`. The URL stays `rtsp://`; its
     port is the camera's HTTP port, 80 when it gives none. Nothing changes for existing code.
   - (b) An `http://` or `https://` URL to `RtspSource`: no new member, but URLs that look like `MjpegHttpSource`'s.
   - (c) Automatic: plain RTSP first, the tunnel on port 80 when that can't connect. A second timeout for every camera
     out of reach, and a port guessed.
   - (d) Leave it out.

## Built

- **`RtspSource.HttpTunnel`** (public, Playback). The port: the URL's, else 80, or 443 for `rtsps://`, the tunnel then
  over HTTPS (both connections, the certificate checked or pinned as for RTSPS).
- **`HttpTunnel`** (internal): a stream under `RtspConnection` that reads the GET's answer and writes each write to the
  POST in base64 of its own, padded, as FFmpeg's and live555's clients write theirs: an RTSP request, or an interleaved
  RTCP receiver report, whole. So the session, Digest and keep-alives are what they are on an RTSP connection.
- **No receiver reports through the tunnel**, an exception to step 7's decision 1 (docs/decisions/2026-11-15-live-rtcp.md,
  "always"). Measured against live555MediaServer (LIVE555 2026.11.08) on its tunnel, a 640x360 session held for 90 s:
  - with them, interleaved and in base64 in the POST like the requests, the session ended at 65 s, live555's time for
    a reader it hears nothing from: 2626 frames, then 5 s of nothing and a reconnect. It seems to take the `$` frame
    for a broken request and lose the keep-alives after it (its source wasn't read to confirm);
  - without them, the keep-alives alone kept it: 2300 frames in 106 s, all the clip's, no reconnect.

  Over a plain RTSP connection live555 takes the same reports (docs/conformance.md, live555MediaServer). A camera
  that needs reports to keep a tunnelled reader, and doesn't count keep-alives, would drop this one; none such has
  been seen.
- **Answers to the GET:** 401, 403, 404, 405 and 501 are final (the same on the next try), any other failure is tried
  again. A 461 to SETUP inside the tunnel is final too: no UDP is tried beside it.
- **`mcdec --live --http-tunnel`**.
- **Not in it:** HTTP authentication of the GET and POST themselves (the RTSP requests inside authenticate as ever),
  and an HTTP proxy between (`CONNECT`, or a proxy's absolute URLs).

## Checks

- **`LiveRtspHttpTests`**, against `RtspTestServer` set to take only tunnelled connections:
  - in lockstep, every H.264 frame through the tunnel is `H264Decoder`'s for the units so far, with Digest inside;
  - a GET and a POST, of the stream's path, with one cookie of 22 letters and digits, and their content types;
  - the keep-alive (`GET_PARAMETER`) comes through the POST at half the session's timeout, and no receiver report;
  - over HTTPS with a pinned certificate;
  - a GET answered 404 or 401 fails, 503 is tried again, and a 461 to SETUP fails with no UDP tried;
  - the default ports and the path, with a query.
- **By hand (2026-11-16):**
  - GStreamer 1.24's RTSP server (tools/gst-rtsp-server.py, Digest): its log shows the GET and the POST joined; 100
    frames, none lost or damaged.
  - live555MediaServer (LIVE555 2026.11.08, tools/live555-check.py's build) on port 80: 200 frames, all the clip's,
    with the port given and without it (80 by default); and the 90 s session above.
- **Not tried:** a camera's tunnel, and an HTTP proxy between.

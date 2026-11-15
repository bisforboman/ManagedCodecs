#!/usr/bin/env python3
"""Serves JPEG files as MJPEG over HTTP (multipart/x-mixed-replace), as a camera does, in a loop at a steady rate: a
stream for the live samples (mclive, mclive-wpf, mcplay) where there is no camera and no FFmpeg, such as the Windows
runner of .github/workflows/windows.yml. Python's standard library only. Every path gives the stream.

    tools/mjpeg-http-server.py samples/gstreamer/jpeg-422 --port 8090 --fps 25
"""
import argparse
import http.server
import os
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("folder", help="a folder of .jpg files, served in the order of their names")
    parser.add_argument("--port", type=int, default=8090)
    parser.add_argument("--fps", type=float, default=25)
    args = parser.parse_args()

    frames = []
    for name in sorted(os.listdir(args.folder)):
        if name.lower().endswith((".jpg", ".jpeg")):
            with open(os.path.join(args.folder, name), "rb") as f:
                frames.append(f.read())
    if not frames:
        parser.error(f"no .jpg files in {args.folder}")

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            self.send_response(200)
            self.send_header("Content-Type", "multipart/x-mixed-replace; boundary=frame")
            self.send_header("Cache-Control", "no-cache")
            self.end_headers()
            start = time.monotonic()
            sent = 0
            try:
                while True:
                    jpeg = frames[sent % len(frames)]
                    self.wfile.write(b"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: %d\r\n\r\n" % len(jpeg))
                    self.wfile.write(jpeg)
                    self.wfile.write(b"\r\n")
                    self.wfile.flush()
                    sent += 1
                    # By the clock from the first frame, so that a slow write doesn't lower the rate for good.
                    time.sleep(max(0.0, start + sent / args.fps - time.monotonic()))
            except (BrokenPipeError, ConnectionError):
                pass  # the client went away

        def log_message(self, format, *arguments):
            pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    server.daemon_threads = True
    print(f"{len(frames)} JPEGs from {args.folder} at {args.fps:g} fps on http://127.0.0.1:{args.port}/", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()

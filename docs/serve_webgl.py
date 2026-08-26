#!/usr/bin/env python3
"""
Serve a Unity WebGL build locally, WITH the compression headers the loader needs.

Why this exists: `python3 -m http.server` serves Unity's compressed build files
as opaque bytes, so the loader gets a Brotli/Gzip blob it cannot parse and fails
with

    Unable to parse Build/<name>.framework.js.br!
    ... verify that web server is sending .br files with HTTP Response Header
    "Content-Encoding: br"

That is not a broken build — it is a server that never announced the encoding.
This one sets Content-Encoding (br/gzip) and the right Content-Type for Unity's
.wasm/.js/.data payloads, which is all the stock server was missing.

Usage (from anywhere):
    python3 docs/serve_webgl.py "Builds/WebGL/<campaign-id>"
    python3 docs/serve_webgl.py "Builds/WebGL/<campaign-id>" --port 8080
then open http://localhost:8000

Browser note: Firefox only accepts Brotli over HTTPS, so a .br build served over
plain HTTP will fail there however correct the headers are — use Chrome/Edge for
local testing, or build with Gzip, or tick Decompression Fallback in
Player Settings → WebGL → Publishing Settings (which makes the loader decompress
in JS and work on any server, at a small startup cost).
"""

import argparse
import functools
import http.server
import os
import socketserver
import sys

# Unity emits <name>.<ext>.<compression>; the encoding is the LAST suffix.
ENCODINGS = {".br": "br", ".gz": "gzip"}

# Content types for the payload extension underneath the compression suffix.
CONTENT_TYPES = {
    ".wasm": "application/wasm",
    ".js": "application/javascript",
    ".json": "application/json",
    ".data": "application/octet-stream",
    ".symbols": "application/octet-stream",
}


class UnityWebGLHandler(http.server.SimpleHTTPRequestHandler):
    """SimpleHTTPRequestHandler that announces Unity's compression."""

    def end_headers(self):
        # Content-Type is handled by guess_type below — setting it here too
        # would emit the header twice.
        ext = os.path.splitext(self.translate_path(self.path))[1].lower()
        encoding = ENCODINGS.get(ext)
        if encoding:
            self.send_header("Content-Encoding", encoding)
        # Compressed builds are cached aggressively otherwise, which hides the
        # next rebuild behind a stale payload.
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    def guess_type(self, path):
        stem, ext = os.path.splitext(path)
        if ext.lower() in ENCODINGS:
            inner = os.path.splitext(stem)[1].lower()
            if inner in CONTENT_TYPES:
                return CONTENT_TYPES[inner]
        return super().guess_type(path)

    def log_message(self, fmt, *args):
        sys.stderr.write("  %s\n" % (fmt % args))


def main():
    ap = argparse.ArgumentParser(description="Serve a Unity WebGL build with compression headers.")
    ap.add_argument("directory", help="the build folder (the one containing index.html)")
    ap.add_argument("--port", type=int, default=8000)
    args = ap.parse_args()

    directory = os.path.abspath(args.directory)
    if not os.path.isdir(directory):
        sys.exit(f"Not a directory: {directory}\n"
                 f"(Paths printed by the ship tool are relative to the PROJECT ROOT.)")
    if not os.path.isfile(os.path.join(directory, "index.html")):
        sys.exit(f"No index.html in {directory} — that is not a Unity WebGL build folder.")

    handler = functools.partial(UnityWebGLHandler, directory=directory)
    socketserver.TCPServer.allow_reuse_address = True
    with socketserver.TCPServer(("", args.port), handler) as httpd:
        print(f"Serving {directory}")
        print(f"  http://localhost:{args.port}    (Ctrl+C to stop)")
        print("  sending Content-Encoding for .br / .gz — Firefox still needs HTTPS for Brotli")
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print("\nstopped")


if __name__ == "__main__":
    main()

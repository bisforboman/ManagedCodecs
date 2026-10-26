#!/usr/bin/env python3
"""Rewrites the encoder name FFmpeg's AAC encoder writes into its first frame's fill element ("Lavc60.31.102", or
whatever version), in place, so the stream reads as another encoder's. FFmpeg's encoder aligns the name to a byte after
the element's 4-bit extension type; FAAC's comes 13 bits after it, where FFmpeg's decoder looks for "libfaac X.Y":
the name written one byte on ("L" then "libfaac 1.28") is read there.

Usage: tools/aac-encoder-name.py <file.aac or .mp4> [name]   (default name: libfaac 1.28)
"""

import re
import sys


def main():
    path = sys.argv[1]
    name = (sys.argv[2] if len(sys.argv) > 2 else "libfaac 1.28").encode("ascii")
    data = open(path, "rb").read()
    found = re.search(rb"Lavc[0-9.]+", data)
    if not found:
        sys.exit(f"{path}: no Lavc encoder name")
    old = found.group(0)
    new = (b"L" + name).ljust(len(old))
    if len(new) > len(old):
        sys.exit(f"{path}: {name!r} does not fit in place of {old!r}")
    with open(path, "wb") as out:
        out.write(data[:found.start()] + new + data[found.end():])
    print(f"{path}: {old.decode()} -> {new.decode()!r}")


if __name__ == "__main__":
    main()

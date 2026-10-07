"""Generates src/MadModStudio.App/Assets/MadModStudio.ico (PNG-in-ICO, 16..256 px) without external libraries.

Design: rounded dark tile, orange (theme accent) "M" monogram with a blue (accent 2) underline.
Run: python3 build/make_icon.py
"""
import math
import struct
import sys
import zlib
from pathlib import Path

BG = (0x1A, 0x1D, 0x23)
ACCENT = (0xE8, 0x74, 0x3B)
ACCENT2 = (0x3B, 0xA7, 0xE8)
SIZES = [16, 24, 32, 48, 64, 128, 256]
SS = 4  # supersampling per axis


def seg_dist(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
    cx, cy = ax + t * dx, ay + t * dy
    return math.hypot(px - cx, py - cy)


def sample(u, v):
    """Colour (r,g,b,a) at normalised coordinates u,v in [0,1]."""
    # Rounded square tile.
    r = 0.2
    qx, qy = abs(u - 0.5) - (0.5 - r), abs(v - 0.5) - (0.5 - r)
    outside = math.hypot(max(qx, 0), max(qy, 0)) + min(max(qx, qy), 0) - r
    if outside > 0:
        return (0, 0, 0, 0)
    # "M" as four thick strokes.
    w = 0.075
    pts = [(0.24, 0.72), (0.24, 0.26), (0.5, 0.56), (0.76, 0.26), (0.76, 0.72)]
    if any(seg_dist(u, v, *pts[i], *pts[i + 1]) < w for i in range(4)):
        return ACCENT + (255,)
    # Underline.
    if 0.80 <= v <= 0.86 and 0.24 <= u <= 0.76:
        return ACCENT2 + (255,)
    return BG + (255,)


def render(size):
    rows = []
    for y in range(size):
        row = bytearray([0])  # PNG filter: none
        for x in range(size):
            acc = [0, 0, 0, 0]
            for sy in range(SS):
                for sx in range(SS):
                    c = sample((x + (sx + 0.5) / SS) / size, (y + (sy + 0.5) / SS) / size)
                    a = c[3]
                    acc[0] += c[0] * a
                    acc[1] += c[1] * a
                    acc[2] += c[2] * a
                    acc[3] += a
            n = SS * SS
            a = acc[3] / n
            if acc[3] == 0:
                row += bytes((0, 0, 0, 0))
            else:
                row += bytes((round(acc[0] / acc[3]), round(acc[1] / acc[3]), round(acc[2] / acc[3]), round(a)))
        rows.append(bytes(row))
    raw = b"".join(rows)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def main(out):
    images = [render(s) for s in SIZES]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    for size, png in zip(SIZES, images):
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset)
        offset += len(png)
    Path(out).write_bytes(header + entries + b"".join(images))
    print(f"wrote {out} ({', '.join(map(str, SIZES))} px)")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "src/MadModStudio.App/Assets/MadModStudio.ico")

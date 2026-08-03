#!/usr/bin/env bash
#
# Renders every platform's icon from the one source, assets/cairn.svg.
#
#   assets/cairn.icns              macOS bundle icon
#   assets/cairn.ico               Windows .exe and window icon
#   src/Cairn.App/Assets/cairn.ico the same file, as an Avalonia resource
#   assets/png/cairn-<n>.png       Linux (hicolor icon theme sizes) and general use
#
# The outputs are committed, so building on Linux or Windows never needs this script.
# Run it only when the artwork changes — it needs macOS tooling (qlmanage renders the
# SVG, iconutil packs the .icns).
#
# Every size is rendered from the vector rather than downscaled from one large raster:
# a 16px icon resampled from 1024px turns the stones to mush, whereas rendering at the
# target size keeps the dark gaps between them.

set -euo pipefail
cd "$(dirname "$0")"

SVG="assets/cairn.svg"
# 480 is not an icon-theme size: it is the logo the ModDB listing wants, kept here so
# it is regenerated with everything else rather than going stale on its own.
SIZES=(16 24 32 48 64 128 256 480 512 1024)
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

command -v qlmanage >/dev/null || { echo "needs macOS (qlmanage)"; exit 1; }

echo "rendering $SVG"
for s in "${SIZES[@]}"; do
  qlmanage -t -s "$s" -o "$WORK" "$SVG" >/dev/null 2>&1
  mv "$WORK/$(basename "$SVG").png" "$WORK/$s.png"
  # qlmanage fits the render into an s-by-s box; assert it really is square at s.
  got="$(sips -g pixelWidth "$WORK/$s.png" | awk '/pixelWidth/{print $2}')"
  [ "$got" = "$s" ] || { echo "  ! $s.png rendered at ${got}px"; exit 1; }
  printf '  %sx%s\n' "$s" "$s"
done

# ---- transparency ---------------------------------------------------------
# QuickLook composites onto white, so every render above has opaque white where the
# rounded corners should be nothing at all. Unfixed that reaches the .icns, both .ico
# files and every PNG — most visibly as white corners on a dark Windows taskbar.
#
# The shape is known exactly: the SVG is a rect with rx=228 on a 1024 canvas. So the
# coverage can be recomputed and the compositing undone rather than guessed at,
#
#     rendered = colour*a + 255*(1-a)   =>   colour = (rendered - 255*(1-a)) / a
#
# which leaves interior pixels (a=1) untouched and changes only the curve and the
# corners. The guess — turning white transparent — would eat the highlights on the
# stones and leave a white fringe along the curve.
python3 - "$WORK" "${SIZES[@]}" <<'PY'
import struct, sys, zlib, os

work, sizes = sys.argv[1], [int(s) for s in sys.argv[2:]]
RADIUS_RATIO = 228.0 / 1024.0          # matches rx in assets/cairn.svg


def read_rgba(path):
    data = open(path, "rb").read()
    pos, idat, ihdr = 8, b"", None
    while pos < len(data):
        ln = struct.unpack(">I", data[pos:pos + 4])[0]
        typ = data[pos + 4:pos + 8]
        if typ == b"IHDR":
            ihdr = struct.unpack(">IIBBBBB", data[pos + 8:pos + 8 + ln])
        elif typ == b"IDAT":
            idat += data[pos + 8:pos + 8 + ln]
        pos += 12 + ln

    w, h, depth, color, _, _, interlace = ihdr
    if (depth, color, interlace) != (8, 6, 0):
        raise SystemExit(f"  ! {path}: expected 8-bit RGBA, got depth={depth} color={color}")

    raw, stride, bpp = zlib.decompress(idat), w * 4, 4
    out, prev, i = bytearray(w * h * 4), bytearray(stride), 0

    for y in range(h):
        f = raw[i]; i += 1
        line = bytearray(raw[i:i + stride]); i += stride

        if f == 1:
            for x in range(bpp, stride):
                line[x] = (line[x] + line[x - bpp]) & 0xFF
        elif f == 2:
            for x in range(stride):
                line[x] = (line[x] + prev[x]) & 0xFF
        elif f == 3:
            for x in range(stride):
                left = line[x - bpp] if x >= bpp else 0
                line[x] = (line[x] + ((left + prev[x]) >> 1)) & 0xFF
        elif f == 4:
            for x in range(stride):
                a = line[x - bpp] if x >= bpp else 0
                b, c = prev[x], (prev[x - bpp] if x >= bpp else 0)
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[x] = (line[x] + (a if (pa <= pb and pa <= pc) else (b if pb <= pc else c))) & 0xFF
        elif f != 0:
            raise SystemExit(f"  ! {path}: unknown PNG filter {f}")

        out[y * stride:(y + 1) * stride] = line
        prev = line

    return w, h, out


def write_rgba(path, w, h, px):
    stride = w * 4
    raw = bytearray()
    for y in range(h):
        raw.append(0)                                  # filter: none
        raw += px[y * stride:(y + 1) * stride]

    def chunk(typ, body):
        return (struct.pack(">I", len(body)) + typ + body
                + struct.pack(">I", zlib.crc32(typ + body) & 0xFFFFFFFF))

    open(path, "wb").write(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b""))


def coverage(x, y, size, radius):
    """Antialiased coverage of the rounded square, from its signed distance field."""
    half = size / 2.0
    px, py = x + 0.5 - half, y + 0.5 - half
    qx, qy = abs(px) - (half - radius), abs(py) - (half - radius)
    d = (max(qx, 0.0) ** 2 + max(qy, 0.0) ** 2) ** 0.5 + min(max(qx, qy), 0.0) - radius
    return min(max(0.5 - d, 0.0), 1.0)


for s in sizes:
    path = os.path.join(work, f"{s}.png")
    w, h, px = read_rgba(path)
    radius = RADIUS_RATIO * w

    for y in range(h):
        row = y * w * 4
        for x in range(w):
            a = coverage(x, y, w, radius)
            if a >= 0.999:
                continue                               # interior: already correct

            i = row + x * 4
            if a <= 0.0:
                px[i:i + 4] = b"\x00\x00\x00\x00"
                continue

            for c in range(3):
                px[i + c] = min(255, max(0, int(round((px[i + c] - 255.0 * (1.0 - a)) / a))))
            px[i + 3] = int(round(a * 255))

    # The corner is the whole point, so it is asserted rather than assumed.
    if px[3] != 0:
        raise SystemExit(f"  ! {s}.png still has an opaque corner (alpha {px[3]})")

    write_rgba(path, w, h, px)

print(f"  alpha restored on {len(sizes)} renders")
PY

# ---- macOS ----------------------------------------------------------------
# iconutil requires this exact naming; @2x is the same pixel count as the next size up.
ICONSET="$WORK/cairn.iconset"
mkdir -p "$ICONSET"
cp "$WORK/16.png"   "$ICONSET/icon_16x16.png"
cp "$WORK/32.png"   "$ICONSET/icon_16x16@2x.png"
cp "$WORK/32.png"   "$ICONSET/icon_32x32.png"
cp "$WORK/64.png"   "$ICONSET/icon_32x32@2x.png"
cp "$WORK/128.png"  "$ICONSET/icon_128x128.png"
cp "$WORK/256.png"  "$ICONSET/icon_128x128@2x.png"
cp "$WORK/256.png"  "$ICONSET/icon_256x256.png"
cp "$WORK/512.png"  "$ICONSET/icon_256x256@2x.png"
cp "$WORK/512.png"  "$ICONSET/icon_512x512.png"
cp "$WORK/1024.png" "$ICONSET/icon_512x512@2x.png"
iconutil -c icns "$ICONSET" -o assets/cairn.icns
echo "  -> assets/cairn.icns"

# ---- Windows --------------------------------------------------------------
# Written by hand: there is no ImageMagick here, and an .ico is a short header
# followed by the images themselves. Entries are stored as PNG, which Windows has
# accepted since Vista and which Skia (what Avalonia decodes with) reads too.
python3 - "$WORK" <<'PY'
import struct, sys, os

work = sys.argv[1]
sizes = [16, 24, 32, 48, 64, 128, 256]
blobs = [(s, open(os.path.join(work, f"{s}.png"), "rb").read()) for s in sizes]

out = bytearray()
out += struct.pack("<HHH", 0, 1, len(blobs))          # reserved, type=icon, count
offset = 6 + 16 * len(blobs)
for s, data in blobs:
    out += struct.pack("<BBBBHHII",
                       0 if s == 256 else s,          # 256 is encoded as 0
                       0 if s == 256 else s,
                       0, 0,                          # palette size, reserved
                       1, 32,                         # colour planes, bits per pixel
                       len(data), offset)
    offset += len(data)
for _, data in blobs:
    out += data

for path in ("assets/cairn.ico", "src/Cairn.App/Assets/cairn.ico"):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    open(path, "wb").write(out)
print(f"  -> assets/cairn.ico ({len(out)} bytes, {len(blobs)} sizes)")
PY

# ---- Linux and general ----------------------------------------------------
mkdir -p assets/png
for s in 16 24 32 48 64 128 256 480 512 1024; do
  cp "$WORK/$s.png" "assets/png/cairn-$s.png"
done
echo "  -> assets/png/cairn-{16..1024}.png"

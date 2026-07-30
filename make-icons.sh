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
SIZES=(16 24 32 48 64 128 256 512 1024)
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
for s in 16 24 32 48 64 128 256 512 1024; do
  cp "$WORK/$s.png" "assets/png/cairn-$s.png"
done
echo "  -> assets/png/cairn-{16..1024}.png"

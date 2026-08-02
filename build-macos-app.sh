#!/usr/bin/env bash
#
# Builds Cairn.app, a proper macOS application bundle.
#
# Two reasons this beats the bare executable:
#   * It gets a Dock icon, a real app name in the menu bar, and normal foreground
#     activation. A loose binary does not.
#   * It publishes as a plain directory rather than a single file, so there is no
#     self-extraction step on first run. Single-file publishing unpacks to ~/.net/<app>
#     before the window can appear.
#
# Note the layout is a real bundle — Contents/MacOS, Contents/Info.plist. The *game*
# ships a flat layout with Info.plist at the top level, which is exactly why naming that
# directory "*.app" makes codesign treat it as a malformed bundle and macOS call it
# damaged. Do not imitate it.
#
# Usage: ./build-macos-app.sh [rid]        (default: osx-arm64)
#   SIGN_IDENTITY="Developer ID Application: ..."  to sign properly; otherwise ad-hoc.
#   ICON=path/to/icon.png                          1024x1024 recommended.

set -euo pipefail
cd "$(dirname "$0")"

RID="${1:-osx-arm64}"

# Overridable so a release can stamp the tag it was cut from. The bundle's version is what
# Finder shows and what macOS compares when deciding whether an install is an upgrade, so
# every build claiming 0.1.0 forever is a small lie that gets harder to correct later.
VERSION="${VERSION:-0.1.0}"
BUNDLE_ID="${BUNDLE_ID:-com.dizzyd.cairn}"
APP="artifacts/$RID/Cairn.app"
EXE="cairn"

echo "building $APP ($RID)"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# Quiet while it works, and everything it said when it does not.
#
# This used to redirect to /dev/null unconditionally, which is fine on a machine where you
# can just run it again — and useless on a build runner, where it turns a failure into an
# exit code and two seconds of silence.
publish() {
  local log
  log="$(mktemp)"

  # -p:Version as well as the plist: the plist is what Finder shows, and this is what the
  # app itself can read to say which version it is in its own window.
  if ! dotnet publish "$1" \
      -c Release -r "$RID" --self-contained true \
      -p:Version="$VERSION" \
      -p:PublishSingleFile=false -p:DebugType=none \
      -o "$APP/Contents/MacOS" --nologo -v quiet > "$log" 2>&1; then
    echo "  dotnet publish failed:"
    sed 's/^/    /' "$log"
    rm -f "$log"
    exit 1
  fi

  rm -f "$log"
}

# Not single-file: a bundle is already a directory, and avoiding self-extraction is the
# whole point of doing this — and it is also what makes the result notarisable, since a
# single-file build unpacks binaries at runtime that were never signed.
#
# The launcher only. The CLI used to ship in here too, which meant a download offering a
# second program nobody was told about; run it from the source tree while it is a
# development tool rather than a published one.
publish src/Cairn.App/Cairn.App.csproj

ICON_KEY=""
ICON_KEY_TEXT='    <key>CFBundleIconFile</key>
    <string>cairn</string>'

if [ -f assets/cairn.icns ]; then
  # Prebuilt by make-icons.sh, which renders each size from assets/cairn.svg. Preferred
  # over resampling one large PNG: a 16px icon downscaled from 1024px loses the gaps
  # between the stones and turns into a smudge.
  cp assets/cairn.icns "$APP/Contents/Resources/cairn.icns"
  ICON_KEY="$ICON_KEY_TEXT"
  echo "  icon: assets/cairn.icns"
elif [ -n "${ICON:-}" ] && [ -f "${ICON:-}" ]; then
  echo "  icon: $ICON (resampled)"
  ICONSET="$(mktemp -d)/cairn.iconset"
  mkdir -p "$ICONSET"
  for size in 16 32 128 256 512; do
    sips -z $size $size "$ICON" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null 2>&1
    sips -z $((size * 2)) $((size * 2)) "$ICON" \
      --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null 2>&1
  done
  iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/cairn.icns"
  ICON_KEY="$ICON_KEY_TEXT"
else
  echo "  icon: none (run ./make-icons.sh)"
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
  <dict>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>$EXE</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleName</key>
    <string>Cairn</string>
    <key>CFBundleDisplayName</key>
    <string>Cairn</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.utilities</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <!-- "Open in Cairn", on a pack page. LaunchServices reads this when it first sees
         the bundle, so the scheme starts working once the app has been somewhere macOS
         scans — building it is not enough. -->
    <key>CFBundleURLTypes</key>
    <array>
      <dict>
        <key>CFBundleURLName</key>
        <string>$BUNDLE_ID.pack</string>
        <key>CFBundleTypeRole</key>
        <string>Viewer</string>
        <key>CFBundleURLSchemes</key>
        <array>
          <string>cairn</string>
        </array>
      </dict>
    </array>
$ICON_KEY
  </dict>
</plist>
PLIST

plutil -lint "$APP/Contents/Info.plist" >/dev/null

# Signing.
#
# --deep, which Apple discourages, after establishing that the alternative does not work
# here. .NET's apphost requires cairn.runtimeconfig.json and cairn.deps.json to sit beside
# the executable, and codesign treats every non-code file in Contents/MacOS as nested code
# that must carry its own signature. A .json cannot, so signing nested binaries
# individually and then the bundle fails at the last step with
#
#   code object is not signed at all
#   In subcomponent: .../Contents/MacOS/cairn.runtimeconfig.json
#
# every time, on a clean tree. Moving the payload out of MacOS/ would mean replacing the
# apphost, which is a much larger change than this is worth.
#
# The cost of --deep is that these entitlements reach nested code as well as the app. They
# are narrow, and the notary service is the actual arbiter of whether Apple minds — if it
# ever objects, the fix is restructuring the bundle rather than a different flag.
#
# The hardened runtime is required for notarisation, and is applied for ad-hoc builds too:
# a local build should fail the way a released one would, not save the surprise. Verified
# it still launches — CoreCLR needs the JIT entitlements or it dies immediately.
if [ "${SKIP_SIGN:-0}" = 1 ]; then
  # Unsigned is fine for a local dev build; macOS only insists for distribution.
  echo "  signing: skipped (SKIP_SIGN=1)"
else
  IDENTITY="${SIGN_IDENTITY:--}"

  # A secure timestamp is required for notarisation, and is a round trip to Apple's
  # server — worth skipping for the ad-hoc builds that are never notarised.
  if [ "$IDENTITY" = "-" ]; then
    echo "  signing: ad-hoc (set SIGN_IDENTITY to sign properly)"
    STAMP=(--timestamp=none)
  else
    echo "  signing: $IDENTITY"
    STAMP=(--timestamp)
  fi

  codesign --force --deep --options runtime "${STAMP[@]}" \
    --entitlements macos-entitlements.plist --sign "$IDENTITY" "$APP"

  echo
  codesign --verify --strict --verbose=2 "$APP" 2>&1 | tail -2 | sed 's/^/  /'
  codesign -d --verbose=2 "$APP" 2>&1 | grep -i "flags=" | sed 's/^/  /'
fi

echo "  size: $(du -sh "$APP" | cut -f1)"
echo "  built: $APP"

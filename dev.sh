#!/usr/bin/env bash
#
# Builds Cairn for the machine you are on, and nothing else.
#
# build-release.sh publishes all four platforms and is for cutting a release. This is the
# one to use while testing: it detects the host rid and builds only that.
#
#   ./dev.sh              build for this machine
#   ./dev.sh --run        build, then launch it
#   ./dev.sh --no-sign    skip code signing (macOS; a little faster)
#   ./dev.sh --cli        build only the CLI
#
# Note on macOS: a bare `dotnet run` uses whatever SDK is on PATH. If that SDK is x64 —
# which it is on this machine — the launcher runs under Rosetta and feels sluggish.
# Publishing for the host rid is what gets you a native build.

set -euo pipefail
cd "$(dirname "$0")"

RUN=0
CLI_ONLY=0
export SIGN_IDENTITY="${SIGN_IDENTITY:--}"
SKIP_SIGN=0

for arg in "$@"; do
  case "$arg" in
    --run) RUN=1 ;;
    --cli) CLI_ONLY=1 ;;
    --no-sign) SKIP_SIGN=1 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

case "$(uname -s)" in
  Darwin) RID_OS=osx ;;
  Linux)  RID_OS=linux ;;
  *)      RID_OS=win ;;
esac

case "$(uname -m)" in
  arm64|aarch64) RID_ARCH=arm64 ;;
  x86_64|amd64)  RID_ARCH=x64 ;;
  *) echo "unsupported architecture: $(uname -m)" >&2; exit 2 ;;
esac

RID="$RID_OS-$RID_ARCH"
echo "building for $RID"

publish() {
  dotnet publish "$1" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -p:DebugType=none \
    -o "$2" --nologo -v quiet >/dev/null
}

if [ "$CLI_ONLY" = 1 ]; then
  OUT="artifacts/$RID"
  publish src/Cairn.Cli/Cairn.Cli.csproj "$OUT"
  echo "  built $OUT/cairn"
  [ "$RUN" = 1 ] && exec "$OUT/cairn" info
  exit 0
fi

if [ "$RID_OS" = osx ]; then
  # A real bundle, so it gets a Dock tile and a proper app name.
  SKIP_SIGN="$SKIP_SIGN" ./build-macos-app.sh "$RID"

  APP="artifacts/$RID/Cairn.app"
  echo
  echo "  $APP"
  [ "$RUN" = 1 ] && open "$APP"
  exit 0
fi

OUT="artifacts/$RID"
publish src/Cairn.App/Cairn.App.csproj "$OUT"
publish src/Cairn.Cli/Cairn.Cli.csproj "$OUT"

echo "  built $OUT/cairn-launcher and $OUT/cairn"
[ "$RUN" = 1 ] && exec "$OUT/cairn-launcher"

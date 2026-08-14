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
#   ./dev.sh --local            run against a cairns on this machine (see cairns/dev.sh)
#   ./dev.sh --server URL       run against a cairns somewhere else
#   ./dev.sh --home DIR         keep packs, games and the sign-in token in DIR
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
SERVER=""
HOME_DIR=""

while [ $# -gt 0 ]; do
  case "$1" in
    --run) RUN=1; shift ;;
    --cli) CLI_ONLY=1; shift ;;
    --no-sign) SKIP_SIGN=1; shift ;;
    --server) SERVER="${2:?--server needs a URL}"; shift 2 ;;
    --home) HOME_DIR="${2:?--home needs a directory}"; shift 2 ;;

    # Both halves of testing against a local server, because doing only the first half is
    # a trap: publishing writes a cairns.json into the pack naming where it went, and a
    # real pack would come away claiming to live at a localhost URL that stops existing
    # when the server does.
    --local)
      SERVER="${SERVER:-http://localhost:5080}"
      HOME_DIR="${HOME_DIR:-$HOME/.cairn-dev}"
      RUN=1
      shift ;;

    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

# Only ever reaches the process being launched, so asking for one without --run would
# silently do nothing.
if [ -n "$SERVER$HOME_DIR" ] && [ "$RUN" = 0 ]; then
  echo "--server/--home only apply to the launched process; add --run" >&2
  exit 2
fi

ENV_ARGS=()
[ -n "$SERVER" ] && { export CAIRNS_SERVER="$SERVER"; ENV_ARGS+=(--env "CAIRNS_SERVER=$SERVER"); }
# CAIRN_DEFAULT_HOME, not CAIRN_HOME. Both put the sandbox somewhere harmless, and only
# one of them leaves the build behaving like a real install: CAIRN_HOME outranks the pointer
# file, so a dev run using it exercises the branch almost no user takes, and the launcher
# will not offer to move a root the environment chose — which made the move untestable in
# the setup that exists for testing. This moves the default instead, so everything downstream
# behaves exactly as it does for somebody who never set either.
[ -n "$HOME_DIR" ] && { export CAIRN_DEFAULT_HOME="$HOME_DIR"; ENV_ARGS+=(--env "CAIRN_DEFAULT_HOME=$HOME_DIR"); }

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
[ -n "$SERVER" ] && echo "  cairns:    $SERVER"
[ -n "$HOME_DIR" ] && echo "  cairn home: $HOME_DIR"

publish() {
  dotnet publish "$1" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -p:DebugType=none \
    -o "$2" --nologo -v quiet >/dev/null
}

if [ "$CLI_ONLY" = 1 ]; then
  OUT="artifacts/$RID"
  publish src/Cairn.Cli/Cairn.Cli.csproj "$OUT"
  echo "  built $OUT/cairn-cli"
  [ "$RUN" = 1 ] && exec "$OUT/cairn-cli" info
  exit 0
fi

if [ "$RID_OS" = osx ]; then
  # A real bundle, so it gets a Dock tile and a proper app name.
  SKIP_SIGN="$SKIP_SIGN" ./build-macos-app.sh "$RID"

  APP="artifacts/$RID/Cairn.app"
  echo
  echo "  $APP"

  if [ "$RUN" = 1 ]; then
    if [ ${#ENV_ARGS[@]} -gt 0 ]; then
      # --env rather than relying on inheritance, and -n because `open` on a bundle that
      # is already running just brings it forward — still holding the environment it
      # started with, which is the one you are trying to change.
      open "${ENV_ARGS[@]}" -n "$APP"
    else
      open "$APP"
    fi
  fi

  exit 0
fi

OUT="artifacts/$RID"
publish src/Cairn.App/Cairn.App.csproj "$OUT"
publish src/Cairn.Cli/Cairn.Cli.csproj "$OUT"

echo "  built $OUT/cairn and $OUT/cairn-cli"
[ "$RUN" = 1 ] && exec "$OUT/cairn"

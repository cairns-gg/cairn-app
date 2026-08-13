#!/usr/bin/env bash
#
# Publishes self-contained, single-file binaries with no .NET prerequisite.
#
# The game is framework-dependent and needs a .NET 10 runtime on the machine, but Cairn
# must not: a launcher that itself requires a runtime install cannot help a user who has
# neither. Self-contained means the user downloads one file and runs it.
#
# Usage: ./build-release.sh [rid ...]      (default: every supported rid)

set -euo pipefail

cd "$(dirname "$0")"

DEFAULT_RIDS=(osx-arm64 osx-x64 win-x64 linux-x64)
RIDS=("${@:-}")
[ -z "${RIDS[0]:-}" ] && RIDS=("${DEFAULT_RIDS[@]}")

OUT="artifacts"
rm -rf "$OUT"

# Stamped into the assembly so the app can report which version it is. Unset means an
# unstamped build, which says "dev" rather than inventing a number that will be believed.
VERSION_ARG=()
[ -n "${VERSION:-}" ] && VERSION_ARG=(-p:Version="$VERSION")

publish() {
  local project="$1" name="$2" rid="$3"
  local dest="$OUT/$rid"

  # Quiet while it works, and everything it said when it does not — the same shape
  # build-macos-app.sh uses, and for the reason written there. This redirected to
  # /dev/null unconditionally, which on a build runner turns a failure into an exit code
  # and no explanation. That was survivable while nothing here failed for an interesting
  # reason; it stopped being so when the audit of restored packages started reporting
  # known vulnerabilities as errors, because the one message worth reading was the one
  # being discarded.
  local log
  log="$(mktemp)"

  if ! dotnet publish "$project" \
      -c Release \
      -r "$rid" \
      --self-contained true \
      ${VERSION_ARG+"${VERSION_ARG[@]}"} \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:EnableCompressionInSingleFile=true \
      -p:DebugType=none \
      -o "$dest" \
      --nologo -v quiet > "$log" 2>&1; then
    echo "  dotnet publish failed:"
    sed 's/^/    /' "$log"
    rm -f "$log"
    exit 1
  fi

  rm -f "$log"

  # Both projects share a per-rid directory. With single-file publishing each is
  # effectively one executable, so there is nothing to collide.
  printf '  %-10s %-18s %s\n' "$rid" "$name" \
    "$(du -h "$dest/$name"* 2>/dev/null | sort -rh | head -1 | cut -f1)"
}

for rid in "${RIDS[@]}"; do
  echo "publishing $rid"
  publish src/Cairn.Cli/Cairn.Cli.csproj cairn-cli "$rid"
  publish src/Cairn.App/Cairn.App.csproj cairn "$rid"

  # linux-x64 only, and not for want of portability: the code runs anywhere, but a
  # dedicated server is published by the game for Linux and Windows alone, "unit" writes
  # systemd files, and the machines people actually host on are Linux. Building a Windows
  # binary nobody asked for is a second thing to test and explain for no one.
  [ "$rid" = linux-x64 ] && publish src/Cairn.Server/Cairn.Server.csproj cairn-server "$rid"
done

echo
echo "artifacts:"
find "$OUT" -maxdepth 2 -type f \( -name 'cairn' -o -name 'cairn.exe' \
  -o -name 'cairn-cli' -o -name 'cairn-cli.exe' -o -name 'cairn-server' \) \
  -exec ls -lh {} \; | awk '{printf "  %-10s %s\n", $5, $NF}'

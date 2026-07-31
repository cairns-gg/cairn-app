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

publish() {
  local project="$1" name="$2" rid="$3"
  local dest="$OUT/$rid"

  dotnet publish "$project" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none \
    -o "$dest" \
    --nologo -v quiet >/dev/null

  # Both projects share a per-rid directory. With single-file publishing each is
  # effectively one executable, so there is nothing to collide.
  printf '  %-10s %-18s %s\n' "$rid" "$name" \
    "$(du -h "$dest/$name"* 2>/dev/null | sort -rh | head -1 | cut -f1)"
}

for rid in "${RIDS[@]}"; do
  echo "publishing $rid"
  publish src/Cairn.Cli/Cairn.Cli.csproj cairn-cli "$rid"
  publish src/Cairn.App/Cairn.App.csproj cairn "$rid"
done

echo
echo "artifacts:"
find "$OUT" -maxdepth 2 -type f \( -name 'cairn' -o -name 'cairn.exe' \
  -o -name 'cairn-cli' -o -name 'cairn-cli.exe' \) \
  -exec ls -lh {} \; | awk '{printf "  %-10s %s\n", $5, $NF}'

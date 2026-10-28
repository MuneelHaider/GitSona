#!/usr/bin/env bash
# Builds GitPersona for the current Linux or macOS machine into artifacts/.
#
#   ./build.sh [version] [runtime]
#
# runtime defaults to linux-x64 on Linux and osx-arm64 / osx-x64 on macOS (by CPU).
# Windows builds use build.ps1. Run with SKIP_TESTS=1 to skip the test run.
set -euo pipefail
cd "$(dirname "$0")"

VERSION="${1:-0.1.0}"
case "$(uname -s)" in
  Linux)  OS=linux; DEFAULT_RID=linux-x64 ;;
  Darwin) OS=osx;   DEFAULT_RID=$([ "$(uname -m)" = "arm64" ] && echo osx-arm64 || echo osx-x64) ;;
  *) echo "Unsupported OS: $(uname -s). Use build.ps1 on Windows." >&2; exit 1 ;;
esac
RID="${2:-$DEFAULT_RID}"

if [ "${SKIP_TESTS:-0}" != "1" ]; then
  dotnet test GitPersona.slnx -c Release
fi

rm -rf "artifacts/$RID"
mkdir -p "artifacts/$RID"
PUBLISH="artifacts/publish/$RID"

dotnet publish src/GitPersona.App -c Release -r "$RID" -p:Version="$VERSION" -o "$PUBLISH"

# Plain portable archive: just the executable (keeps its executable bit inside the tarball).
tar -czf "artifacts/$RID/GitPersona-$RID.tar.gz" -C "$PUBLISH" GitPersona

# Velopack package: AppImage on Linux, .app bundle + installer on macOS.
# Not code-signed or notarised in v1; failures here must not lose the portable archive.
ICON=src/GitPersona.App/Assets/app.png
[ "$OS" = "osx" ] && ICON=src/GitPersona.App/Assets/app.icns
mkdir -p artifacts/pack && rm -rf "artifacts/pack/$RID" && mkdir -p "artifacts/pack/$RID"
cp "$PUBLISH/GitPersona" "artifacts/pack/$RID/"

dotnet tool restore
if dotnet vpk "$OS" pack \
    --packId GitPersona --packVersion "$VERSION" --packTitle GitPersona --packAuthors GitPersona \
    --runtime "$RID" --packDir "artifacts/pack/$RID" --mainExe GitPersona \
    --icon "$ICON" --outputDir "artifacts/$RID/velopack"; then
  echo "Velopack package built."
else
  echo "WARNING: Velopack packaging failed; the portable archive was still produced." >&2
fi

echo
echo "Done. Version $VERSION ($RID)"
find "artifacts/$RID" -type f -maxdepth 2 | sort

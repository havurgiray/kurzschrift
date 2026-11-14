#!/bin/bash
# Builds Kurzschrift.app and installs it into ~/Applications.
# With --no-install it stops at build/Kurzschrift.app and leaves the running app alone.
# With --arch arm64 or --arch x86_64 (only together with --no-install) it builds for that
# architecture instead of this Mac's, into build/<arch>/Kurzschrift.app (release.sh uses this).
# SwiftPM keeps each architecture's build in its own folder, .build/<arch>-apple-macosx.
#
# Signed ad hoc (no certificate, no keychain access). The catch: macOS treats every rebuild as a
# new app, so it asks again for microphone and system-audio permission after each rebuild.
set -euo pipefail
cd "$(dirname "$0")"

usage() { echo "usage: ./build.sh [--no-install] [--arch arm64|x86_64]" >&2; exit 2; }
INSTALL=yes
ARCH=
while [ $# -gt 0 ]; do
  case "$1" in
    --no-install) INSTALL=no ;;
    --arch) [ $# -ge 2 ] || usage; ARCH="$2"; shift ;;
    *) usage ;;
  esac
  shift
done

# --disable-keychain: stop SwiftPM from looking up github.com credentials in the login keychain.
BUILD=(swift build -c release --disable-keychain)
APP="build/Kurzschrift.app"
if [ -n "$ARCH" ]; then
  case "$ARCH" in arm64|x86_64) ;; *) usage ;; esac
  if [ "$INSTALL" = yes ]; then
    echo "--arch builds are for release.sh and aren't installed; add --no-install." >&2
    exit 2
  fi
  MIN_MACOS=$(/usr/libexec/PlistBuddy -c "Print :LSMinimumSystemVersion" Info.plist)
  BUILD+=(--triple "$ARCH-apple-macosx$MIN_MACOS")
  APP="build/$ARCH/Kurzschrift.app"
fi
"${BUILD[@]}"
# Not .build/release: that link points at whichever architecture was built last.
BIN=$("${BUILD[@]}" --show-bin-path)

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN/Kurzschrift" "$APP/Contents/MacOS/"
cp Info.plist "$APP/Contents/"
for bundle in "$BIN"/*.bundle; do
  [ -e "$bundle" ] && cp -R "$bundle" "$APP/Contents/Resources/"
done
codesign --force --deep --sign - "$APP"
[ "$INSTALL" = no ] && exit 0

INSTALLED="$HOME/Applications/Kurzschrift.app"
mkdir -p "$HOME/Applications"
pkill -x Kurzschrift 2>/dev/null && sleep 1 || true
rm -rf "$INSTALLED"
cp -R "$APP" "$INSTALLED"
# Only one copy on disk, so kurzschrift:// links always reach the installed app.
rm -rf build
echo "Installed $INSTALLED"

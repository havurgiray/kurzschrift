#!/bin/bash
# Builds Kurzschrift.app and installs it into ~/Applications.
#
# Signed ad hoc (no certificate, no keychain access). The catch: macOS treats every rebuild as a
# new app, so it asks again for microphone and system-audio permission after each rebuild.
set -euo pipefail
cd "$(dirname "$0")"

# --disable-keychain: stop SwiftPM from looking up github.com credentials in the login keychain.
swift build -c release --disable-keychain

APP="build/Kurzschrift.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/Kurzschrift "$APP/Contents/MacOS/"
cp Info.plist "$APP/Contents/"
for bundle in .build/release/*.bundle; do
  [ -e "$bundle" ] && cp -R "$bundle" "$APP/Contents/Resources/"
done
codesign --force --deep --sign - "$APP"

INSTALLED="$HOME/Applications/Kurzschrift.app"
mkdir -p "$HOME/Applications"
pkill -x Kurzschrift 2>/dev/null && sleep 1 || true
rm -rf "$INSTALLED"
cp -R "$APP" "$INSTALLED"
# Only one copy on disk, so kurzschrift:// links always reach the installed app.
rm -rf build
echo "Installed $INSTALLED"

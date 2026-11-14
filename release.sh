#!/bin/bash
# Builds the downloads for a GitHub release, with the version taken from Info.plist:
#   dist/Kurzschrift-<version>-mac-arm64.dmg   Apple Silicon
#   dist/Kurzschrift-<version>-mac-x86_64.dmg  Intel
#   dist/Kurzschrift-<version>.zip             the Apple Silicon app, zipped as before
# Each disk image holds Kurzschrift.app and a link to Applications to drag it onto.
# It doesn't install anything or quit the running app.
#
# Then publish them:
#   git tag -a v<version> -m "Kurzschrift <version>" && git push origin v<version>
#   gh release create v<version> dist/Kurzschrift-<version>-mac-*.dmg dist/Kurzschrift-<version>.zip --verify-tag
#
# The app is signed ad hoc, not notarized, so people who download it allow it once in
# System Settings -> Privacy & Security (see the README).
set -euo pipefail
cd "$(dirname "$0")"

if [ -n "$(git status --porcelain)" ]; then
  echo "Commit or stash your changes first, so the downloads match a commit." >&2
  exit 1
fi
VERSION=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" Info.plist)

rm -rf build
mkdir -p dist
ARTIFACTS=()
# arm64 last, so .build/release points at the Apple Silicon build again afterwards.
for ARCH in x86_64 arm64; do
  ./build.sh --no-install --arch "$ARCH"
  APP="build/$ARCH/Kurzschrift.app"
  codesign --verify --deep --strict "$APP"
  BUILT=$(lipo -archs "$APP/Contents/MacOS/Kurzschrift")
  if [ "$BUILT" != "$ARCH" ]; then
    echo "$APP is built for '$BUILT', expected $ARCH." >&2
    exit 1
  fi

  # The disk image's contents: the app and a link to /Applications.
  STAGE="build/dmg-$ARCH"
  mkdir -p "$STAGE"
  ditto "$APP" "$STAGE/Kurzschrift.app"
  ln -s /Applications "$STAGE/Applications"
  DMG="dist/Kurzschrift-$VERSION-mac-$ARCH.dmg"
  rm -f "$DMG"
  hdiutil create -quiet -volname "Kurzschrift $VERSION" -srcfolder "$STAGE" -fs HFS+ -format UDZO "$DMG"
  hdiutil verify -quiet "$DMG"
  ARTIFACTS+=("$DMG")

  if [ "$ARCH" = arm64 ]; then
    ZIP="dist/Kurzschrift-$VERSION.zip"
    rm -f "$ZIP"
    # ditto keeps the bundle's signature and symlinks intact.
    ditto -c -k --keepParent "$APP" "$ZIP"
    ARTIFACTS+=("$ZIP")
  fi
done
# Only one copy of the app on disk, so kurzschrift:// links keep reaching the installed one.
rm -rf build

echo "Built for $(git rev-parse --short HEAD):"
for f in "${ARTIFACTS[@]}"; do
  echo "  $f ($(du -h "$f" | cut -f1 | tr -d ' '))"
done
shasum -a 256 "${ARTIFACTS[@]}"

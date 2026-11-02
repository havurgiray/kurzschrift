#!/bin/bash
# Builds the download for a GitHub release: dist/Kurzschrift-<version>.zip, with the version
# taken from Info.plist. It doesn't install anything or quit the running app.
#
# Then publish it:
#   git tag -a v<version> -m "Kurzschrift <version>" && git push origin v<version>
#   gh release create v<version> dist/Kurzschrift-<version>.zip --verify-tag
#
# The app is signed ad hoc, not notarized, so people who download it allow it once in
# System Settings -> Privacy & Security (see the README).
set -euo pipefail
cd "$(dirname "$0")"

if [ -n "$(git status --porcelain)" ]; then
  echo "Commit or stash your changes first, so the zip matches a commit." >&2
  exit 1
fi
VERSION=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" Info.plist)

./build.sh --no-install
codesign --verify --deep --strict build/Kurzschrift.app

ZIP="dist/Kurzschrift-$VERSION.zip"
mkdir -p dist
rm -f "$ZIP"
# ditto keeps the bundle's signature and symlinks intact.
ditto -c -k --keepParent build/Kurzschrift.app "$ZIP"
# Only one copy of the app on disk, so kurzschrift:// links keep reaching the installed one.
rm -rf build

echo "Built $ZIP ($(git rev-parse --short HEAD))"
shasum -a 256 "$ZIP"

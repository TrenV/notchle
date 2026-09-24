#!/usr/bin/env bash
# Builds a release Notchle.app into build/ without Xcode (SwiftPM + ad-hoc codesign).
# Ad-hoc signatures change on every build, so macOS re-asks the Automation permission
# after a rebuild. That is expected.
set -euo pipefail
cd "$(dirname "$0")/.."
swift build -c release --product Notchle
BIN="$(swift build -c release --show-bin-path)"
APP=build/Notchle.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN/Notchle" "$APP/Contents/MacOS/Notchle"
cp Support/Info.plist "$APP/Contents/Info.plist"
for b in "$BIN"/*.bundle; do [[ -e "$b" ]] && cp -R "$b" "$APP/Contents/Resources/"; done
codesign --force --sign - --entitlements Support/Notchle.entitlements "$APP"
codesign --verify --verbose=1 "$APP"
echo "built $APP"

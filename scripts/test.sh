#!/usr/bin/env bash
# Runs the test suite. Works with only the Command Line Tools installed: their Swift Testing
# framework is not on SwiftPM's default search path, so we add it explicitly.
set -euo pipefail
cd "$(dirname "$0")/.."
"$(dirname "$0")/check-portable.sh"
if [[ "$(xcode-select -p)" == *CommandLineTools* ]]; then
  F=/Library/Developer/CommandLineTools/Library/Developer/Frameworks
  L=/Library/Developer/CommandLineTools/Library/Developer/usr/lib
  exec swift test -Xswiftc -F -Xswiftc "$F" -Xlinker -F -Xlinker "$F" \
    -Xlinker -rpath -Xlinker "$F" -Xlinker -rpath -Xlinker "$L" "$@"
fi
exec swift test "$@"

#!/usr/bin/env bash
# NotchleCore must stay Foundation-only so it can be built for Windows later.
set -euo pipefail
cd "$(dirname "$0")/.."
if grep -rnE '^\s*(@testable )?import (AppKit|SwiftUI|Combine|AVFoundation|AVKit|UIKit|Cocoa|os|OSLog|Darwin|Security|ScriptingBridge|MediaPlayer|CoreGraphics|QuartzCore)\b' Sources/NotchleCore; then
  echo "check-portable: NotchleCore imports a non-portable module (see above)" >&2
  exit 1
fi
if grep -rnE '\bNS(AppleScript|Workspace|Screen|Panel|Color|Image|Sound)\b|\bDispatchQueue\.main\b' Sources/NotchleCore; then
  echo "check-portable: NotchleCore uses an Apple-only API (see above)" >&2
  exit 1
fi
echo "check-portable: NotchleCore is Foundation-only"

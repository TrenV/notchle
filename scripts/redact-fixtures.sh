#!/usr/bin/env bash
# Spotify embed pages carry an anonymous web-player accessToken. Run this after saving a new
# fixture; FixtureHygieneTests fails if a token slips through.
set -euo pipefail
cd "$(dirname "$0")/.."
for f in Tests/NotchleCoreTests/Fixtures/*.html; do
  perl -pi -e 's/"accessToken":"[^"]*"/"accessToken":"REDACTED"/g' "$f"
done
echo "redacted $(ls Tests/NotchleCoreTests/Fixtures/*.html | wc -l | tr -d ' ') fixtures"

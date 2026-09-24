#!/usr/bin/env bash
# Run after saving a new Spotify embed page as a fixture: cuts it down to the data the
# parsers read (dropping Spotify's page code, images and the anonymous web-player token).
# FixtureHygieneTests fails if a fixture still carries any of that.
set -euo pipefail
cd "$(dirname "$0")/.."
python3 scripts/shrink-fixtures.py Tests/NotchleCoreTests/Fixtures/*.html

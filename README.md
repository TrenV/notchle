# Notchle

A Heardle-style song quiz that lives at the top of your screen. Paste a Spotify playlist,
album or artist link. Notchle plays 5 seconds of a song and asks for the title and every
credited artist; a wrong guess gives you a retry at 10 and 15 seconds. Get it right and
you get a burst of confetti while the song keeps playing. Clear all 20 songs of a set to
unlock the next 20.

| | macOS | Windows |
|---|---|---|
| Where it lives | in the MacBook notch (a pill under the menu bar without one) | the "island": a black pill hanging from the top edge of the screen |
| Code | Swift / SwiftUI (`Sources/`) | C# / .NET 10 / WPF (`windows/`) |
| Full songs | Spotify desktop app, via AppleScript | Spotify Connect (Premium + your own Spotify developer app) |
| No-login fallback | 30-second previews | 30-second previews (default) |
| Status | runs; playback measured live | compiles and its core tests pass on macOS; not yet run on Windows |

Both open on hover or click, close when the mouse leaves unless you're typing, and only
take the keyboard after you click them.

## Build

macOS (no Xcode needed, the Command Line Tools are enough):

    scripts/test.sh        # Swift tests (+ portability check)
    scripts/bundle.sh      # build/Notchle.app
    open build/Notchle.app

Windows: see [windows/README.md](windows/README.md). `windows/scripts/test.sh` builds the
Windows solution and runs its core tests on macOS too.

## How it fits together

- [ARCHITECTURE.md](ARCHITECTURE.md): the pure game reducer, the platform adapters, why tracks
  come from Spotify's public embed page.
- [spec/](spec/): answer-judging cases and shuffle vectors that the Swift and C# suites both
  run, so the two versions can't drift apart.

Saved Spotify pages under `Tests/NotchleCoreTests/Fixtures` must be redacted with
`scripts/redact-fixtures.sh`; a test fails if an access token slips through.

# Notchle architecture

A Heardle-style song quiz that lives in the MacBook notch. Paste a Spotify playlist, album
or artist link; Notchle plays 5 seconds, asks for title and artist, and offers a 10s and
15s retry. Correct: confetti and the song keeps playing. 20/20 unlocks the next 20.

## Layers

| Target | Platform | Contents |
|---|---|---|
| `NotchleCore` | portable (Foundation only) | models, game reducer, answer judge, Spotify embed parser, progress store |
| `NotchleMac` | macOS | Spotify-app player (AppleScript), preview player (AVPlayer), notch panel + SwiftUI views |
| `Notchle` | macOS | app entry; the coordinator that runs `GameEffect`s against the adapters |

`scripts/check-portable.sh` (run by `scripts/test.sh`) fails the build if `NotchleCore`
imports AppKit, SwiftUI, Combine, AVFoundation or other Apple-only modules.

## Game loop

`GameEngine.send(GameAction) -> [GameEffect]` is a pure reducer: no timers, no I/O, seeded
shuffles. The platform coordinator runs the effects (`fetch`, `playSnippet`,
`continuePlaying`, `stop`, `persistProgress`) and feeds results back as actions
(`loaded`, `snippetFinished`, `playbackFailed`). The UI renders `GameState` and sends actions.

## Track source

Spotify's Web API in development mode (Feb/Mar 2026 rules) only returns playlist items for
playlists you own or collaborate on, and removed artist top tracks. Notchle instead reads the
public embed page (`open.spotify.com/embed/<kind>/<id>`), whose `__NEXT_DATA__` JSON lists
title, artists, URI, duration and a preview URL. No login, no API key. It is undocumented
and may change; the parser is covered by fixture tests so a break is obvious.

## Porting to Windows

Reuse `NotchleCore` unchanged (Swift builds on Windows via SwiftPM). Provide:
- a `Player`: the Spotify Windows app has no AppleScript, so either play `previewURL`
  clips (portable) or drive playback through the Spotify Web API Connect endpoints (Premium);
- a UI that renders `GameState` and sends `GameAction`s (a top-of-screen overlay in
  WinUI, or any UI toolkit that can bridge to Swift);
- the app-data directory for `ProgressStore` (`FileManager` maps Application Support to AppData).

## Building (no Xcode needed)

    scripts/test.sh      # portability check + Swift Testing suite
    scripts/bundle.sh    # release build -> build/Notchle.app (ad-hoc signed)

# Notchle

A Heardle-style song quiz that lives at the top of your screen. Paste a Spotify playlist,
album or artist link. Notchle plays 5 seconds of a song and asks for the title and every
credited artist; a wrong guess gives you a retry at 10 and 15 seconds. Get it right and
you get a burst of confetti while the song keeps playing. Clear all 20 songs of a set to
unlock the next 20.

| | macOS | Windows |
|---|---|---|
| Where it lives | in the MacBook notch (a pill under the menu bar on Macs without one) | the "island": a black pill hanging from the top edge of the screen |
| Full songs | the Spotify desktop app | Spotify Connect (Premium + your own Spotify developer app) |
| No-login option | 30-second previews | 30-second previews (the default) |
| Status | runs; playback measured live | builds and passes its tests on Windows CI; not yet tried on a real PC |

---

## Install on macOS

### What you need

- macOS 14 (Sonoma) or later.
- Apple's Command Line Tools (Swift 6). Full Xcode is not needed. Check with `swift --version`;
  if it's missing, run `xcode-select --install`.
- The **Spotify desktop app**, installed and signed in (`brew install --cask spotify`, or
  from spotify.com). Only needed for full songs; the 30-second previews work without it.
  Tested with a signed-in account; Spotify Free has not been tried.

### Build and install

```bash
git clone https://github.com/TrenV/notchle.git
cd notchle
scripts/bundle.sh                  # builds build/Notchle.app (about a minute the first time)
cp -R build/Notchle.app /Applications/
open /Applications/Notchle.app
```

Notchle has no Dock icon. You'll find a ♪ icon in the menu bar (New link, Reset progress,
Quit) and the game in the notch.

### First launch

1. Hover over the notch to open it, click it, paste a Spotify link, press Enter.
2. When the first snippet plays, macOS asks: *"Notchle wants to control Spotify."* Click
   **Allow**.
3. If you clicked Don't Allow: System Settings → Privacy & Security → Automation →
   Notchle → turn **Spotify** on.

The app is signed only locally (no Apple Developer ID), so **macOS asks again after every
rebuild**. That's expected.

### Start at login (optional)

System Settings → General → Login Items → **+** → pick Notchle in Applications.

### Uninstall

Quit it from the ♪ menu, then:

```bash
rm -rf /Applications/Notchle.app ~/Library/Application\ Support/Notchle
```

---

## Install on Windows

### What you need

- Windows 10 (version 2004) or Windows 11, 64-bit.
- Nothing else for 30-second previews. The app is one self-contained `.exe`; no .NET
  install needed.

### Get the app

Either download it from CI (needs the GitHub CLI, `gh`, signed in):

```bash
gh run download -R TrenV/notchle -n Notchle-win-x64
```

This fetches `Notchle.Windows.exe` from the latest workflow run. GitHub keeps CI artifacts
for 90 days by default.

Or build it yourself on any OS with the .NET 10 SDK:

```bash
dotnet publish windows/src/Notchle.Windows -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

### Run it

1. Put `Notchle.Windows.exe` somewhere permanent, e.g. `%LOCALAPPDATA%\Programs\Notchle\`.
2. Double-click it. The exe is not code-signed, so Windows SmartScreen may warn: click
   **More info → Run anyway**.
3. The island appears at the top of your screen and an icon appears in the system tray.
   Hover over the island (or press **Ctrl+Alt+N**), click it, paste a Spotify link, press Enter.

Tray menu: New link, Connect Spotify, Reset progress, **Start with Windows**, Quit.

### Full songs with Spotify Connect (optional)

Previews play without any login. For full songs:

1. You need Spotify **Premium** and the Spotify desktop app open and signed in on the same PC.
2. Create a developer app at <https://developer.spotify.com/dashboard> → **Create app**:
   - Redirect URI: `http://127.0.0.1/callback` (exactly that; Spotify rejects `localhost`).
   - API: **Web API**.
   - Under **User Management**, add the Spotify account you'll play with.
3. Copy the app's **Client ID**, then in Notchle: tray → **Connect Spotify…** → paste it.
   Your browser opens Spotify's consent page; approve it.

Heads-up: in this mode Spotify's own window and the Windows media flyout (volume keys) can
show the song title. Also, Spotify Connect has only been tested against a simulated Spotify
so far, not a real account.

### Uninstall

Tray → Quit. Turn off **Start with Windows** first if you enabled it. Then delete the exe
and `%APPDATA%\Notchle`.

---

## Playing

- **Enter** submits (and means Next / Next set / Replay).
- **Tab** switches between Title and Artist(s).
- **Esc** gives up on this song.
- **⌘R** (macOS) / **Ctrl+R** (Windows) retries with a longer snippet after a wrong guess.
- Typos are fine. The artist field needs **every** credited artist, in any order; the
  placeholder tells you how many.
- The notch or island only takes the keyboard after you click it. It closes when your mouse
  leaves, unless you're typing.
- Playlists give up to 100 songs (5 sets), albums all their tracks, and artists their 10 top
  tracks.

## Developing

```bash
scripts/test.sh            # Swift tests + check that NotchleCore stays Foundation-only
windows/scripts/test.sh    # builds the Windows solution and runs its core tests (works on macOS)
swift run Notchle --ui-demo --ui-start guessing   # macOS UI demo with fake songs, no audio
```

- [ARCHITECTURE.md](ARCHITECTURE.md): the pure game reducer, the platform adapters, and why
  tracks come from Spotify's public embed page.
- [spec/](spec/): answer-judging cases and shuffle vectors that the Swift and C# suites both
  run, so the two versions can't drift apart.
- [windows/README.md](windows/README.md): Windows internals (players, snippet timing, CI).
- CI (`.github/workflows/windows.yml`) builds and tests both versions on every push and
  uploads Windows screenshots of every island screen.
- Saved Spotify pages in `Tests/NotchleCoreTests/Fixtures` must be run through
  `scripts/redact-fixtures.sh`, which shrinks them to the track data the parsers read; a test
  fails if a page still carries Spotify's page code, config or access token.

## License

[MIT](LICENSE). Notchle is not affiliated with or endorsed by Spotify. It reads Spotify's
public embed pages for track lists and plays music through your own Spotify app or account.

# Notchle for Windows

C# / .NET 10 / WPF port of the macOS app. The game rules, contracts and shared test vectors
are the Swift ones (see `/ARCHITECTURE.md`). An "island" at the top of the screen stands in
for the notch. A tray icon holds the menu.

## Layout

| Project | Targets | What |
| --- | --- | --- |
| `src/Notchle.Core` | `net10.0` | Everything platform-neutral: engine, judge, source, store, `App/AppCoordinator`, snippet timing, the Spotify Web API player and sign-in. |
| `src/Notchle.Windows` | `net10.0-windows10.0.22621.0` | WPF app: island UI, `Playback/` (WinRT preview player, DPAPI token store), `Shell/` (tray, single instance, autostart). |
| `tests/Notchle.Core.Tests` | `net10.0` | Runs on any OS. |
| `tests/Notchle.Windows.Tests` | Windows only | Runs in CI. |

## Build and test

```sh
windows/scripts/test.sh          # macOS/Linux: builds everything (XAML included), runs Core tests
```

```powershell
dotnet build windows/Notchle.Windows.slnx -c Release
dotnet test windows/tests/Notchle.Core.Tests -c Release
dotnet test windows/tests/Notchle.Windows.Tests -c Release
windows/src/Notchle.Windows/bin/Release/net10.0-windows10.0.22621.0/Notchle.Windows.exe
```

CI (`.github/workflows/windows.yml`) builds and tests on `windows-latest`, renders the UI
snapshots, and uploads them together with a self-contained single-file `Notchle.Windows.exe`
(win-x64). A `macos-latest` job runs the Swift tests and `windows/scripts/test.sh`.

## Command line

| | |
| --- | --- |
| *(none)* | Run the game. Only one instance runs at a time; launching again brings up the island. |
| `--ui-demo [...]` | The island demo (fake state, no audio). |
| `--ui-snapshots <dir>` | Render every island screen to PNG in `<dir>` and exit (exit code 0 on success). |

## Tray menu

- **New link…**: go back to the link field and show the island.
- **Connect Spotify…**: sign in for full tracks (see below).
- **Reset progress**: after a confirmation, cleared songs can come back.
- **Start with Windows**: toggles `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Notchle`.
- **Quit Notchle**: pauses the song, then exits.

Progress and settings live in `%APPDATA%\Notchle\progress.json`.

## Players

### 30-second previews (default)

Plays `Track.PreviewUrl` with `Windows.Media.Playback.MediaPlayer`. It needs no login. Its
`CommandManager` is off, so the song never shows up in the Windows media flyout, on the lock
screen, or under the media keys. Tracks without a preview clip report "no preview".
"Keeps playing" after a correct guess stops when the ~30 s clip ends. **Restart song** (↺ or
Ctrl+Shift+R on the answer screen) seeks the clip back to 0:00 and plays it to the end.

### Spotify Connect (full tracks, optional)

Uses the Spotify Web API to control the Spotify desktop app on this PC.

> **Spoiler warning:** Spotify's own app shows the playing track in its window *and* in the
> Windows media flyout (volume keys, Win+A), which reveals the answer. That is why previews
> are the default.

Requirements:

1. Spotify **Premium**, and the Spotify desktop app running on this PC and signed in.
2. Your own Spotify developer app: <https://developer.spotify.com/dashboard> → Create app.
   - Redirect URI: `http://127.0.0.1/callback`. Spotify rejects `localhost`. For a loopback
     IP you register no port, and Notchle picks a free one on each sign-in.
   - APIs used: Web API.
   - Since February 2026, a new Development Mode app allows 5 users. Add your account
     under *User Management*.
3. Tray → **Connect Spotify…**, then paste the app's client id. The browser opens Spotify's
   consent page (scopes `user-modify-playback-state user-read-playback-state`). After you
   approve, Notchle switches to Spotify Connect.

How it works: Authorization Code with PKCE (no client secret). The tokens are stored in
`%APPDATA%\Notchle\spotify-tokens.bin`, encrypted with DPAPI for the current Windows user.
Each snippet runs these steps:

1. `GET /v1/me/player/devices` finds this PC's Spotify app (type `Computer`, named after the PC).
2. If that device is not active, `PUT /v1/me/player` transfers playback to it.
3. `PUT /v1/me/player/play` sends the track URI and `position_ms`.
4. `GET /v1/me/player` is polled until `progress_ms` reaches the end of the snippet.
5. `PUT /v1/me/player/pause` stops it.

**Restart song** (↺ or Ctrl+Shift+R on the answer screen) runs steps 1–3 with `position_ms` 0
and never pauses. **Replay snippet** (the same button while guessing) is just the snippet again.

These errors get their own messages: 401 means signed out or expired (the token is
refreshed once first). 403 means Premium or user management. 404 means the Spotify app
is not open. 429 means rate limited.

Status: implemented and tested against a fake Spotify, but **never run against the real
service**. Treat it as unverified until someone connects a real account.

## Snippet timing

Both macOS players reported "playing" about 250–300 ms before the audio actually moved,
so a wall-clock sleep made every snippet short. Both Windows players time snippets with
`SnippetTimer` (Notchle.Core):

1. Wait until the player's own position has moved past the start.
2. Sleep in short slices until that position reaches the target.
3. Pause.

If the position stops moving, the snippet fails as stalled. Cancelling a snippet (a guess
while it plays) pauses right away and throws `OperationCanceledException`.
`AppCoordinator` runs playback strictly in order: a new operation cancels the running one
and waits for it to finish before it starts. So a restart (or a replayed snippet) always starts
after the cancelled snippet has paused, and that pause can't cut it off.

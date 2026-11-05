# Claude Tray

A system-tray app for **Claude Code** on Linux and Windows: your background sessions (which
ones need input, which are done) and where you stand on your plan — how much of each limit is
used, when it resets, whether you're on pace to run out, and how many tokens you used today
and this week.

Version 2 is a rewrite: the KDE Plasma widget (v1, still on the `v1.x` tags) became a standalone
app that runs on any Linux desktop with a system tray and on Windows.

C# / .NET 10 with [Avalonia](https://avaloniaui.net) 12 for the UI. One self-contained
executable per platform; no .NET install needed on the target machine.

## What it shows

- **Tray icon** with badges: current 5-hour session usage in percent (bottom right), coloured
  by your highest limit (green, orange from 70 %, red from 90 %), and the number of background
  sessions waiting for input (top left). Dimmed while Claude Code isn't running.
- **Left click** opens the panel:
  - **Left column — background sessions**: *Needs input*, *Working*, *Completed* (newest 10,
    *Show all* for the rest) and *Failed*, each with its last status line or result and how long
    ago it changed. **Click** a session to open it in a terminal (`claude attach`), or raise its
    window if it's already open. **✕** then **Remove** drops it from the list (`claude rm`; the
    conversation log is kept, `claude --resume` still finds it).
  - **Right column — usage**: *Limits* (meter + time to reset), *Tokens by day* (last 7 days),
    *Tokens by model* (today, with the input / output / cache split), *Pace* (straight-line
    projection to each reset) and *Model* buttons.
  - Footer: **Refresh**, **Restart** (tmux only), **Open / Start Claude Code**.
- **Other agents**: when [OpenCode](https://opencode.ai) or
  [Hermes Agent](https://github.com/NousResearch/hermes-agent) is installed, the panel gets a tab
  for each next to *Claude Code*. A tab shows that agent's sessions from the last 7 days (click
  one to open it again in a terminal), its models (default, backup / other configured models,
  and where they run) and its tokens by day and by model. Read-only: models are changed in the
  agents themselves. The footer button opens the agent of the selected tab.
- **Right click** (menu): Open Claude Code, the usage panel, Usage on claude.ai, Refresh,
  Settings file, Quit.
- Terminal summary: `claude-tray --text`; other agents: `claude-tray --agents`
- **Updates**: when a new release is out, the panel says so; **Update** downloads it from GitHub,
  checks it against the release's SHA256SUMS, runs its installer and restarts the app. Turn the
  check off in *Settings → About*. From a terminal: `claude-tray --update`.

### Model buttons

- With `tmuxTarget` set (Linux, Claude Code running in tmux): the button types `/model <id>`
  into that running session, and **Restart** restarts it in place, resuming the same
  conversation (e.g. after an update).
- Otherwise (always on Windows): the button sets `"model"` in Claude Code's user
  `settings.json`, the model every **new** session starts with. **Account default** removes
  it. The first change backs the file up to `settings.json.claude-tray.bak`.

## Where the data comes from

| What | Source |
|---|---|
| Limits | The endpoint Claude Code's `/usage` uses, called with Claude Code's own login (`.credentials.json`), at most every 5 minutes. The login is only read, never refreshed. Not a documented public API; it may change. |
| Tokens | Claude Code's session logs on **this computer** (`projects/**/*.jsonl`), de-duplicated per message, by local day. Only changed files are re-read. Limits are account-wide; token counts are per computer (the usage endpoint reports only percentages). |
| Sessions | `claude agents --json --all`, plus `jobs/<id>/state.json` for the status line and result. |
| OpenCode | Its database `~/.local/share/opencode/opencode.db` (opened read-only): tokens of every reply plus the session totals, sessions; models from `~/.config/opencode/opencode.json(c)`. `XDG_DATA_HOME` / `XDG_CONFIG_HOME` are honoured. |
| Hermes | Its database `~/.hermes/state.db` (read-only; `HERMES_HOME` is honoured): tokens per session and model, counted on the day the session was last active; models from `config.yaml` (`model:` and `fallback_providers:`). |

`CLAUDE_CONFIG_DIR` is honoured.

## Install

### Linux

```sh
./build.sh linux-x64                 # or skip: packaging/linux/install.sh builds too
packaging/linux/install.sh           # --no-autostart to not start it at login
```

Installs to `~/.local/lib/claude-tray` (+ `~/.local/bin/claude-tray`), an app-menu entry and an
autostart entry, then starts it. Remove with `packaging/linux/uninstall.sh`.

The tray icon uses StatusNotifierItem: KDE Plasma and most desktops show it; GNOME needs the
AppIndicator extension. On Plasma the panel opens next to the mouse pointer (placed by KWin);
elsewhere next to the panel / taskbar.

### Windows

Copy `dist\win-x64` to the machine, then in PowerShell:

```powershell
.\install.ps1                        # -NoAutostart to not start it at sign-in
```

Installs to `%LOCALAPPDATA%\Programs\ClaudeTray`, adds a Start-menu shortcut and a sign-in
entry (HKCU Run), then starts it. Remove with `.\uninstall.ps1`. No admin rights needed.

Sessions open in Windows Terminal (`wt.exe`) when it's installed, else in a console window.

## Configure

`config.json` — `~/.config/claude-tray/` on Linux, `%APPDATA%\ClaudeTray\` on Windows. Written
with the defaults on first start; tray menu → *Settings file…* opens it. Restart the app after
editing.

| Key | Default | What it does |
|---|---|---|
| `openCommand` | *(empty)* | What *Open Claude Code* runs (through `sh -c` on Linux). Empty = a terminal running `claude` in your home folder |
| `sessionCommand` | *(empty)* | Terminal a session opens in; `{claude}`, `{id}`, `{name}`, `{title}` are filled in. Empty = picked for your terminal (konsole, gnome-terminal, kgx, xfce4-terminal, alacritty, kitty, wezterm, foot, xterm; Windows Terminal or a console on Windows) |
| `tmuxTarget` | *(empty)* | Linux: tmux socket and pane of your Claude Code, e.g. `-L claude -t claude`. Enables live model switching and **Restart** |
| `claudeCommand` | *(empty)* | The Claude Code executable. Empty = `~/.local/bin/claude` (native installer), else `claude` on PATH |
| `models` | current models | The model buttons: `{ "name": …, "id": … }` |

Log: `~/.cache/claude-tray/claude-tray.log` (Linux), `%LOCALAPPDATA%\ClaudeTray\cache\claude-tray.log` (Windows).

## Build

Needs the .NET 10 SDK.

```sh
dotnet test                          # unit tests
./build.sh                           # dist/linux-x64 and dist/win-x64 (self-contained, single file)
dotnet run --project src/ClaudeTray  # run from source
```

| Path | |
|---|---|
| `src/ClaudeTray.Core` | Everything but the UI: limits, tokens, sessions, terminals, tmux, KWin, settings.json |
| `src/ClaudeTray` | Avalonia app: tray icon, panel, `--text` mode |
| `tests/ClaudeTray.Tests` | xUnit tests for the Core parsing and formatting |
| `packaging/` | Per-user install / uninstall scripts for Linux and Windows |

## Support

Questions, bugs and ideas: [open an issue](https://github.com/JamesStuder/claude-tray/issues/new)
(also *Settings → Support* in the app). Claude Tray is free and open source (MIT), with nothing held back.

## Upgrading from the v1 Plasma widget

Remove the old widget from your panel (right-click it → Remove), run `./uninstall.sh` from your v1
checkout if you like, then install v2 as above. v1's `~/.config/claude-usage/config.toml` isn't
read; the matching v2 keys are `openCommand`, `tmuxTarget`, `claudeCommand`, `sessionCommand` and
`models` in `config.json`.

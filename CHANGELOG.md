# Changelog

## Unreleased (3.0.0)

- Tray icon: the Claude session-% badge is gone (and the icon is no longer dimmed when Claude Code isn't running). The top-left badge now counts sessions waiting for you across all agents: Claude Code needs-input, OpenCode pending permissions, Goose tool confirmations, Codex and Qwen questions. Waiting agent sessions show first in their tab with an orange **?**. The tooltip says which agents are waiting. Agent data refreshes every 15 s (3 s while the panel is open).

- **Renamed from Claude Tray to Token Perch** (it covers more agents than Claude Code now): command `tokenperch`, repo `JamesStuder/tokenperch`, settings in `~/.config/tokenperch` / `%APPDATA%\TokenPerch`. On first start the Claude Tray settings are copied over; the installers remove a Claude Tray install (autostart, menu entry, binary).

- Other agents: tabs for OpenCode, Hermes Agent, Codex CLI, Goose, Pi and Qwen Code once they have been used on this computer (a newly installed one appears within a minute), each with its sessions (click to resume in a terminal; ✕ → Remove hides one from the list without touching the agent's history), its models (default and backup / other configured ones) and its tokens by day and model, read from the agent's own database and config. `claude-tray --agents` prints the same in a terminal.

- Windows: the panel opens next to the tray icon, placed from Windows' own monitor info at click time (it opened off screen after a Remote Desktop window was resized).
- Token sections say *this computer*: they come from local session logs, while limits are account-wide.
- Updates: checks GitHub for a new release (a minute after start, then every 6 hours); the panel shows it with **Update** (downloads the release for your platform, checks it against SHA256SUMS, runs its installer and restarts) and **What's new**. Off switch and *Check now* in Settings → About. `claude-tray --update` does the same from a terminal.
- Settings → Support opens a GitHub issue; the pay-what-you-want link is gone for now.

## 2.0.0 — 2026-10-20

Rewrite as a standalone cross-platform app (C#/.NET 10 + Avalonia) for Linux and Windows; replaces the
KDE Plasma widget (1.x).

- Settings window (tray menu → Settings…, or ⚙ in the panel): Claude Code login status, log in again / log out; theme and custom panel colours (background, text, buttons, button text); support (optional, pay what you want); about with the settings file and log.

- Tray icon with session-usage and needs-input badges; panel with background sessions (open,
  raise, remove) and usage (limits, tokens by day and model, pace).
- Model buttons: live switch through tmux (Linux), else the default model in Claude Code's
  settings.json (all platforms).
- Restart in tmux, resuming the same conversation.
- Plasma: panel placed next to the pointer by KWin; session windows raised by KWin.
- Windows: sessions in Windows Terminal, raised by window title.
- `claude-tray --text` terminal summary.
- Self-contained single-file builds and per-user installers for Linux and Windows.

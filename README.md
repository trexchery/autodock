# autodock

> Every Claude Code account you own, in one tray icon: usage at a glance, one-click switching.

[![Release](https://img.shields.io/github/v/release/trexchery/autodock)](../../releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-black)](LICENSE)
![Windows 11](https://img.shields.io/badge/Windows-11-black)

<img src="assets/autodock.png" width="336" alt="autodock dashboard: three accounts with 5-hour and weekly usage meters">

## Why autodock?

If you use more than one of your own Claude Code accounts, you end up guessing which one has quota left
and logging in and out to move between them. autodock shows all of them side by side and switches the
active login without stopping anything that is running. Everything stays on your PC.

## Features

- **Usage at a glance.** 5-hour, weekly and per-model limits with reset times, for every account.
- **One-click switching.** Running sessions are never interrupted.
- **Pinned terminals.** Open Claude Code locked to one account, unaffected by switches.
- **Optional auto-switch.** Off by default. It moves to another account near a threshold you set.
- **Local and encrypted.** Backups are encrypted with DPAPI and folders are locked to your user. No telemetry, proxy or cloud sync.

## Quickstart

1. Install [Claude Code](https://docs.anthropic.com/en/docs/claude-code) and check that `claude` runs in a terminal.
2. Download `autodock-win-x64.zip` from [Releases](../../releases) and unzip it into a folder you keep, for example `%LOCALAPPDATA%\Programs` (it creates `autodock\`).
3. Run `autodock.exe`.

The icon appears in the tray and your current login is added automatically. No admin rights or .NET
install needed. The exe is unsigned, so SmartScreen may warn on first run (*More info → Run anyway*).

## Usage

| | |
|---|---|
| **See usage** | Click the tray icon. Meters turn amber at 80 % and red at 95 %. |
| **Details** | Click a row to see every limit and when it resets. |
| **Switch** | Expand a row → **Switch**, or right-click the tray → *Switch to*. |
| **Add an account** | **+** runs the official `claude auth login` in a temporary profile. Open the link in a private browser window. |
| **Pinned terminal** | **Terminal** in an expanded row. |
| **Keep on screen** | The pin icon turns the dashboard into a movable desktop window. |
| **Start with Windows** | ⚙ Settings. |

Right-click any row for the rest: rename, exclude from auto-switch, log in again, remove.

## How switching works

Switching replaces Claude Code's default login file. A response already in progress finishes on the old
account, and default-profile sessions use the new one from their next message. The first message after a
switch may cost extra quota while the cache rebuilds, so this is not a seamless hand-off. Auto-switch never
bypasses a limit: it polls conservatively, has a 5-minute cooldown, and skips excluded or busy accounts.

## Security

The only network calls go to Anthropic's OAuth and usage endpoints. Output and logs pass through a token
redactor. Claude Code's own active login file is not encrypted; that's how Claude Code works on Windows.
Details and reporting are in [SECURITY.md](SECURITY.md).

## Build from source

```powershell
# needs uv and the .NET 8 SDK
pwsh dock\build.ps1   # engine, app, tests, zip -> dist\
```

## Limitations

- Windows only.
- Token rotation relies on Claude Code's lock-file protocol (reverse-engineered from 2.1.218). A future Claude Code release could break it.

## Contributing

Issues and PRs are welcome. Please run `dock\build.ps1` before opening a PR.

## License

MIT. Built on [claude-swap](https://github.com/realiti4/claude-swap) by Onur Cetinkol (engine docs:
[docs/claude-swap.md](docs/claude-swap.md)). autodock additions © trexchery.

autodock is an independent project, not affiliated with or endorsed by Anthropic. "Claude" is a trademark
of Anthropic. You're responsible for following Anthropic's [Consumer Terms and Usage Policy](https://www.anthropic.com/legal)
for every account you use.

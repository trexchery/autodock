# autodock

A Windows 11 tray app for people who use several of their own Claude Code accounts.
It shows every account's usage at a glance, switches in one click, opens terminals
pinned to one account, and can optionally move you to another account before you hit a limit.

<img src="assets/autodock.png" width="336" alt="autodock dashboard: three accounts with 5-hour, weekly and per-model usage bars and reset times">

> autodock is an independent open-source project. It is not affiliated with, endorsed by,
> or supported by Anthropic. "Claude" and "Claude Code" are trademarks of Anthropic.
> You are responsible for following Anthropic's [Consumer Terms and Usage Policy](https://www.anthropic.com/legal)
> for every account you use with it.

autodock is built on [claude-swap](https://github.com/realiti4/claude-swap) v0.27.2 by
Onur Cetinkol (MIT), which does the OAuth, quota, locking and switching work. autodock adds the
Windows tray app, isolated enrollment, and DPAPI-encrypted credential backups.

## Install

1. Install [Claude Code](https://docs.anthropic.com/en/docs/claude-code) and make sure `claude` runs in a terminal.
2. Download `autodock-win-x64.zip` from [Releases](../../releases) and unzip it to a folder you keep, for example `%LOCALAPPDATA%\Programs\autodock`.
3. Run `autodock.exe`. The icon appears in the tray. Pin it via *Taskbar settings → Other system tray icons*.
4. Optional: open ⚙ Settings and tick *Start autodock when I sign in to Windows*.

You don't need admin rights or a .NET install. The exe is unsigned, so SmartScreen may warn on
first run (*More info → Run anyway*).

To uninstall, untick start-up, exit the app, and delete the folder. Your accounts stay in
`%USERPROFILE%\.claude-swap-backup`; delete that folder too if you want them gone.

## Use

| Action | How |
|---|---|
| See usage | Left-click the tray icon. Each account is one row with its 5-hour and weekly meters; the green dot marks the active account. Meters turn amber at 80 % and red at 95 %. |
| Details | Click a row to expand it: every window (including per-model limits) with its reset time. |
| Switch | Expand a row and click **Switch**, or right-click a row, or right-click the tray → *Switch to*. |
| Pinned terminal | **Terminal** in an expanded row opens Claude Code as that account in its own profile. Switches never affect it. |
| Add an account | **+** in the header. A console runs the official `claude auth login` in a temporary profile. Open the login link in a **private browser window** so you don't approve the account you're already signed in with. If a code is shown, paste it into the console (nothing appears while you paste) and press Enter. Your current login and running sessions are not touched. On first use, the login already on this PC is added too. |
| Keep it on screen | The pin icon keeps the dashboard open as a movable window (drag it by the header); it reopens there after a restart. |
| Auto-switch | The switch at the bottom (off by default). Threshold, strategy and per-model limits are in Settings. |
| Everything else | Right-click a row (or **More**): terminal in a folder, pin a folder, exclude from auto-switch, rename, log in again, remove. |

## What switching does and doesn't do

- Switching replaces the default login file (`%USERPROFILE%\.claude\.credentials.json`). It never stops a process.
- A response that is already running finishes on the old account. Default-profile sessions pick up the new account on their next message.
- The first message after a switch may use extra quota while the conversation cache rebuilds. This is not a seamless hand-off.
- Pinned terminals keep their account until you close them.
- Auto-switch never bypasses a limit. It runs one engine check per refresh tick (default 3 minutes), with per-account backoff, a 5-minute cooldown and hysteresis. It skips excluded accounts, accounts with a dead login, API-key accounts, and accounts busy in a pinned terminal.

## Security and privacy

- **Local only.** No telemetry, no proxy, no cloud sync, no browser-cookie access, no auto-update. The only network calls go to Anthropic's OAuth token, profile and usage endpoints.
- **Encrypted backups.** Stored per-account logins are encrypted with Windows DPAPI (your user, this machine), as `dpapi1:` files under `.claude-swap-backup\credentials`.
- **Locked-down folders.** On every start, `.claude-swap-backup` and `%LOCALAPPDATA%\autodock` are restricted to your user and SYSTEM.
- **Safe writes.** All credential writes are atomic. Refresh tokens rotate under Claude Code's own lock files, so a switch never races a refresh.
- **No leaks.** Anything autodock displays or logs passes through a token redactor. Temporary enrollment profiles are deleted right after import.
- **What it can't protect against.** Malware running as your Windows user could read your logins, as it could for Claude Code itself. Claude Code's own active login file is not encrypted; that is how Claude Code works on Windows.

Found a vulnerability? See [SECURITY.md](SECURITY.md).

## Build from source

```powershell
# needs: uv, .NET 8 SDK
pwsh dock\build.ps1          # engine + app + both test suites + zip -> dist\
```

The engine's own CLI (`engine\cswap.exe`) is documented in [docs/claude-swap.md](docs/claude-swap.md).
`AUTODOCK_ENGINE=<path to cswap.exe>` points the app at a different engine build.

## Known limitations

- Windows only.
- Default-profile sessions follow the active account. To keep a conversation on one account, run it in a pinned terminal.
- Rotating the active account's token relies on Claude Code's lock-file protocol, which claude-swap reverse-engineered from Claude Code 2.1.218. A future Claude Code change could break it.
- Each account needs a browser sign-in to enroll.

## License

MIT. See [LICENSE](LICENSE). The claude-swap engine is © Onur Cetinkol; autodock additions are © trexchery.

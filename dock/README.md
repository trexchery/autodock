# Claude Dock

A Windows 11 tray app for managing your own Claude Code accounts. It is built on
[claude-swap](https://github.com/realiti4/claude-swap) v0.27.2 (MIT, © Onur Cetinkol),
which does the OAuth, quota, locking and switching work. The tray app is a thin
WinForms front end that calls the bundled `engine\cswap.exe`.

## Install (portable)

1. Install Claude Code and make sure `claude` runs in a terminal.
2. Unzip `ClaudeDock-win-x64.zip` to a folder you keep, for example `%LOCALAPPDATA%\Programs\ClaudeDock`.
3. Run `ClaudeDock.exe`. The icon appears in the tray. Pin it via *Taskbar settings → Other system tray icons*.
4. Optional: open ⚙ Settings and tick *Start Claude Dock when I sign in to Windows*.

You don't need admin rights or a .NET install. To uninstall, untick start-up, exit the app,
and delete the folder. Your accounts stay in `%USERPROFILE%\.claude-swap-backup`; delete
that folder too if you want them gone.

## Use

| Action | How |
|---|---|
| Add an account | **+ Add account**. A console runs the official `claude auth login` in a temporary profile. Sign in with your browser. Your current login and running sessions are not touched. On first use, the login already on this PC is added too. |
| See usage | Left-click the tray icon. Each account shows its 5-hour, weekly and per-model windows with reset times. The tray dot shows the active account's highest window: green below 70 %, amber below 90 %, red above. A white centre means auto-switch is on. |
| Switch | **Switch** on a card, or right-click the tray → *Switch to*. |
| Pinned terminal | **Terminal** opens a console running Claude Code as that account in its own profile (`cswap run N`). Switches never affect it. *••• → Pin a folder* binds a folder to an account, so `cswap run` there always uses that account. |
| Auto-switch | Tick *Auto-switch near limit*. Threshold, strategy and per-model limits are in Settings. |
| Exclude an account | *••• → Exclude from auto-switch*. |
| Repair a dead login | *••• → Re-enroll*. |

## What switching does and doesn't do

- Switching replaces the default login file (`%USERPROFILE%\.claude\.credentials.json`). It never stops a process.
- A response that is already running finishes on the old account. Default-profile sessions pick up the new account on their next message.
- The first message after a switch may use extra quota while the conversation cache rebuilds. This is not a seamless hand-off.
- Pinned terminals keep their account until you close them.
- Auto-switch runs one engine check per refresh tick (default 3 minutes). The engine applies its own per-account backoff, cooldown (5 min) and hysteresis. It skips accounts you've excluded, accounts with a dead login, API-key accounts, and accounts busy in a pinned terminal.

## Security

- Everything stays local. There is no proxy, telemetry, cloud sync or browser-cookie access. The engine only calls Anthropic's OAuth token, profile and usage endpoints.
- Dormant per-account backups are encrypted with DPAPI (current user, this machine). They are stored as `dpapi1:` files under `.claude-swap-backup\credentials`. Older base64 files are still readable and are re-encrypted on their next write.
- On every start, the app limits `.claude-swap-backup` and `%LOCALAPPDATA%\ClaudeDock` to your user and SYSTEM. Inherited ACLs are removed.
- Writes are atomic (temp file, then rename), including session profiles.
- Refresh tokens are rotated under Claude Code's own lock files, so a switch never races a refresh.
- Anything the app displays passes through a token redactor first.
- Enrollment profiles live in `%LOCALAPPDATA%\ClaudeDock\enroll` and are deleted right after import.
- Leave the "Export" feature in the CLI unused unless you encrypt the output: export files are plaintext.

## Build from source

```powershell
# needs: uv, .NET 8 SDK
pwsh dock\build.ps1          # engine + app + both test suites + zip -> dist\
```

`CLAUDE_DOCK_ENGINE=<path to cswap.exe>` points the app at a different engine.

## Known limitations

- Default-profile sessions follow the active account. To keep a conversation on one account, run it in a pinned terminal.
- Rotating the active account's token relies on Claude Code's lock-file protocol, which claude-swap reverse-engineered from Claude Code 2.1.218.
- Enrollment needs a browser sign-in for each account. There is no unattended enrollment.
- The `.exe` is unsigned, so SmartScreen may warn on first run (*More info → Run anyway*).

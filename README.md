<div align="center">

<img src="assets/autodock.png" width="336" alt="The autodock dashboard: one expanded account showing its 5-hour, weekly and Opus weekly limits with reset times, and two collapsed accounts with usage meters">

<h1>autodock</h1>

<p>All your Claude Code, Codex and Grok Build accounts in one Windows tray icon.<br>
See what's left on each, switch in one click, and keep working.</p>

<p>
<a href="https://github.com/trexchery/autodock/releases/latest"><strong>Download for Windows</strong></a>
&nbsp;·&nbsp;
<a href="https://github.com/trexchery/autodock/issues/new/choose">Report a bug</a>
&nbsp;·&nbsp;
<a href="SECURITY.md">Security</a>
</p>

</div>

---

## About

If you have more than one Claude Code subscription, the friction is always the same: you don't know which
account still has quota until you hit a limit, and moving between them means logging out and back in.

autodock sits in the tray and answers both. It shows every account's 5-hour, weekly and per-model usage
side by side, and switches Claude Code's active login without stopping anything that is running. If you also
use OpenAI's Codex CLI or xAI's Grok Build, their accounts appear in the same dashboard. It keeps everything
on your PC: no servers, no telemetry, no browser cookies.

autodock is built on [claude-swap](https://github.com/realiti4/claude-swap) by Onur Cetinkol, which handles
the OAuth, quota and switching logic. autodock adds the native Windows app, isolated account enrollment
and encrypted credential storage.

## Features

- **Usage at a glance.** Slim 5-hour and weekly meters for every account. Click a row for each limit,
  including per-model limits, and when it resets. Meters turn amber at 80 % and red at 95 %.
- **One-click switching.** Changes the account Claude Code uses without interrupting running sessions.
- **Pinned terminals.** Open Claude Code locked to one account in its own profile. Switching never affects it.
- **Safe enrollment.** New accounts sign in through the official `claude auth login` in a throwaway profile,
  so your current login is never touched.
- **Codex and Grok Build too.** Detected automatically and shown in their own section: Codex 5-hour and weekly
  limits (whichever your plan has), Grok's weekly or monthly pool.
- **Optional auto-switch.** Off by default, Claude accounts only. Moves to the account with the most headroom before you reach a
  threshold you choose.
- **Stays out of the way.** A dot in the tray, a dashboard that follows the Windows light or dark theme,
  and an optional pinned window that stays on your desktop.

## Getting started

### Requirements

- Windows 11, 64-bit
- [Claude Code](https://docs.anthropic.com/en/docs/claude-code), with `claude` working in a terminal

No admin rights, Python or .NET install needed.

### Install

1. Download `autodock-win-x64.zip` from the [latest release](https://github.com/trexchery/autodock/releases/latest).
2. Unzip it into a folder you keep, for example `%LOCALAPPDATA%\Programs`. This creates an `autodock` folder.
3. Run `autodock\autodock.exe`.

The icon appears in the tray and the account you're signed in with is added automatically. To keep the icon
visible, pin it in *Settings → Personalization → Taskbar → Other system tray icons*. To start autodock with
Windows, open ⚙ in the dashboard and turn on *Start with Windows*.

The executable is not code-signed yet, so SmartScreen may warn on first launch. Choose *More info → Run anyway*.
Each release ships a `.sha256` file if you want to verify the download:

```powershell
Get-FileHash .\autodock-win-x64.zip -Algorithm SHA256
```

### Uninstall

Turn off *Start with Windows*, exit autodock from the tray menu, and delete its folder. Your saved accounts
live in `%USERPROFILE%\.claude-swap-backup` and app settings in `%LOCALAPPDATA%\autodock`. Delete those too
to remove everything.

## Usage

| To | Do this |
|---|---|
| See usage | Click the tray icon. The green dot marks the active account. |
| See limits and resets | Click an account row to expand it. |
| Switch accounts | Expand a row and click **Switch**, or right-click the tray icon → *Switch to*. |
| Add an account | Click **+** (it asks which CLI when several are installed). A console runs the official login. Open the link in a **private browser window** so you approve the right account, then paste the code if one is shown. Nothing appears while you paste; press Enter. |
| Work on one account | Click **Terminal** in an expanded row. That window stays on its account until you close it. |
| Keep it on screen | Click the pin icon. The dashboard becomes a movable window and reopens where you left it. |
| Everything else | Right-click a row: open a terminal in a folder, rename, exclude from auto-switch, sign in again, remove. |

### Auto-switch

Turn it on with the switch at the bottom of the dashboard. In ⚙ Settings you choose:

- **Switch at:** the share of the 5-hour or weekly limit that triggers a switch (default 90 %).
- **Next account:** the one with the most quota left, or the one whose weekly limit resets soonest.
- **Also watch models:** include per-model limits such as Opus weekly.
- **Refresh every:** how often usage is checked (default 3 minutes, minimum 1).

Auto-switch never bypasses a limit. It checks once per refresh, backs off per account, waits at least
5 minutes between switches, and skips accounts that are excluded, signed out, using an API key, or busy in a
pinned terminal.

## How switching works

Switching replaces Claude Code's default login file, `%USERPROFILE%\.claude\.credentials.json`. It never
stops a process.

- A response that is already running finishes on the old account.
- Sessions in the default profile use the new account from their next message.
- The first message after a switch can use extra quota while the conversation cache rebuilds. This is not a
  seamless hand-off.
- Pinned terminals keep their account until you close them.

For Codex and Grok, switching rewrites `%USERPROFILE%\.codex\auth.json` or `%USERPROFILE%\.grok\auth.json`.
Running Codex or Grok sessions keep their old account until restarted; new sessions use the new one. autodock
never refreshes their tokens itself, because those tokens are single-use.

## Security and privacy

- **Local only.** No telemetry, proxy, cloud sync, auto-update or browser-cookie access. The only network
  requests go to Anthropic's OAuth, profile and usage endpoints, and to the usage endpoints the Codex and Grok
  CLIs themselves use.
- **Encrypted at rest.** Saved logins are encrypted with Windows DPAPI, tied to your Windows user on this PC.
- **Locked-down folders.** On every start, `.claude-swap-backup` and `%LOCALAPPDATA%\autodock` are restricted
  to your user and SYSTEM.
- **Safe writes.** Credential writes are atomic, and token refreshes use Claude Code's own lock files, so a
  switch never races a refresh.
- **No leaks.** Everything autodock shows or logs passes through a token redactor. Temporary sign-in profiles
  are deleted right after import.

What it can't protect against: malware running as your Windows user can read your logins, just as it could
read Claude Code's. Claude Code's own active login file is unencrypted; that is how Claude Code works on Windows.

To report a vulnerability, see [SECURITY.md](SECURITY.md). Please don't open a public issue.

## Building from source

You need [uv](https://docs.astral.sh/uv/), the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
and PowerShell 7.

```powershell
git clone https://github.com/trexchery/autodock.git
cd autodock
pwsh dock\build.ps1
```

This builds the engine and the tray app, runs the Python and C# test suites, and writes
`dist\autodock\autodock.exe` and `dist\autodock-win-x64.zip`. Add `-SkipTests` for a faster build.

| Path | What's there |
|---|---|
| `dock/Autodock` | The tray app (.NET 8 WinForms) |
| `dock/Autodock.Tests` | App tests, including an end-to-end engine test with synthetic credentials |
| `src/claude_swap` | The engine, bundled as `engine\cswap.exe` |
| `tests` | Engine tests |

The engine also works on its own from a terminal; see [docs/claude-swap.md](docs/claude-swap.md). Set
`AUTODOCK_ENGINE` to the path of a different `cswap.exe` to point the app at another engine build.

## Limitations

- Windows only.
- Sessions in the default profile follow the active account. To keep a conversation on one account, run it in a
  pinned terminal.
- Token rotation for the active account relies on Claude Code's lock-file protocol, which claude-swap
  reverse-engineered from Claude Code 2.1.218. A future Claude Code release could change it.
- Every account needs one browser sign-in to enroll.
- Codex and Grok usage come from the same undocumented endpoints their CLIs use, so a CLI update could change
  them. A dormant Codex or Grok login that expires shows as expired until you switch to it or sign in again.
- Grok Build support is tested with synthetic data only so far.

## Contributing

Bug reports and pull requests are welcome. Please run `pwsh dock\build.ps1` before opening a pull request, and
never include real tokens or credential files in issues, tests or logs.

## License

Released under the [MIT License](LICENSE). The claude-swap engine is © Onur Cetinkol; autodock additions are
© trexchery.

autodock is an independent project. It is not affiliated with, endorsed by or supported by Anthropic.
"Claude" and "Claude Code" are trademarks of Anthropic. You are responsible for following Anthropic's
[Consumer Terms and Usage Policy](https://www.anthropic.com/legal) for every account you use with it. "OpenAI" and "Codex" are trademarks of OpenAI, and "Grok" and "xAI" are
trademarks of X.AI Corp; autodock is not affiliated with either. Follow OpenAI's
[Terms of Use](https://openai.com/policies/terms-of-use) and xAI's
[Terms of Service](https://x.ai/legal/terms-of-service) for those accounts.

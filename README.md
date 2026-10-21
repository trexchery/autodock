# clautoswap

multi-account switcher for Claude Code, Codex, and Grok Build. live usage, reset timers, and one-click switching.

**[download for Windows](https://github.com/trexchery/clautoswap/releases/latest)**

<p align="center">
  <img src="assets/clautoswap.png" width="420" alt="clautoswap tray dashboard listing Claude, Codex, and Grok Build accounts with 5-hour and weekly usage">
</p>

## features

- 5-hour and weekly usage limits and reset times for every account in one tray dashboard
- one-click account switching without closing running sessions
- pinned Claude terminals and optional auto-switching between Claude accounts
- **continue in...** starts a new CLI session with local handoff notes after an interruption
- project folders: assign a folder to an account, and terminals opened there run as that account without changing your login
- **preserve remote control** (on by default): a new terminal turns Claude remote control back on only if its last session used it
- optional quota notifications when a limit gets near, runs out, or resets
- **auto-prime** (off by default): tracks each Claude account's 5-hour window and, with one click in the tray menu, starts every fresh one early with a single silent Haiku "hi" (no window, nothing saved), so it also resets early. success is only claimed once a new usage reading shows the window started
- **agents** (off until you start them): work through a repository's open GitHub issues on your other Claude accounts, one reviewed pull request at a time, steered from a normal Claude Code terminal. give them a goal and a read-only director plans the steps, across your repositories, and calls it done only on evidence. never merges. [how it works](docs/agents.md)

## install

**Windows 11 (64-bit)** and [Claude Code](https://docs.anthropic.com/en/docs/claude-code) required. Codex and Grok Build optional.

1. download `clautoswap-win-x64.zip` from the [latest release](https://github.com/trexchery/clautoswap/releases/latest).
2. extract the zip and run `clautoswap\clautoswap.exe`.
3. open the tray icon and use **+** to add accounts through official CLI sign-in.

no installer or admin access required. unsigned releases may trigger Windows SmartScreen; a `.sha256` checksum is included with the download.

## good to know

- automatic switching is **off by default** and currently supports Claude accounts only.
- switching updates the default CLI login. running Codex/Grok sessions need restarting to use the newly selected account; pinned Claude terminals remain on their assigned account.
- Claude stops remote control when the signed-in account changes, and clautoswap never reconnects or types into a running session. pinned terminals keep their account, so their remote control is unaffected.
- **continue in...** notes are built locally and clautoswap sends nothing, but the new agent sends them to its provider like any prompt. notes are deleted after 7 days.
- saved credentials are encrypted locally with Windows DPAPI. a pinned Claude terminal holds a plaintext login only while it's open; clautoswap moves it back into the encrypted store when it closes. no telemetry, cloud sync, or browser-cookie access. [security policy](SECURITY.md).
- Grok Build support has only been tested with synthetic data so far.
- auto-prime is off by default and never sends on a schedule: each prime is one click of yours. that click doesn't make it allowed: Anthropic's consumer terms (§3) restrict automated or scripted access to a subscription, so whether to use it is your call. it skips excluded, API-key and paid-extra-usage accounts and any near a weekly or model limit, which makes a charge unlikely but can't rule one out. when it can't tell whether a prime went out, or can't record one, it stops rather than send again.

## build from source

requires [uv](https://docs.astral.sh/uv/), the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), and PowerShell 7.

```powershell
git clone https://github.com/trexchery/clautoswap.git
cd clautoswap
pwsh dock\build.ps1
```

built on [claude-swap](https://github.com/realiti4/claude-swap) by Onur Cetinkol. clautoswap additions by trexchery. [MIT license](LICENSE). independent project, not affiliated with Anthropic, OpenAI, or xAI; provider terms apply.

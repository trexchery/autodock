# autodock

multi-account switcher for claude code, codex, and grok build. live usage, reset timers, and one-click switching.

**[download for windows](https://github.com/trexchery/autodock/releases/latest)**

<p align="center">
  <img src="assets/autodock.png" width="420" alt="autodock tray dashboard listing claude, codex, and grok build accounts with 5-hour and weekly usage">
</p>

## features

- 5-hour and weekly usage limits and reset times for every account in one tray dashboard
- one-click account switching without closing running sessions
- pinned claude terminals and optional auto-switching between claude accounts
- **continue in...** starts a new cli session with local handoff notes after an interruption
- project folders: assign a folder to an account, and terminals opened there run as that account without changing your login
- **preserve remote control** (on by default): a new terminal turns claude remote control back on only if its last session used it
- optional quota notifications when a limit gets near, runs out, or resets

## install

**windows 11 (64-bit)** and [claude code](https://docs.anthropic.com/en/docs/claude-code) required. codex and grok build optional.

1. download `autodock-win-x64.zip` from the [latest release](https://github.com/trexchery/autodock/releases/latest).
2. extract the zip and run `autodock\autodock.exe`.
3. open the tray icon and use **+** to add accounts through official cli sign-in.

no installer or admin access required. unsigned releases may trigger windows smartscreen; a `.sha256` checksum is included with the download.

## good to know

- automatic switching is **off by default** and currently supports claude accounts only.
- switching updates the default cli login. running codex/grok sessions need restarting to use the newly selected account; pinned claude terminals remain on their assigned account.
- claude stops remote control when the signed-in account changes, and autodock never reconnects or types into a running session. pinned terminals keep their account, so their remote control is unaffected.
- **continue in...** notes are built locally and autodock sends nothing, but the new agent sends them to its provider like any prompt. notes are deleted after 7 days.
- saved credentials are encrypted locally with windows dpapi. a pinned claude terminal holds a plaintext login only while it's open; autodock moves it back into the encrypted store when it closes. no telemetry, cloud sync, or browser-cookie access. [security policy](SECURITY.md).
- grok build support has only been tested with synthetic data so far.

## build from source

requires [uv](https://docs.astral.sh/uv/), the [.net 8 sdk](https://dotnet.microsoft.com/download/dotnet/8.0), and powershell 7.

```powershell
git clone https://github.com/trexchery/autodock.git
cd autodock
pwsh dock\build.ps1
```

built on [claude-swap](https://github.com/realiti4/claude-swap) by onur cetinkol. autodock additions by trexchery. [mit license](LICENSE). independent project, not affiliated with anthropic, openai, or xai; provider terms apply.

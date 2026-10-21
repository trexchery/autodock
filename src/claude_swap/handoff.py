"""``cswap handoff --from <claude|codex|grok> --dir <project> [--json]``.

Builds local hand-off notes from the newest session transcript a CLI left for a
project folder, so a *different* agent can start a new session with that
context. The notes are built locally as a Markdown file under
``%LOCALAPPDATA%\\handoff\\notes`` and handoff sends nothing, but the target
agent reads them and sends them to its provider like any prompt. Unfinished
reasoning can't be transferred; this is a fresh start with notes, not a resume.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path

from claude_swap.json_output import SCHEMA_VERSION, error_envelope
from claude_swap.paths import get_backup_root, get_claude_config_home, get_default_claude_config_home

TRANSCRIPT_CAP = 6000
KEEP_DAYS = 7
SOURCES = ("claude", "codex", "grok")

# Same patterns as the tray's Engine.Redact, plus generic sk-/xai- keys, JWTs, and common
# GitHub/AWS/Slack/Google/Stripe keys and PEM private keys: the notes go to another vendor's agent.
_SECRET = re.compile(
    r"sk-ant-[A-Za-z0-9_\-]{6,}"
    r"|(?i:bearer)\s+[A-Za-z0-9._\-]{12,}"
    r"|(?i:\"(access|refresh|id)_?token\"\s*:\s*\"[^\"]*\")"
    r"|(?i:(access|refresh|id)_?token[\"']?\s*[=:]\s*(?:\"[^\"]*\"|'[^']*'|[^\s\"',;]+))"
    r"|(?i:(api_?key|secret|password)\w*[\"']?\s*[=:]\s*(?:\"[^\"]*\"|'[^']*'|[^\s\"',;]+))"
    r"|\b(sk|xai)-[A-Za-z0-9_\-]{16,}"
    r"|eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]*"
    r"|gh[pousr]_[A-Za-z0-9]{30,}"
    r"|github_pat_[A-Za-z0-9_]{20,}"
    r"|\b(?:AKIA|ASIA)[0-9A-Z]{16}\b"
    r"|xox[abprs]-[A-Za-z0-9-]{10,}"
    r"|AIza[0-9A-Za-z_\-]{30,}"
    r"|\b[rs]k_(?:live|test)_[A-Za-z0-9]{16,}"
)
# Applied first: a "secret_key=" prefix would otherwise eat the BEGIN line and leak the key body.
_PEM = re.compile(r"(?s)-----BEGIN [A-Z ]*PRIVATE KEY-----.*?(?:-----END [A-Z ]*PRIVATE KEY-----|\Z)")


class HandoffError(Exception):
    pass


def redact(text: str) -> str:
    return _SECRET.sub("[redacted]", _PEM.sub("[redacted]", text))


def _same_dir(a: str, b: str) -> bool:
    norm = lambda p: os.path.normcase(os.path.normpath(os.path.abspath(p))).rstrip("\\/")
    return norm(a) == norm(b)


def _newest(paths) -> Path | None:
    best = None
    for p in paths:
        try:
            m = p.stat().st_mtime
        except OSError:
            continue
        if best is None or m > best[0]:
            best = (m, p)
    return best[1] if best else None


def _read_jsonl(path: Path):
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            try:
                rec = json.loads(line)
            except (ValueError, RecursionError):
                continue
            if isinstance(rec, dict):
                yield rec


# -- discovery -----------------------------------------------------------------

def claude_project_dirs() -> list[Path]:
    """Every Claude ``projects/`` root: default, CLAUDE_CONFIG_DIR, and the
    per-account profiles ``cswap run N`` creates under ``<backup>/sessions``."""
    roots = [get_default_claude_config_home(), get_claude_config_home()]
    roots += sorted((get_backup_root() / "sessions").glob("*"))
    seen, out = set(), []
    for r in roots:
        p = r / "projects"
        if str(p) not in seen:
            seen.add(str(p))
            out.append(p)
    return out


def project_dir_name(project: str | Path) -> str:
    # Claude Code names the folder after the cwd with every non-alphanumeric char as '-'.
    return re.sub(r"[^A-Za-z0-9]", "-", os.path.abspath(project))


def find_claude(project: str) -> Path | None:
    encoded = project_dir_name(project)
    return _newest(f for root in claude_project_dirs() for f in (root / encoded).glob("*.jsonl"))


def codex_session_dirs() -> list[Path]:
    from claude_swap.providers import codex

    homes = [Path.home() / ".codex", codex.codex_home()]
    homes += sorted((codex.store_dir() / "homes").glob("*"))
    return list(dict.fromkeys(h / "sessions" for h in homes))


def _codex_cwd(path: Path) -> str | None:
    for rec in _read_jsonl(path):
        payload = rec.get("payload")
        if rec.get("type") == "session_meta" and isinstance(payload, dict):
            cwd = payload.get("cwd")
            return cwd if isinstance(cwd, str) else None
        return None  # session_meta is always the first record
    return None


def find_codex(project: str) -> Path | None:
    files = [f for d in codex_session_dirs() for f in d.glob("*/*/*/rollout-*.jsonl")]
    def mtime(f: Path) -> float:
        try:
            return f.stat().st_mtime
        except OSError:
            return 0.0

    files.sort(key=mtime, reverse=True)
    for f in files:
        try:
            cwd = _codex_cwd(f)
        except OSError:
            continue
        if cwd and _same_dir(cwd, project):
            return f
    return None


# -- transcript parsing ----------------------------------------------------------

def _clip(s: str, n: int) -> str:
    s = " ".join(s.split()) if n <= 200 else s.strip()
    return s if len(s) <= n else s[: n - 1] + "…"


def _tool_line(name: str, args) -> str:
    if isinstance(args, str):
        try:
            args = json.loads(args)
        except json.JSONDecodeError:
            return f"{name}: {args}"
    target = ""
    if isinstance(args, dict):
        for k in ("file_path", "path", "notebook_path", "command", "cmd", "pattern", "url", "query", "description"):
            v = args.get(k)
            if v:
                target = " ".join(map(str, v)) if isinstance(v, list) else str(v)
                break
    return f"{name}: {target}" if target else name


def _user_text(text) -> str | None:
    if not isinstance(text, str):
        return None
    t = text.strip()
    # Harness-injected context (system reminders, slash-command tags, AGENTS.md, environment) isn't the user.
    if not t or t.startswith(("<", "# AGENTS.md", "[Request interrupted")):
        return None
    return t


def parse_claude(path: Path) -> tuple[list[str], list[str]]:
    users, tools = [], []
    for rec in _read_jsonl(path):
        if rec.get("isMeta") or rec.get("isSidechain"):
            continue
        msg = rec.get("message") or {}
        content = msg.get("content") if isinstance(msg, dict) else None
        items = [{"type": "text", "text": content}] if isinstance(content, str) else content
        if not isinstance(items, list):
            continue
        for it in items:
            if not isinstance(it, dict):
                continue
            if rec.get("type") == "user" and it.get("type") == "text":
                if (t := _user_text(it.get("text") or "")):
                    users.append(t)
            elif rec.get("type") == "assistant" and it.get("type") == "tool_use":
                tools.append(_tool_line(it.get("name") or "tool", it.get("input")))
    return users, tools


def parse_codex(path: Path) -> tuple[list[str], list[str]]:
    users, tools = [], []
    for rec in _read_jsonl(path):
        p = rec.get("payload")
        if rec.get("type") != "response_item" or not isinstance(p, dict):
            continue
        kind = p.get("type")
        if kind == "message" and p.get("role") == "user":
            content = p.get("content")
            for it in content if isinstance(content, list) else []:
                if isinstance(it, dict) and (t := _user_text(it.get("text") or "")):
                    users.append(t)
        elif kind in ("function_call", "custom_tool_call"):
            tools.append(_tool_line(p.get("name") or "tool", p.get("arguments", p.get("input"))))
        elif kind == "local_shell_call":
            tools.append(_tool_line("shell", p.get("action")))
    return users, tools


# -- notes -------------------------------------------------------------------------

def _git(project: str) -> str:
    # The folder's repo config is untrusted: core.fsmonitor, external diff and textconv could run its commands.
    # --no-optional-locks: no index write, so no post-index-change hook and no index.lock under a live session.
    git = ["git", "--no-optional-locks", "-c", "core.fsmonitor=false", "-C", project]
    try:
        status = subprocess.run(git + ["status", "--short"],
                                capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
        if status.returncode != 0:
            return "_Not a git repository._"
        stat = subprocess.run(git + ["diff", "--stat", "--no-ext-diff", "--no-textconv"],
                              capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
    except (OSError, subprocess.SubprocessError):
        return "_git not available._"
    out = (status.stdout.strip() or "(clean)") + ("\n\n" + stat.stdout.strip() if stat.stdout.strip() else "")
    return "```\n" + _clip(out, 3000) + "\n```"


def build_notes(source: str, project: str, transcript: Path, users: list[str], tools: list[str]) -> str:
    # Redact before clipping, so a cut can't shorten a secret below the patterns' minimum length.
    users, tools = [redact(u) for u in users], [redact(t) for t in tools]
    part = []
    if users:
        part.append("## Original request\n\n" + _clip(users[0], 2000))
        recent = users[1:][-3:]
        if recent:
            part.append("## Latest user messages\n\n" + "\n\n".join("- " + _clip(u, 800) for u in recent))
    if tools:
        part.append("## Last actions of the previous agent\n\n" + "\n".join("- " + _clip(t, 160) for t in tools[-15:]))
    body = "\n\n".join(part) or "_The transcript had no readable user messages._"
    if len(body) > TRANSCRIPT_CAP:
        body = body[: TRANSCRIPT_CAP - 1] + "…"
    title = {"claude": "Claude Code", "codex": "Codex", "grok": "Grok Build"}[source]
    notes = (
        "# Hand-off notes (interrupted work)\n\n"
        f"Work in `{project}` was interrupted in a {title} session and is being picked up by a different agent "
        "in a new session. This is not a resume: only these notes carry over, and the previous agent's "
        "unfinished reasoning is lost.\n\n"
        f"{body}\n\n## Working tree now\n\n{_git(project)}\n\n"
        "## What to do\n\n"
        "1. Re-read the files involved; do not trust these notes over the code.\n"
        "2. Verify the current state (build, tests, git status) before changing anything.\n"
        "3. Continue the original task from there.\n"
        "4. If something is unclear or looks half-done in a risky way, ask the user first.\n"
    )
    return redact(notes)


def handoff_dir() -> Path:
    base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
    return Path(base) / "handoff" / "notes"


def cleanup(directory: Path, now: float | None = None) -> None:
    cutoff = (now or time.time()) - KEEP_DAYS * 86400
    for f in directory.glob("*.md"):
        try:
            if f.stat().st_mtime < cutoff:
                f.unlink()
        except OSError:
            pass


def run(source: str, project: str) -> dict:
    if not os.path.isdir(project):
        raise HandoffError(f"folder not found: {project}")
    if source == "grok":
        raise HandoffError("reading Grok Build sessions isn't supported yet; Grok works as a target only")
    find, parse = (find_claude, parse_claude) if source == "claude" else (find_codex, parse_codex)
    transcript = find(project)
    if transcript is None:
        raise HandoffError("no recent session found for this folder")
    notes = build_notes(source, os.path.abspath(project), transcript, *parse(transcript))
    out_dir = handoff_dir()
    out_dir.mkdir(parents=True, exist_ok=True)
    cleanup(out_dir)
    path = out_dir / f"{datetime.now():%Y%m%d-%H%M%S}-{source}.md"
    tmp = path.with_suffix(".tmp")
    tmp.write_text(notes, encoding="utf-8")
    os.replace(tmp, path)  # the target CLI never sees a half-written file
    return {"schemaVersion": SCHEMA_VERSION, "path": str(path), "source": source,
            "transcript": str(transcript), "chars": len(notes)}


def main(argv: list[str]) -> int:
    p = argparse.ArgumentParser(prog="cswap handoff")
    p.add_argument("--from", dest="source", choices=SOURCES, required=True)
    p.add_argument("--dir", required=True)
    p.add_argument("--json", action="store_true")
    try:
        args = p.parse_args(argv)
    except SystemExit:
        print(json.dumps(error_envelope(HandoffError("invalid arguments"))))
        return 1
    try:
        result = run(args.source, args.dir)
    except (HandoffError, OSError) as e:
        print(json.dumps(error_envelope(e)) if args.json else f"error: {e}")
        return 1
    print(json.dumps(result) if args.json else result["path"])
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

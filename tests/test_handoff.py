"""handoff: synthetic transcripts in tmp dirs only; no network."""

from __future__ import annotations

import json
import os
import re
import time

import pytest

from claude_swap import handoff
from claude_swap.paths import get_backup_root

SECRETS = [
    "sk-ant-oat01-SYNTHETICabcdef123456",
    "Bearer SYNTHETICtoken1234567890",
    "sk-SYNTHETICopenaikey1234567890",
    "xai-SYNTHETICgrokkey1234567890",
    "eyJhbGciOiJub25lIn0.eyJzdWIiOiJzeW50aGV0aWMifQ.c2ln",
    "ghp_SYNTHETICgithubtoken0123456789",
    "github_pat_SYNTHETIC_finegrained_0123",
    "AKIAIOSFODNN7EXAMPLE",
    "xoxb-SYNTHETIC-slack-token",
    "AIzaSYNTHETICgooglekey0123456789ab",
    "sk_live_SYNTHETICstripe12345",
    "-----BEGIN RSA PRIVATE KEY-----\nSYNTHETICkeymaterial\n-----END RSA PRIVATE KEY-----",
]


@pytest.fixture
def project(tmp_path, monkeypatch):
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "local"))
    monkeypatch.delenv("CODEX_HOME", raising=False)
    d = tmp_path / "my.proj"
    d.mkdir()
    return d


def _write(path, records, mtime=None):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(json.dumps(r) for r in records) + "\n", encoding="utf-8")
    if mtime:
        os.utime(path, (mtime, mtime))
    return path


def _claude_records(first="Build the parser", extra=()):
    recs = [
        {"type": "user", "message": {"role": "user", "content": "<system-reminder>ignore</system-reminder>"}},
        {"type": "user", "message": {"role": "user", "content": first}},
        {"type": "assistant", "message": {"content": [
            {"type": "text", "text": "ok"},
            {"type": "tool_use", "name": "Edit", "input": {"file_path": "src/parser.py", "old_string": "x"}},
            {"type": "tool_use", "name": "Bash", "input": {"command": "pytest -q"}},
        ]}},
        {"type": "user", "message": {"content": [{"type": "tool_result", "content": "TOOL OUTPUT"}]}},
    ]
    recs += [{"type": "user", "message": {"content": [{"type": "text", "text": t}]}} for t in extra]
    return recs


def _encoded(d):
    return re.sub(r"[^A-Za-z0-9]", "-", os.path.abspath(d))


def test_claude_default_profile(project):
    root = handoff.claude_project_dirs()[0]
    _write(root / _encoded(project) / "a.jsonl", _claude_records())
    r = handoff.run("claude", str(project))
    text = open(r["path"], encoding="utf-8").read()
    assert r["source"] == "claude" and r["chars"] == len(text)
    assert "Build the parser" in text
    assert "- Edit: src/parser.py" in text and "- Bash: pytest -q" in text
    assert "TOOL OUTPUT" not in text and "ignore" not in text
    assert "not a resume" in text and "ask the user" in text


def test_claude_per_account_profile_wins_when_newer(project):
    now = time.time()
    _write(handoff.claude_project_dirs()[0] / _encoded(project) / "old.jsonl", _claude_records("OLD task"), now - 100)
    prof = get_backup_root() / "sessions" / "2-x_example.com" / "projects" / _encoded(project) / "new.jsonl"
    _write(prof, _claude_records("NEW task"), now)
    r = handoff.run("claude", str(project))
    assert r["transcript"] == str(prof)
    assert "NEW task" in open(r["path"], encoding="utf-8").read()


def test_codex_matches_cwd(project, tmp_path):
    sessions = handoff.codex_session_dirs()[0]
    meta = lambda cwd: {"type": "session_meta", "payload": {"id": "s", "cwd": cwd}}
    msg = lambda t: {"type": "response_item", "payload": {"type": "message", "role": "user",
                                                           "content": [{"type": "input_text", "text": t}]}}
    call = {"type": "response_item", "payload": {"type": "function_call", "name": "shell",
                                                 "arguments": json.dumps({"command": ["git", "status"]})}}
    mine = _write(sessions / "2026/10/01/rollout-a.jsonl",
                  [meta(str(project)), msg("<environment_context>x</environment_context>"), msg("Fix the bug"), call],
                  time.time() - 50)
    _write(sessions / "2026/10/02/rollout-b.jsonl", [meta(str(tmp_path / "other")), msg("Other")], time.time())
    r = handoff.run("codex", str(project))
    text = open(r["path"], encoding="utf-8").read()
    assert r["transcript"] == str(mine)
    assert "Fix the bug" in text and "- shell: git status" in text and "Other" not in text


def test_no_session_and_grok_errors(project, capsys):
    assert handoff.main(["--from", "claude", "--dir", str(project), "--json"]) == 1
    assert "no recent session found" in json.loads(capsys.readouterr().out)["error"]["message"]
    with pytest.raises(handoff.HandoffError, match="target only"):
        handoff.run("grok", str(project))


def test_caps_and_redaction(project):
    big = "word " * 3000
    extra = [f"msg {i} " + " ".join(SECRETS) for i in range(6)] + [big]
    recs = _claude_records("start " + SECRETS[0], extra)
    recs += [{"type": "assistant", "message": {"content": [
        {"type": "tool_use", "name": "Bash", "input": {"command": f"curl -H 'Authorization: {SECRETS[1]}' n{i}"}}]}}
        for i in range(40)]
    _write(handoff.claude_project_dirs()[0] / _encoded(project) / "a.jsonl", recs)
    text = open(handoff.run("claude", str(project))["path"], encoding="utf-8").read()
    for s in SECRETS:
        assert s not in text and handoff.redact(s) == "[redacted]"
    assert "msg 0" not in text  # only the last 3 follow-ups
    assert text.count("- Bash:") == 15
    transcript_part = text.split("## Working tree now")[0]
    assert len(transcript_part) < handoff.TRANSCRIPT_CAP + 600
    assert handoff.redact('"access_token": "abc"  refresh_token=xyz id_token=j').count("[redacted]") == 3
    ordinary = r"edit C:\Users\dev\my.proj\src\app.py, then mail dev@example.com"
    assert handoff.redact(ordinary) == ordinary
    assert "MIIE" not in handoff.redact("secret_key=" + SECRETS[-1].replace("SYNTHETICkeymaterial", "MIIEsynthetic"))
    assert handoff.redact("-----BEGIN PRIVATE KEY-----\nMIIEtruncated") == "[redacted]"
    # Quoted values (TOML, .env, JSON) are redacted whole, spaces included.
    for quoted in ('password = "hunter two"', "access_token = 'abc def'", '{"api_key": "abc 123"}'):
        out = handoff.redact(quoted)
        assert "hunter" not in out and "abc" not in out, out


def test_git_missing(project, monkeypatch):
    def boom(*a, **k):
        raise FileNotFoundError("git")
    monkeypatch.setattr(handoff.subprocess, "run", boom)
    assert handoff._git(str(project)) == "_git not available._"


def test_not_a_repo(project):
    assert handoff._git(str(project)) == "_Not a git repository._"


def test_cleanup_old_files(tmp_path):
    old, new = tmp_path / "old.md", tmp_path / "new.md"
    old.write_text("x"), new.write_text("y")
    os.utime(old, (time.time() - 8 * 86400,) * 2)
    handoff.cleanup(tmp_path)
    assert not old.exists() and new.exists()


def test_odd_records_dont_crash(project):
    root = handoff.claude_project_dirs()[0]
    f = _write(root / _encoded(project) / "a.jsonl", [
        {"type": "user", "message": {"content": 5}},
        {"type": "user", "message": {"content": [{"type": "text", "text": {"x": 1}}]}},
        {"type": "user", "message": ["not", "a", "dict"]},
        {"type": "assistant", "message": {"content": [{"type": "tool_use", "name": "Bash", "input": {"command": ["git", 1]}}]}},
        {"type": "user", "message": {"content": "[Request interrupted by user]"}},
    ] + _claude_records())
    with open(f, "a", encoding="utf-8") as fh:
        fh.write("{broken\n" + "[" * 100000 + "\n")
    text = open(handoff.run("claude", str(project))["path"], encoding="utf-8").read()
    assert "Build the parser" in text and "- Bash: git 1" in text
    assert "Request interrupted" not in text

"""End to end (Windows): a session profile holds a plaintext login only while
a terminal uses it.

Drives the real CLI (``python -m claude_swap``) in subprocesses against a
synthetic home, with a fake ``claude`` (a .cmd shim on PATH) and the network
routed into a dead proxy. Synthetic credentials only.
"""

from __future__ import annotations

import json
import os
import signal
import subprocess
import sys
import time
from pathlib import Path

import pytest

from claude_swap import dpapi
from claude_swap.process_detection import is_pid_alive
from claude_swap.session import RUN_MARKERS, slugify_email

pytestmark = pytest.mark.skipif(
    sys.platform != "win32", reason="session sealing is Windows-only"
)

TIMEOUT = 30
DEAD_PROXY = "http://127.0.0.1:9"
ALICE = ("alice@example.com", "org-a")
BOB = ("bob@example.com", "org-b")

# `auth status --json` answers from the config dir; an interactive launch
# registers a session record, rotates the profile's tokens (a refresh), says
# it is up, and exits once released.
FAKE_CLAUDE = r'''
import json, os, sys, time
from pathlib import Path

config = os.environ.get("CLAUDE_CONFIG_DIR", "")
root = Path(config) if config else Path.home() / ".claude"
if sys.argv[1:3] == ["auth", "status"]:
    status = {"loggedIn": False, "authMethod": "none"}
    try:
        account = json.loads((root / ".claude.json").read_text())["oauthAccount"]
        if (root / ".credentials.json").exists():
            status = {"loggedIn": True, "authMethod": "claude.ai",
                      "email": account["emailAddress"],
                      "orgId": account.get("organizationUuid")}
    except (OSError, ValueError, KeyError):
        pass
    print(json.dumps(status))
    sys.exit(0)

info = {"pid": os.getpid(), "config": config}
record = root / "sessions" / f"{os.getpid()}.json"
if config:  # never touch the default login
    record.parent.mkdir(exist_ok=True)
    record.write_text(json.dumps({
        "pid": os.getpid(), "sessionId": "fake", "cwd": os.getcwd(),
        "startedAt": int(time.time() * 1000), "kind": "interactive",
        "entrypoint": "cli",
    }))
    creds_path = root / ".credentials.json"
    creds = json.loads(creds_path.read_text())
    oauth = creds["claudeAiOauth"]
    oauth["accessToken"] += "-rotated"
    oauth["refreshToken"] += "-rotated"
    oauth["expiresAt"] += 1000
    creds_path.write_text(json.dumps(creds))
ready = Path(os.environ["FAKE_READY"])
ready.with_suffix(".tmp").write_text(json.dumps(info))
ready.with_suffix(".tmp").replace(ready)
release = Path(os.environ["FAKE_RELEASE"])
deadline = time.time() + 45
while not release.exists() and time.time() < deadline:
    time.sleep(0.05)
record.unlink(missing_ok=True)
'''


def _wait_for(predicate, what: str) -> None:
    deadline = time.monotonic() + TIMEOUT
    while not predicate():
        if time.monotonic() > deadline:
            pytest.fail(f"timed out waiting for {what}")
        time.sleep(0.05)


class Terminal:
    def __init__(self, proc: subprocess.Popen, release: Path, log: Path, info: dict):
        self.proc, self.release, self.log, self.info = proc, release, log, info
        self.profile = Path(info["config"]) if info["config"] else None

    def finish(self) -> None:
        """claude exits normally; the wrapper mirrors its exit code."""
        self.release.touch()
        assert self.proc.wait(timeout=TIMEOUT) == 0, self.log.read_text()


class Rig:
    def __init__(self, tmp_path: Path):
        self.home = tmp_path / "home"
        (self.home / ".claude").mkdir(parents=True)
        self.signals = tmp_path / "signals"
        self.signals.mkdir()
        bin_dir = tmp_path / "bin"
        bin_dir.mkdir()
        fake = bin_dir / "fake_claude.py"
        fake.write_text(FAKE_CLAUDE)
        (bin_dir / "claude.cmd").write_text(f'@"{sys.executable}" "{fake}" %*\r\n')
        drop = {
            "CLAUDE_CONFIG_DIR", "CLAUDE_SECURESTORAGE_CONFIG_DIR", "XDG_DATA_HOME",
            "NO_PROXY", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN",
            "CLAUDE_CODE_OAUTH_TOKEN", "CODEX_HOME", "GROK_HOME", "GROK_AUTH",
            "GROK_AUTH_PATH",
        }
        self.env = {k: v for k, v in os.environ.items() if k.upper() not in drop}
        self.env.update(
            USERPROFILE=str(self.home),
            HOME=str(self.home),
            LOCALAPPDATA=str(self.home / "AppData" / "Local"),
            HTTPS_PROXY=DEAD_PROXY,
            HTTP_PROXY=DEAD_PROXY,
            PATH=f"{bin_dir}{os.pathsep}{os.environ.get('PATH', '')}",
        )
        self.launched: list[tuple[subprocess.Popen, Path]] = []
        self.backup = self.home / ".claude-swap-backup"

    def cswap(self, *args: str) -> subprocess.CompletedProcess:
        return subprocess.run(
            [sys.executable, "-m", "claude_swap", *args],
            env=self.env, cwd=self.home, stdin=subprocess.DEVNULL,
            capture_output=True, text=True, timeout=TIMEOUT,
        )

    def enroll(self, name: str, account: tuple[str, str]) -> None:
        email, org = account
        login = self.home / f"login-{name}"
        login.mkdir()
        (login / ".credentials.json").write_text(json.dumps({"claudeAiOauth": {
            "accessToken": f"synthetic-access-{name}",
            "refreshToken": f"synthetic-refresh-{name}",
            "expiresAt": 4102444800000,
            "scopes": ["user:inference"],
        }}))
        (login / ".claude.json").write_text(json.dumps({"oauthAccount": {
            "emailAddress": email, "accountUuid": f"acc-{name}",
            "organizationUuid": org, "organizationName": "",
        }}))
        result = self.cswap("enroll", str(login))
        assert result.returncode == 0, result.stdout + result.stderr

    def launch(self, slot: str, tag: str) -> Terminal:
        ready, release = self.signals / f"{tag}.ready", self.signals / f"{tag}.release"
        log = self.signals / f"{tag}.log"
        # A file, not a pipe: claude inherits the handle, and a pipe would stay
        # open after the wrapper dies.
        with log.open("w") as out:
            proc = subprocess.Popen(
                [sys.executable, "-m", "claude_swap", "run", slot],
                env=dict(self.env, FAKE_READY=str(ready), FAKE_RELEASE=str(release)),
                cwd=self.home, stdin=subprocess.DEVNULL,
                stdout=out, stderr=subprocess.STDOUT,
            )
        self.launched.append((proc, release))
        _wait_for(lambda: ready.exists() or proc.poll() is not None, f"{tag} to start")
        if not ready.exists():
            pytest.fail(f"{tag} did not start: {log.read_text()}")
        return Terminal(proc, release, log, json.loads(ready.read_text()))

    def backup_refresh(self, slot: int, email: str) -> str:
        raw = (self.backup / "credentials" / f".creds-{slot}-{email}.enc").read_text()
        assert raw.startswith(dpapi.PREFIX)  # sealed, not plaintext
        return json.loads(dpapi.unseal(raw))["claudeAiOauth"]["refreshToken"]

    def close(self) -> None:
        for _, release in self.launched:  # let every fake claude exit on its own
            release.touch()
        for proc, _ in self.launched:
            try:
                proc.wait(timeout=TIMEOUT)
            except subprocess.TimeoutExpired:
                proc.kill()  # our own wrapper launcher, nothing else


def _plaintext(profile: Path) -> tuple[str, str] | None:
    """(email, refresh token) the profile holds, or None once sealed."""
    try:
        creds = json.loads((profile / ".credentials.json").read_text())
    except FileNotFoundError:
        return None
    email = json.loads((profile / ".claude.json").read_text())["oauthAccount"][
        "emailAddress"
    ]
    return email, creds["claudeAiOauth"]["refreshToken"]


@pytest.fixture
def rig(tmp_path):
    rig = Rig(tmp_path)
    yield rig
    rig.close()


def test_normal_launch_seals_on_exit(rig):
    rig.enroll("a", ALICE)
    term = rig.launch("1", "a")
    assert _plaintext(term.profile) == (ALICE[0], "synthetic-refresh-a-rotated")

    term.finish()

    assert _plaintext(term.profile) is None
    assert (term.profile / ".claude.json").exists()  # history stays
    assert rig.backup_refresh(1, ALICE[0]) == "synthetic-refresh-a-rotated"


def test_two_accounts_concurrently_stay_apart(rig):
    rig.enroll("a", ALICE)
    rig.enroll("b", BOB)
    ta = rig.launch("1", "a")
    tb = rig.launch("2", "b")
    assert ta.profile != tb.profile
    assert _plaintext(ta.profile) == (ALICE[0], "synthetic-refresh-a-rotated")
    assert _plaintext(tb.profile) == (BOB[0], "synthetic-refresh-b-rotated")

    ta.finish()
    assert _plaintext(ta.profile) is None
    assert _plaintext(tb.profile) == (BOB[0], "synthetic-refresh-b-rotated")

    tb.finish()
    assert _plaintext(tb.profile) is None
    assert rig.backup_refresh(1, ALICE[0]) == "synthetic-refresh-a-rotated"
    assert rig.backup_refresh(2, BOB[0]) == "synthetic-refresh-b-rotated"


def test_same_account_twice_seals_after_the_last_exit(rig):
    rig.enroll("a", ALICE)
    first = rig.launch("1", "a1")
    second = rig.launch("1", "a2")
    assert first.profile == second.profile
    twice = (ALICE[0], "synthetic-refresh-a-rotated-rotated")
    assert _plaintext(first.profile) == twice

    first.finish()
    assert _plaintext(first.profile) == twice  # the other terminal is live

    second.finish()
    assert _plaintext(first.profile) is None
    assert rig.backup_refresh(1, ALICE[0]) == twice[1]


def test_list_leaves_a_running_session_alone(rig):
    rig.enroll("a", ALICE)
    term = rig.launch("1", "a")
    before = (term.profile / ".credentials.json").read_bytes()

    listed = rig.cswap("list", "--json")
    assert listed.returncode == 0, listed.stderr
    json.loads(listed.stdout)
    assert (term.profile / ".credentials.json").read_bytes() == before

    term.finish()
    assert _plaintext(term.profile) is None
    json.loads(rig.cswap("list", "--json").stdout)


def test_active_default_account_takes_the_fast_path(rig):
    rig.enroll("a", ALICE)
    default_creds = rig.home / ".claude" / ".credentials.json"
    default_creds.write_text(json.dumps({"claudeAiOauth": {
        "accessToken": "synthetic-access-a",
        "refreshToken": "synthetic-refresh-a",
        "expiresAt": 4102444800000,
    }}))
    default_config = rig.home / ".claude.json"
    default_config.write_text(json.dumps({"oauthAccount": {
        "emailAddress": ALICE[0], "organizationUuid": ALICE[1],
    }}))
    before = default_creds.read_bytes(), default_config.read_bytes()

    term = rig.launch("1", "e")
    assert term.profile is None  # plain claude on the default login
    term.finish()

    assert not (rig.backup / "sessions" / f"1-{slugify_email(ALICE[0])}").exists()
    assert (default_creds.read_bytes(), default_config.read_bytes()) == before


def test_closed_window_is_sealed_by_the_next_list(rig):
    rig.enroll("a", ALICE)
    term = rig.launch("1", "a")
    # The resident wrapper's own PID (the venv launcher spawns it as a child).
    (marker,) = (term.profile / RUN_MARKERS).iterdir()
    wrapper_pid = int(marker.name)
    assert wrapper_pid != os.getpid()

    # Closing the console kills the wrapper and claude: only the processes
    # this test started, wrapper first so no exit seal can run.
    os.kill(wrapper_pid, signal.SIGTERM)
    term.proc.wait(timeout=TIMEOUT)
    try:
        os.kill(term.info["pid"], signal.SIGTERM)
    except OSError:
        pass  # already gone with the wrapper's process tree
    _wait_for(lambda: not is_pid_alive(term.info["pid"]), "fake claude to exit")
    assert _plaintext(term.profile) == (ALICE[0], "synthetic-refresh-a-rotated")

    # The killed claude's record stays behind, and on Windows it carries no
    # start stamp: if its PID is reused (a loaded machine spawns plenty), the
    # sweep rightly holds off. The tray polls `list`, so a later sweep seals.
    def swept() -> bool:
        listed = rig.cswap("list", "--json")
        assert listed.returncode == 0, listed.stderr
        json.loads(listed.stdout)
        return _plaintext(term.profile) is None

    _wait_for(swept, "a list to seal the closed window's profile")
    assert rig.backup_refresh(1, ALICE[0]) == "synthetic-refresh-a-rotated"

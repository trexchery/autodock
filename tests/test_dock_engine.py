"""Claude Dock engine additions: DPAPI sealing and isolated enrollment.

All credentials here are synthetic.
"""

import base64
import json
import sys
from pathlib import Path

import pytest

from claude_swap import dpapi
from claude_swap.enroll import build_envelope, enroll
from claude_swap.exceptions import TransferError
from claude_swap.switcher import ClaudeAccountSwitcher

FAKE_CREDS = {
    "claudeAiOauth": {
        "accessToken": "sk-ant-oat01-SYNTHETIC-access",
        "refreshToken": "sk-ant-ort01-SYNTHETIC-refresh",
        "expiresAt": 4102444800000,
        "scopes": ["user:inference"],
    },
    "mcpOAuth": {"some-server": {"accessToken": "machine-local"}},
}


def _login_dir(root: Path, email: str = "enrolled@example.com") -> Path:
    d = root / "enroll"
    d.mkdir()
    (d / ".credentials.json").write_text(json.dumps(FAKE_CREDS), encoding="utf-8")
    (d / ".claude.json").write_text(
        json.dumps({
            "userID": "machine-specific",
            "oauthAccount": {
                "emailAddress": email,
                "accountUuid": "acc-uuid-1",
                "organizationUuid": "org-uuid-1",
                "organizationName": "Synthetic Org",
            },
        }),
        encoding="utf-8",
    )
    return d


class TestDpapi:
    def test_roundtrip(self):
        secret = json.dumps(FAKE_CREDS)
        sealed = dpapi.seal(secret)
        assert "SYNTHETIC" not in sealed
        assert dpapi.unseal(sealed) == secret

    @pytest.mark.skipif(sys.platform != "win32", reason="DPAPI is Windows-only")
    def test_windows_output_is_encrypted_not_base64(self):
        sealed = dpapi.seal("hello")
        assert sealed.startswith(dpapi.PREFIX)
        blob = base64.b64decode(sealed[len(dpapi.PREFIX):])
        assert b"hello" not in blob

    def test_reads_legacy_base64(self):
        legacy = base64.b64encode(b'{"legacy": true}').decode()
        assert dpapi.unseal(legacy) == '{"legacy": true}'

    @pytest.mark.skipif(sys.platform != "win32", reason="DPAPI is Windows-only")
    def test_tampered_blob_is_rejected(self):
        sealed = dpapi.seal("hello")
        blob = bytearray(base64.b64decode(sealed[len(dpapi.PREFIX):]))
        blob[-1] ^= 0xFF
        with pytest.raises(OSError):
            dpapi.unseal(dpapi.PREFIX + base64.b64encode(bytes(blob)).decode())

    def test_garbage_is_rejected(self):
        with pytest.raises(Exception):
            dpapi.unseal("!!!! not base64")


class TestEnroll:
    def test_envelope_strips_machine_state(self, tmp_path: Path):
        env = build_envelope(_login_dir(tmp_path), slot=3)
        acct = env["accounts"][0]
        assert env["activeAccountNumber"] is None
        assert acct["number"] == 3
        assert acct["email"] == "enrolled@example.com"
        assert acct["organizationUuid"] == "org-uuid-1"
        assert set(acct["credentials"]) == {"claudeAiOauth"}
        assert acct["config"] == {"oauthAccount": acct["config"]["oauthAccount"]}

    def test_missing_login_is_clear_error(self, tmp_path: Path):
        with pytest.raises(TransferError, match="no login found"):
            build_envelope(tmp_path, slot=1)

    def test_enroll_writes_sealed_backup_and_leaves_live_login_alone(self, temp_home: Path):
        switcher = ClaudeAccountSwitcher()
        live = temp_home / ".claude" / ".credentials.json"
        live.parent.mkdir(parents=True, exist_ok=True)
        live.write_text('{"untouched": true}', encoding="utf-8")

        email = enroll(switcher, str(_login_dir(temp_home)))

        assert email == "enrolled@example.com"
        assert live.read_text(encoding="utf-8") == '{"untouched": true}'
        seq = switcher._get_sequence_data()
        assert seq["accounts"]["1"]["email"] == "enrolled@example.com"
        assert seq.get("activeAccountNumber") is None
        stored = json.loads(switcher._read_account_credentials("1", email))
        assert stored["claudeAiOauth"]["refreshToken"] == "sk-ant-ort01-SYNTHETIC-refresh"
        if sys.platform == "win32":
            for f in switcher.credentials_dir.glob("*.enc"):
                text = f.read_text(encoding="utf-8")
                assert text.startswith(dpapi.PREFIX)
                assert "SYNTHETIC" not in base64.b64decode(text[len(dpapi.PREFIX):]).decode("latin-1")

    def test_re_enroll_overwrites_same_slot(self, temp_home: Path):
        switcher = ClaudeAccountSwitcher()
        login = _login_dir(temp_home)
        enroll(switcher, str(login))
        FAKE = json.loads((login / ".credentials.json").read_text())
        FAKE["claudeAiOauth"]["refreshToken"] = "sk-ant-ort01-SYNTHETIC-rotated"
        (login / ".credentials.json").write_text(json.dumps(FAKE), encoding="utf-8")

        enroll(switcher, str(login))

        seq = switcher._get_sequence_data()
        assert list(seq["accounts"]) == ["1"]
        stored = json.loads(switcher._read_account_credentials("1", "enrolled@example.com"))
        assert stored["claudeAiOauth"]["refreshToken"] == "sk-ant-ort01-SYNTHETIC-rotated"

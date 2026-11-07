import pathlib

import pytest

from rig.repo_governance import vercel_gating
from rig.repo_governance.vercel_gating import CliAuth, token_is_live

pytestmark = pytest.mark.ci


NOW_S = 1_800_000_000.0


@pytest.mark.parametrize(
    ("expires_at", "live"),
    [
        (None, True),
        (0, True),
        (NOW_S + 3600, True),
        (NOW_S + 30, False),
        ((NOW_S + 3600) * 1000, True),
        ((NOW_S - 3600) * 1000, False),
    ],
)
def test_token_is_live_reads_seconds_or_milliseconds(expires_at: float | None, live: bool):
    assert token_is_live(CliAuth(token="t", expires_at=expires_at), NOW_S) is live


def test_vercel_token_reads_cli_auth_file(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch):
    auth = tmp_path / "auth.json"
    auth.write_text('{"token": "abc", "expiresAt": 99999999999999}', encoding="utf-8")
    monkeypatch.delenv("VERCEL_TOKEN", raising=False)
    monkeypatch.setattr(vercel_gating, "_cli_auth_candidates", lambda: [tmp_path / "missing.json", auth])
    assert vercel_gating.vercel_token() == "abc"


def test_checkout_facts_read_vercel_json_allowlist(tmp_path: pathlib.Path):
    checkout = tmp_path / "repo"
    checkout.mkdir()
    (checkout / "vercel.json").write_text(
        '{"git": {"deploymentEnabled": {"main": true, "*": false, "x": "nope"}}}', encoding="utf-8"
    )
    facts = vercel_gating._repo_facts("repo", None, tmp_path)
    assert facts.allow == ("main",)
    assert facts.deny == ("*",)

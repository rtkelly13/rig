"""Fixtures for vault maintenance scenarios.

Behaviour is exercised through the public entry point — the `vault` Typer app via
``CliRunner`` — against an isolated throwaway vault, so the scenarios assert what a
user would see rather than internal state.
"""

from __future__ import annotations

from pathlib import Path

import pytest
from typer.testing import CliRunner

PARA_FOLDERS = ("Projects", "Areas", "Resources", "Archive", "Social Media Posts", "Daily")


@pytest.fixture(autouse=True)
def _never_touch_the_real_vault(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """Point every test at a throwaway vault, whether it asks for one or not.

    Without this, any code path reaching `get_vault_path()` without an override
    falls through to the real vault — 2,400 notes on this machine — and
    scanning it hangs the suite. That bug was
    inherited from the package this was ported from, where it made six tests fail
    and one full run take two and a half hours. An autouse default kills the whole
    class of problem: a test must opt *in* to a path, never fall back to a real one.
    """
    fallback = tmp_path / "_isolated_vault"
    fallback.mkdir(exist_ok=True)
    monkeypatch.setenv("VAULT_PATH", str(fallback))
    monkeypatch.delenv("TVS_VAULT_PATH", raising=False)


@pytest.fixture
def vault(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Path:
    """An isolated PARA-shaped vault, wired in via VAULT_PATH."""
    vault_dir = tmp_path / "Vault"
    for folder in PARA_FOLDERS:
        (vault_dir / folder).mkdir(parents=True)
    monkeypatch.setenv("VAULT_PATH", str(vault_dir))
    monkeypatch.delenv("TVS_VAULT_PATH", raising=False)
    return vault_dir


@pytest.fixture
def data_dir(tmp_path: Path) -> Path:
    """An isolated data root for scenarios that write alongside the vault."""
    data = tmp_path / "data"
    data.mkdir()
    return data


@pytest.fixture
def cli() -> CliRunner:
    """Runner for the `vault` app.

    A wide COLUMNS keeps Rich from wrapping mid-phrase so scenarios can assert on
    human-readable sentences.
    """
    return CliRunner(env={"COLUMNS": "200"})

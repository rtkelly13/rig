"""Feature: orphan detection edge cases via `vault graph orphans list`.

Orphan detection prefers the native Obsidian CLI when it is installed and
falls back to a regex wikilink scan otherwise. The scan resolves empty,
URL-encoded, and anchored wikilinks, and excluded folders never count as
orphans.
"""

from __future__ import annotations

import subprocess
from pathlib import Path

import pytest
from typer.testing import CliRunner

from rig.obsidian import orphan_manager
from rig.obsidian.cli import app


def test_native_obsidian_cli_results_are_filtered_and_preferred(
    cli: CliRunner, vault: Path, data_dir: Path, monkeypatch: pytest.MonkeyPatch
):
    """Scenario: when the obsidian CLI answers, its list is used minus excluded folders."""

    # Given the native obsidian CLI reports orphans, one of them in Templates/
    class _Native:
        returncode = 0
        stdout = "Resources/Lonely.md\nTemplates/Skeleton.md\n"
        stderr = ""

    monkeypatch.setattr(subprocess, "run", lambda *a, **k: _Native())  # noqa: ARG005
    # ...and Obsidian has *this* vault open, which is the only case the native
    # answer is about. See get_native_orphans.
    monkeypatch.setattr(orphan_manager, "_obsidian_open_vault", lambda: vault)

    # When the user lists orphans
    result = cli.invoke(app, ["graph", "orphans", "list"])

    # Then the native result is shown with excluded folders filtered out
    assert result.exit_code == 0
    assert "Resources/Lonely.md" in result.output
    assert "Templates/Skeleton.md" not in result.output


def test_native_results_are_ignored_for_a_vault_obsidian_does_not_have_open(
    cli: CliRunner, vault: Path, data_dir: Path, monkeypatch: pytest.MonkeyPatch
):
    """Scenario: the desktop app answers about its own vault, so it is not consulted.

    `obsidian orphans` takes no path and reports on whichever vault is open. Asking
    it about a different vault returns a confident answer about the wrong one, which
    is how a temporary vault used to inherit the real vault's orphan list.
    """

    # Given the native CLI would answer with orphans from some other vault
    class _OtherVault:
        returncode = 0
        stdout = "Resources/NotInThisVault.md\n"
        stderr = ""

    called: list[bool] = []

    def _run(*_a: object, **_k: object) -> _OtherVault:
        called.append(True)
        return _OtherVault()

    monkeypatch.setattr(subprocess, "run", _run)
    # ...but Obsidian has a different vault open
    monkeypatch.setattr(orphan_manager, "_obsidian_open_vault", lambda: vault.parent / "SomeOtherVault")

    # And this vault has one genuine orphan
    (vault / "Resources" / "Lonely.md").write_text("# Lonely\n", encoding="utf-8")

    # When the user lists orphans
    result = cli.invoke(app, ["graph", "orphans", "list"])

    # Then the other vault's answer is neither used nor even requested
    assert result.exit_code == 0
    assert not called, "the desktop app should not be consulted about a vault it has not open"
    assert "NotInThisVault.md" not in result.output
    assert "Resources/Lonely.md" in result.output


def test_regex_scan_resolves_encoded_anchored_and_empty_wikilinks(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: notes referenced via encoded or anchored links are not orphans."""
    # Given a hub referencing one note by URL-encoded name, one via an anchor,
    # plus an empty wikilink, and one truly unreferenced note
    (vault / "Resources" / "My Note.md").write_text("# My Note\n", encoding="utf-8")
    (vault / "Resources" / "Anchored.md").write_text("# Anchored\n\n## Section\n", encoding="utf-8")
    (vault / "Resources" / "Lonely.md").write_text("# Lonely\n", encoding="utf-8")
    (vault / "Resources" / "Hub.md").write_text(
        "# Hub\n\n[[My%20Note]] and [[Anchored#Section]] and [[]] and [[Hub]]\n",
        encoding="utf-8",
    )

    # When the user lists orphans (no native CLI in this environment)
    result = cli.invoke(app, ["graph", "orphans", "list"])

    # Then only the unreferenced note is an orphan
    assert result.exit_code == 0
    assert "Lonely.md" in result.output
    assert "My Note.md" not in result.output
    assert "Anchored.md" not in result.output

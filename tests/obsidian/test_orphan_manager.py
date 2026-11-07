from __future__ import annotations

from unittest.mock import MagicMock, patch

import pytest

from rig.obsidian.orphan_manager import (
    _get_all_references as get_all_references,
)
from rig.obsidian.orphan_manager import (
    find_orphans,
    get_all_md_files,
    get_native_orphans,
    link_orphan_to_parent,
    run_link_orphans,
    run_list_orphans,
    run_root_hub,
)


@pytest.fixture
def mock_vault(tmp_path):
    vault = tmp_path / "vault"
    vault.mkdir()

    # Create some structure
    (vault / "Areas").mkdir()
    (vault / "Resources").mkdir()
    (vault / "Templates").mkdir()

    # Notes
    (vault / "Areas" / "Career.md").write_text("Reference to [[Resources/Books]]", encoding="utf-8")
    (vault / "Resources" / "Books.md").write_text("No references here.", encoding="utf-8")
    (vault / "Resources" / "Orphan.md").write_text("I am alone.", encoding="utf-8")
    (vault / "Templates" / "T1.md").write_text("Template", encoding="utf-8")

    return vault


def test_get_all_md_files(mock_vault):
    files = get_all_md_files(mock_vault)
    assert "Areas/Career.md" in files
    assert "Resources/Books.md" in files
    assert "Templates/T1.md" in files
    assert len(files) == 4


def test_get_all_references(mock_vault):
    md_files = get_all_md_files(mock_vault)
    refs = get_all_references(mock_vault, md_files)

    # Career.md has [[Resources/Books]]
    assert "Resources/Books" in refs


@patch("rig.obsidian.orphan_manager.get_native_orphans")
def test_find_orphans(mock_native, mock_vault):
    mock_native.return_value = []  # Force the regex scan

    orphans = find_orphans(mock_vault)

    # Areas/Career.md should be orphan (nothing links to it)
    # Resources/Books.md is NOT orphan (Career links to it)
    # Resources/Orphan.md is orphan
    # Templates/T1.md is excluded by default

    assert "Areas/Career.md" in orphans
    assert "Resources/Orphan.md" in orphans
    assert "Resources/Books.md" not in orphans
    assert "Templates/T1.md" not in orphans


def test_link_orphan_to_parent(mock_vault):
    # Create a hub
    hub = mock_vault / "Resources.md"
    hub.touch()

    orphan = "Resources/Orphan.md"
    linked_hub = link_orphan_to_parent(mock_vault, orphan)

    assert linked_hub == "Resources.md"
    content = hub.read_text(encoding="utf-8")
    assert "[[" + orphan + "]]" in content


@patch("rig.obsidian.orphan_manager.get_vault_path")
@patch("rig.obsidian.orphan_manager.get_native_orphans")
def test_run_root_hub(mock_native, mock_vault_path, mock_vault):
    mock_vault_path.return_value = mock_vault
    mock_native.return_value = []

    # Setup PARA folders
    (mock_vault / "Projects").mkdir()
    (mock_vault / "Projects" / "P1.md").touch()

    _ = run_root_hub()

    assert (mock_vault / "Home.md").exists()
    assert (mock_vault / "Projects.md").exists()
    assert (mock_vault / "Areas.md").exists()
    assert (mock_vault / "Resources.md").exists()

    home_content = (mock_vault / "Home.md").read_text(encoding="utf-8")
    assert "[[Projects]]" in home_content

    # P1.md should be linked in Projects.md
    p_hub_content = (mock_vault / "Projects.md").read_text(encoding="utf-8")
    assert "[[Projects/P1.md]]" in p_hub_content


# ── find_orphans: native CLI path ─────────────────────────────────────────────


@patch("rig.obsidian.orphan_manager.get_native_orphans")
def test_find_orphans_prefers_native_results(mock_native, mock_vault):
    mock_native.return_value = [
        "Resources/Orphan.md",
        "Templates/T1.md",  # excluded by default patterns
        "Areas/Career.md",
    ]

    orphans = find_orphans(mock_vault)

    assert orphans == ["Areas/Career.md", "Resources/Orphan.md"]


@patch("rig.obsidian.orphan_manager.get_native_orphans")
def test_find_orphans_custom_exclude_patterns(mock_native, mock_vault):
    mock_native.return_value = []

    orphans = find_orphans(mock_vault, exclude_patterns=["Areas/"])

    assert "Areas/Career.md" not in orphans
    assert "Resources/Orphan.md" in orphans
    assert "Templates/T1.md" in orphans


# ── _get_all_references: edge branches ────────────────────────────────────────


def test_get_all_references_handles_encoded_anchored_and_empty_links(tmp_path):
    vault = tmp_path / "vault"
    (vault / "Resources").mkdir(parents=True)
    (vault / "Resources" / "My Note.md").write_text("target", encoding="utf-8")
    (vault / "Resources" / "Books.md").write_text("target", encoding="utf-8")
    (vault / "Index.md").write_text(
        "[[Resources/My%20Note]] and [[Books#Chapter 1]] and [[]] and [[Missing Note]]",
        encoding="utf-8",
    )

    md_files = get_all_md_files(vault)
    refs = get_all_references(vault, md_files)

    assert "Resources/My Note" in refs
    assert "Books" in refs
    assert "Missing Note" not in refs
    assert "" not in refs


def test_get_all_references_skips_unreadable_files(tmp_path):
    vault = tmp_path / "vault"
    vault.mkdir()
    (vault / "Good.md").write_text("[[Other]]", encoding="utf-8")
    (vault / "Other.md").write_text("plain", encoding="utf-8")
    # rglob-visible entry whose read_text raises (it is a directory)
    (vault / "Bad.md").mkdir()

    md_files = get_all_md_files(vault)
    md_files["Bad.md"] = "Bad"
    refs = get_all_references(vault, md_files)

    assert "Other" in refs


# ── link_orphan_to_parent: failure paths ──────────────────────────────────────


def test_link_orphan_to_parent_no_hub_returns_none(mock_vault):
    assert link_orphan_to_parent(mock_vault, "Resources/Orphan.md") is None


def test_link_orphan_to_parent_write_failure_returns_none(tmp_path):
    vault = tmp_path / "vault"
    (vault / "Resources").mkdir(parents=True)
    # Hub path exists but is a directory, so opening it for append raises
    (vault / "Resources.md").mkdir()

    assert link_orphan_to_parent(vault, "Resources/Orphan.md") is None


# ── get_native_orphans ────────────────────────────────────────────────────────


@patch("subprocess.run")
def test_get_native_orphans_success(mock_run):
    mock_run.return_value = MagicMock(returncode=0, stdout="Areas/One.md\n\n  Areas/Two.md \n")

    assert get_native_orphans() == ["Areas/One.md", "Areas/Two.md"]
    assert mock_run.call_args.args[0] == ["obsidian", "orphans"]


@patch("subprocess.run")
def test_get_native_orphans_nonzero_exit(mock_run):
    mock_run.return_value = MagicMock(returncode=1, stdout="")

    assert get_native_orphans() == []


@patch("subprocess.run")
def test_get_native_orphans_missing_binary(mock_run):
    mock_run.side_effect = FileNotFoundError("obsidian not installed")

    assert get_native_orphans() == []


# ── run_list_orphans ──────────────────────────────────────────────────────────


@patch("rig.obsidian.orphan_manager.get_vault_path")
@patch("rig.obsidian.orphan_manager.get_native_orphans")
def test_run_list_orphans_logs_each_orphan(mock_native, mock_vault_path, mock_vault):
    mock_vault_path.return_value = mock_vault
    mock_native.return_value = []
    logs: list[str] = []

    result = run_list_orphans(mock_vault, log=logs.append)

    assert "Areas/Career.md" in result
    assert "Resources/Orphan.md" in result
    assert logs == result


@patch("rig.obsidian.orphan_manager.get_vault_path")
@patch("rig.obsidian.orphan_manager.get_native_orphans")
def test_run_list_orphans_without_log(mock_native, mock_vault_path, mock_vault):
    mock_vault_path.return_value = mock_vault
    mock_native.return_value = []

    result = run_list_orphans(mock_vault)

    assert "Resources/Orphan.md" in result


@patch("rig.obsidian.orphan_manager.get_vault_path")
@patch("rig.obsidian.orphan_manager.get_native_orphans")
def test_run_list_orphans_compare_renders_cross_check(mock_native, mock_vault_path, mock_vault, capsys):
    mock_vault_path.return_value = mock_vault
    # First call inside find_orphans returns nothing (forces regex scan);
    # second call feeds the comparison table.
    mock_native.side_effect = [
        [],
        ["Resources/Orphan.md", "Resources/OnlyNative.md", "Templates/T1.md"],
    ]
    logs: list[str] = []

    result = run_list_orphans(mock_vault, log=logs.append, compare=True)

    out = capsys.readouterr().out
    assert "Orphan Cross-Check" in out
    assert "Summary:" in out
    assert "Both: 1" in out
    assert "Only Custom: 1" in out
    assert "Only Native (Filtered): 2" in out  # count is unfiltered in the summary line
    assert "Resources/Orphan.md" in result


@patch("rig.obsidian.orphan_manager.get_vault_path")
@patch("rig.obsidian.orphan_manager.get_native_orphans")
def test_run_list_orphans_compare_without_log(mock_native, mock_vault_path, mock_vault, capsys):
    mock_vault_path.return_value = mock_vault
    mock_native.side_effect = [[], []]

    result = run_list_orphans(mock_vault, compare=True)

    assert "Orphan Cross-Check" not in capsys.readouterr().out
    assert "Resources/Orphan.md" in result


# ── run_link_orphans ──────────────────────────────────────────────────────────


@patch("rig.obsidian.orphan_manager.get_vault_path")
@patch("rig.obsidian.orphan_manager.get_native_orphans")
def test_run_link_orphans_links_and_counts(mock_native, mock_vault_path, mock_vault):
    mock_vault_path.return_value = mock_vault
    mock_native.return_value = []
    (mock_vault / "Resources.md").touch()
    logs: list[str] = []

    result = run_link_orphans(mock_vault, log=logs.append)

    assert result.orphans_found >= 2
    assert result.linked == 1
    assert any("Resources/Orphan.md -> Resources.md" in msg for msg in logs)
    assert "[[Resources/Orphan.md]]" in (mock_vault / "Resources.md").read_text(encoding="utf-8")


@patch("rig.obsidian.orphan_manager.get_vault_path")
@patch("rig.obsidian.orphan_manager.get_native_orphans")
def test_run_link_orphans_without_log_still_counts(mock_native, mock_vault_path, mock_vault):
    mock_vault_path.return_value = mock_vault
    mock_native.return_value = []
    (mock_vault / "Resources.md").touch()

    result = run_link_orphans(mock_vault)

    # the linked counter is independent of the optional log callback
    assert result.linked == 1
    assert "[[Resources/Orphan.md]]" in (mock_vault / "Resources.md").read_text(encoding="utf-8")


# ── run_root_hub ──────────────────────────────────────────────────────────────


@patch("rig.obsidian.orphan_manager.get_vault_path")
@patch("rig.obsidian.orphan_manager.get_native_orphans")
def test_run_root_hub_logs_links_and_is_idempotent(mock_native, mock_vault_path, mock_vault):
    mock_vault_path.return_value = mock_vault
    mock_native.return_value = []
    (mock_vault / "Projects").mkdir()
    (mock_vault / "Projects" / "P1.md").touch()
    logs: list[str] = []

    first = run_root_hub(log=logs.append)
    assert first.hubs_created >= 1
    assert first.links_added >= 1
    assert any("Linked Projects/P1.md" in msg for msg in logs)

    second = run_root_hub(log=logs.append)
    assert second.hubs_created == 0
    assert second.links_added == 0


@patch("rig.obsidian.orphan_manager.get_vault_path")
@patch("rig.obsidian.orphan_manager.get_native_orphans")
def test_run_root_hub_with_existing_home(mock_native, mock_vault_path, mock_vault):
    mock_vault_path.return_value = mock_vault
    mock_native.return_value = []
    (mock_vault / "Home.md").write_text("# Existing Home\n", encoding="utf-8")

    result = run_root_hub()

    home_content = (mock_vault / "Home.md").read_text(encoding="utf-8")
    assert "# Existing Home" in home_content
    assert "[[Resources]]" in home_content
    assert result.hubs_created >= 2  # Areas.md + Resources.md at minimum

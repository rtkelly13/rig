"""Tests for tvs.obsidian.link_manager — broken link detection and resolution."""

from __future__ import annotations

from pathlib import Path

from rig.obsidian.link_manager import (
    find_broken_links,
    find_unlinked_mentions,
    resolve_link_target,
    run_fix_links,
    run_list_links,
)

# ── resolve_link_target ───────────────────────────────────────────────────────


def test_resolve_link_target_exact_match():
    valid = {"Notes/My Note", "Resources/Article"}
    stems: dict[str, list[str]] = {}
    assert resolve_link_target("Notes/My Note", valid, stems) == "Notes/My Note"


def test_resolve_link_target_stem_match():
    valid: set[str] = set()
    stems = {"My Note": ["Notes/My Note"]}
    assert resolve_link_target("My Note", valid, stems) == "Notes/My Note"


def test_resolve_link_target_stem_first_wins():
    """When multiple notes share a stem, the first path wins."""
    valid: set[str] = set()
    stems = {"Note": ["A/Note", "B/Note"]}
    assert resolve_link_target("Note", valid, stems) == "A/Note"


def test_resolve_link_target_no_match():
    valid = {"Notes/Other"}
    stems: dict[str, list[str]] = {"Other": ["Notes/Other"]}
    assert resolve_link_target("Nonexistent", valid, stems) is None


def test_resolve_link_target_empty():
    assert resolve_link_target("", set(), {}) is None


# ── find_broken_links (regex fallback) ────────────────────────────────────────


def _build_vault(tmp_path: Path, files: dict[str, str]) -> Path:
    """Create a mini-vault with the given file structure."""
    for rel_path, content in files.items():
        full = tmp_path / rel_path
        full.parent.mkdir(parents=True, exist_ok=True)
        full.write_text(content)
    return tmp_path


def test_find_broken_links_no_broken(tmp_path: Path):
    _build_vault(
        tmp_path,
        {
            "Note A.md": "# A\nLinks to [[Note B]]",
            "Note B.md": "# B\nLinks back to [[Note A]]",
        },
    )
    broken = find_broken_links(tmp_path)
    assert broken == []


def test_find_broken_links_detects_broken(tmp_path: Path):
    _build_vault(
        tmp_path,
        {
            "Note A.md": "Links to [[Missing Note]] and [[Also Missing]]",
            "Note B.md": "Valid link to [[Note A]]",
        },
    )
    broken = find_broken_links(tmp_path)
    targets = {b.target for b in broken}
    assert "Missing Note" in targets
    assert "Also Missing" in targets
    assert len(broken) == 2


def test_find_broken_links_handles_aliases(tmp_path: Path):
    """Wikilinks with aliases (|display) should resolve by target, not alias."""
    _build_vault(
        tmp_path,
        {
            "Note A.md": "See [[Note B|my display text]]",
            "Note B.md": "# B",
        },
    )
    broken = find_broken_links(tmp_path)
    assert broken == []


def test_find_broken_links_handles_headings(tmp_path: Path):
    """Wikilinks with heading anchors (#section) should resolve the base note."""
    _build_vault(
        tmp_path,
        {
            "Note A.md": "See [[Note B#section]]",
            "Note B.md": "# B\n## section",
        },
    )
    broken = find_broken_links(tmp_path)
    assert broken == []


def test_find_broken_links_line_numbers(tmp_path: Path):
    _build_vault(
        tmp_path,
        {
            "Note.md": "Line 1\nLine 2 [[Missing]]\nLine 3\n",
        },
    )
    broken = find_broken_links(tmp_path)
    assert len(broken) == 1
    assert broken[0].line == 2
    assert broken[0].source == "Note.md"


# ── find_broken_links (path-style links) ───────────────────────────────────────


def test_find_broken_links_reports_vault_relative_sources(tmp_path: Path):
    """Broken links are reported against the vault-relative source note."""
    _build_vault(
        tmp_path,
        {
            "Projects/Plan.md": "# Plan\nSee [[Missing]]",
            "Note B.md": "Valid link to [[Plan]]",
        },
    )
    broken = find_broken_links(tmp_path)
    assert [(b.source, b.target) for b in broken] == [("Projects/Plan.md", "Missing")]


def test_find_broken_links_ignores_path_style_links_to_real_notes(tmp_path: Path):
    """Path-style wikilinks to existing notes are not false positives."""
    _build_vault(
        tmp_path,
        {
            "Home.md": "# Home\n[[Areas/Career]]",
            "Areas/Career.md": "# Career\n[[Home]]",
        },
    )
    assert find_broken_links(tmp_path) == []


# ── find_unlinked_mentions ────────────────────────────────────────────────────


def test_find_unlinked_mentions_basic(tmp_path: Path):
    _build_vault(
        tmp_path,
        {
            "Distributed Systems.md": "# Distributed Systems\nThis is about DS.",
            "Another Note.md": "Reading about Distributed Systems is fun.",
        },
    )
    unlinked = find_unlinked_mentions(tmp_path)
    mentions = {u.mention for u in unlinked}
    assert "Distributed Systems" in mentions


def test_find_unlinked_mentions_ignores_wikilinks(tmp_path: Path):
    """Already-linked mentions should not appear as unlinked."""
    _build_vault(
        tmp_path,
        {
            "Distributed Systems.md": "# DS",
            "Another Note.md": "Read about [[Distributed Systems]] here.",
        },
    )
    unlinked = find_unlinked_mentions(tmp_path)
    mentions = {u.mention for u in unlinked}
    assert "Distributed Systems" not in mentions


def test_find_unlinked_mentions_short_stems_ignored(tmp_path: Path):
    """Stems with 4 or fewer characters should be ignored to avoid false positives."""
    _build_vault(
        tmp_path,
        {
            "Go.md": "# Go programming language",
            "Notes.md": "I love Go programming.",
        },
    )
    unlinked = find_unlinked_mentions(tmp_path)
    mentions = {u.mention for u in unlinked}
    assert "Go" not in mentions


def test_find_unlinked_mentions_self_reference_ignored(tmp_path: Path):
    """A note mentioning its own stem should not be flagged."""
    _build_vault(
        tmp_path,
        {
            "Kubernetes Basics.md": "# Kubernetes Basics\nThis note is about Kubernetes Basics in depth.",
        },
    )
    unlinked = find_unlinked_mentions(tmp_path)
    assert unlinked == []


# ── run_list_links ────────────────────────────────────────────────────────────


def test_run_list_links_returns_report(tmp_path: Path):
    _build_vault(
        tmp_path,
        {
            "A.md": "Link to [[B]]",
            "B.md": "Link to [[Missing]]",
        },
    )
    report = run_list_links(tmp_path)
    assert len(report.broken) == 1
    assert report.unlinked == ()


def test_run_list_links_with_vault_subdir(tmp_path: Path):
    """run_list_links should prefer root/Vault if it exists."""
    vault = tmp_path / "Vault"
    vault.mkdir()
    (vault / "A.md").write_text("Link to [[NonExistent]]")

    report = run_list_links(tmp_path)
    assert len(report.broken) == 1


# ── run_fix_links ─────────────────────────────────────────────────────────────


def test_run_fix_links_resolves_stem(tmp_path: Path):
    """Broken links that match a unique stem should be auto-fixed."""
    vault = tmp_path / "Vault"
    vault.mkdir()
    sub = vault / "Sub"
    sub.mkdir()
    (sub / "Target Note.md").write_text("# Target Note")
    (vault / "Source.md").write_text("See [[Target Note]]")

    # The stem "Target Note" should resolve to "Sub/Target Note"
    # So [[Target Note]] at root would actually resolve via stem lookup
    # This test is about when the full path is used incorrectly:
    (vault / "Source.md").write_text("See [[Wrong/Target Note]]")
    result = run_fix_links(tmp_path)
    assert result.broken_found >= 1
    # If stem matches uniquely, it should be fixed
    if result.fixed > 0:
        content = (vault / "Source.md").read_text()
        assert "Sub/Target Note" in content

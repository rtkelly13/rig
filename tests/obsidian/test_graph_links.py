"""Feature: wikilink health maintenance via the `vault graph links` CLI.

A user scans the vault for dead wikilinks and unlinked mentions with
`vault graph links list`, then repairs fixable links with
`vault graph links fix --apply`.
"""

from __future__ import annotations

from pathlib import Path

from typer.testing import CliRunner

from rig.obsidian.cli import app


def _note(path: Path, body: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(body, encoding="utf-8")


def test_list_reports_clean_vault(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: a vault whose wikilinks all resolve reports no problems."""
    # Given two notes that link to each other correctly
    _note(vault / "Home.md", "# Home\n\n- [[Areas/Career]]\n")
    _note(vault / "Areas" / "Career.md", "# Career\n\n[[Home]]\n")

    # When the user lists link problems
    result = cli.invoke(app, ["graph", "links", "list"])

    # Then the vault is reported clean
    assert result.exit_code == 0
    assert "No broken links or unlinked mentions found" in result.output


def test_list_reports_broken_links(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: notes pointing at missing targets are listed as broken links."""
    # Given a note that links to a note which does not exist
    _note(vault / "Projects" / "Plan.md", "# Plan\n\nSee [[Gone Forever]]\n")

    # When the user lists link problems
    result = cli.invoke(app, ["graph", "links", "list"])

    # Then the dead wikilink is reported with its source location
    assert result.exit_code == 0
    assert "Broken Links" in result.output
    assert "Projects/Plan.md" in result.output
    assert "Gone Forever" in result.output


def test_list_reports_unlinked_mentions(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: a note title mentioned in plain text shows up as an unlinked mention."""
    # Given a note whose title appears un-linked in another note's body
    _note(vault / "Areas" / "Career Plan.md", "# Career Plan\n")
    _note(vault / "Home.md", "# Home\n\nThe Career Plan needs review. [[Areas/Career Plan]]\n")

    # When the user lists link problems
    result = cli.invoke(app, ["graph", "links", "list"])

    # Then the mention is surfaced
    assert result.exit_code == 0
    assert "Unlinked Mentions" in result.output
    assert "Career Plan" in result.output


def test_list_truncates_long_mention_lists(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: more than twenty unlinked mentions are truncated with a count."""
    # Given one note mentioning 22 other note titles without linking them
    mentions: list[str] = []
    for index in range(22):
        stem = f"Topic Note {index:02d}"
        _note(vault / "Resources" / f"{stem}.md", f"# {stem}\n\n[[Home]]\n")
        mentions.append(f"read {stem} today.")
    _note(vault / "Home.md", "# Home\n\n" + "\n".join(mentions) + "\n")

    # When the user lists link problems
    result = cli.invoke(app, ["graph", "links", "list"])

    # Then the mention list is truncated with an "and N more" summary
    assert result.exit_code == 0
    assert "Unlinked Mentions" in result.output
    assert "more." in result.output


def test_fix_defaults_to_dry_run(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: fix without --apply changes nothing and says so."""
    # Given a note with a dead wikilink
    _note(vault / "Projects" / "Plan.md", "# Plan\n\nSee [[Gone Forever]]\n")

    # When the user runs fix without --apply
    result = cli.invoke(app, ["graph", "links", "fix"])

    # Then nothing is rewritten
    assert result.exit_code == 0
    assert "Dry run only" in result.output
    assert "[[Gone Forever]]" in (vault / "Projects" / "Plan.md").read_text(encoding="utf-8")


def test_fix_apply_rewrites_fixable_links(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: --apply rewrites links whose stem matches exactly one real note."""
    # Given a link with the wrong folder prefix and one truly missing target
    _note(vault / "Resources" / "Deep Dive.md", "# Deep Dive\n\ncontent\n")
    _note(vault / "Projects" / "Plan.md", "# Plan\n\nSee [[Projects/Deep Dive]] and [[Gone Forever]]\n")

    # When the user applies link fixes
    result = cli.invoke(app, ["graph", "links", "fix", "--apply"])

    # Then the fixable link is rewritten in place and the summary reflects it
    assert result.exit_code == 0
    assert "Fixed 1 of 2 broken links" in result.output
    content = (vault / "Projects" / "Plan.md").read_text(encoding="utf-8")
    assert "[[Resources/Deep Dive]]" in content
    assert "[[Gone Forever]]" in content

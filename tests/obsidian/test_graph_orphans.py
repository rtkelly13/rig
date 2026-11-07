"""Feature: orphan detection and PARA hub maintenance via the `vault graph` CLI.

A user finds notes without incoming links (`vault graph orphans list`),
attaches them to their parent hubs (`vault graph orphans link --apply`) and
maintains PARA root hub notes (`vault graph hubs --apply`).
"""

from __future__ import annotations

from pathlib import Path

from typer.testing import CliRunner

from rig.obsidian.cli import app


def _note(path: Path, body: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(body, encoding="utf-8")


def test_orphans_list_reports_unreferenced_notes(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: a note that nothing links to is listed as an orphan."""
    # Given a note that no other note links to
    _note(vault / "Projects" / "Lonely Idea.md", "# Lonely Idea\n")

    # When the user lists orphans
    result = cli.invoke(app, ["graph", "orphans", "list"])

    # Then the orphan path is printed
    assert result.exit_code == 0
    assert "Projects/Lonely Idea.md" in result.output


def test_orphans_list_reports_clean_vault(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: a vault where every note is referenced reports no orphans."""
    # Given a note that is linked from Home (Home itself is excluded from orphan checks)
    _note(vault / "Home.md", "# Home\n\n- [[Areas/Career]]\n")
    _note(vault / "Areas" / "Career.md", "# Career\n\n[[Home]]\n")

    # When the user lists orphans
    result = cli.invoke(app, ["graph", "orphans", "list"])

    # Then no orphans are reported
    assert result.exit_code == 0
    assert "No orphan notes found" in result.output


def test_orphans_link_defaults_to_dry_run(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: orphan linking without --apply changes nothing."""
    # Given an orphan note
    _note(vault / "Projects" / "Lonely Idea.md", "# Lonely Idea\n")

    # When the user runs orphan linking without --apply
    result = cli.invoke(app, ["graph", "orphans", "link"])

    # Then it is a dry run and the note is untouched
    assert result.exit_code == 0
    assert "Dry run only" in result.output
    assert (vault / "Projects" / "Lonely Idea.md").read_text(encoding="utf-8") == "# Lonely Idea\n"


def test_orphans_link_apply_attaches_orphan_to_parent_hub(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: --apply appends a wikilink for the orphan to its nearest parent hub."""
    # Given an orphan inside a folder that has a matching hub note
    _note(vault / "Resources" / "Python.md", "# Python Hub\n")
    _note(vault / "Resources" / "Python" / "Async Guide.md", "# Async Guide\n")

    # When the user applies orphan linking
    result = cli.invoke(app, ["graph", "orphans", "link", "--apply"])

    # Then the hub gains a link to the orphan and the summary counts it
    assert result.exit_code == 0
    assert "Linked 1 of" in result.output
    hub_content = (vault / "Resources" / "Python.md").read_text(encoding="utf-8")
    assert "[[Resources/Python/Async Guide.md]]" in hub_content


def test_hubs_defaults_to_dry_run(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: hub maintenance without --apply creates nothing."""
    # Given a vault without hub notes
    # When the user runs hubs without --apply
    result = cli.invoke(app, ["graph", "hubs"])

    # Then it is a dry run and no hub files appear
    assert result.exit_code == 0
    assert "Dry run only" in result.output
    assert not (vault / "Home.md").exists()


def test_hubs_apply_creates_root_hubs_and_links_orphans(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: --apply creates Home plus PARA hubs and links first-level orphans."""
    # Given a vault with an orphan directly under Projects
    _note(vault / "Projects" / "Roadmap.md", "# Roadmap\n")

    # When the user applies hub maintenance
    result = cli.invoke(app, ["graph", "hubs", "--apply"])

    # Then hub notes exist and the orphan was linked from its PARA hub
    assert result.exit_code == 0
    assert "Created" in result.output
    assert "root links" in result.output
    assert (vault / "Home.md").exists()
    assert (vault / "Projects.md").exists()
    assert "[[Projects/Roadmap.md]]" in (vault / "Projects.md").read_text(encoding="utf-8")


def test_hubs_apply_is_idempotent(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: re-applying hub maintenance creates no duplicates."""
    # Given hub maintenance has already been applied once
    _note(vault / "Projects" / "Roadmap.md", "# Roadmap\n")
    cli.invoke(app, ["graph", "hubs", "--apply"])

    # When the user applies it again
    result = cli.invoke(app, ["graph", "hubs", "--apply"])

    # Then nothing new is created or linked
    assert result.exit_code == 0
    assert "Created 0 hubs and added 0 root links" in result.output

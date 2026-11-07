"""Feature: vault linting and structural repair via the `vault graph` CLI.

A user scans for metadata failures, duplicates, and ugly filenames with
`vault graph lint`, then repairs them via the `vault graph repair` commands
(duplicates, backfill-types, metadata, normalize, filenames).
"""

from __future__ import annotations

from pathlib import Path

from typer.testing import CliRunner

from rig.obsidian.cli import app


def _note(path: Path, body: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(body, encoding="utf-8")


def _resource(path: Path, *, title: str, source: str, body: str = "content\n") -> None:
    _note(
        path,
        f'---\ntype: resource\ntitle: {title}\nsource: "{source}"\nstatus: active\ncreated: 2024-01-01\n---\n\n{body}',
    )


def test_lint_reports_clean_vault(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: a vault with complete metadata and no duplicates lints clean."""
    # Given one well-formed resource note
    _resource(vault / "Resources" / "Python" / "Async Guide.md", title="Async Guide", source="https://example.com/a")

    # When the user lints the vault
    result = cli.invoke(app, ["graph", "lint"])

    # Then the vault is clean
    assert result.exit_code == 0
    assert "Vault is clean" in result.output


def test_lint_reports_metadata_duplicates_and_filenames(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: metadata failures, duplicate groups and ugly filenames are all reported."""
    # Given a note missing its type, two notes sharing a URL, and an ugly filename
    _note(vault / "Resources" / "No Type.md", "# No Type\n\nplain body\n")
    _resource(vault / "Resources" / "One.md", title="Same Thing", source="https://example.com/dup")
    _resource(vault / "Resources" / "Two.md", title="Same Thing Two", source="https://example.com/dup")
    _resource(vault / "Resources" / "Python" / "my_ugly.note.md", title="My Ugly Note", source="https://example.com/u")

    # When the user lints the vault
    result = cli.invoke(app, ["graph", "lint"])

    # Then every category of problem is reported
    assert result.exit_code == 0
    assert "metadata errors" in result.output
    assert "duplicate groups" in result.output
    assert "filename issues" in result.output


def test_lint_json_reports_structured_findings(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: --json emits a machine-readable lint report."""
    import json

    # Given a note missing required metadata
    _note(vault / "Resources" / "No Type.md", "# No Type\n\nplain body\n")

    # When the user lints with --json
    result = cli.invoke(app, ["graph", "lint", "--json"])

    # Then the report parses and flags the failure
    assert result.exit_code == 0
    report = json.loads(result.output)
    assert report["ok"] is False
    assert report["metadata"]["errors"]


def test_repair_duplicates_defaults_to_dry_run(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: duplicate cleanup without --apply only previews the changes."""
    # Given two resources sharing the same source URL
    _resource(
        vault / "Resources" / "Python" / "Async Guide.md",
        title="Async Guide",
        source="https://example.com/async",
        body="many words " * 40,
    )
    _resource(
        vault / "Resources" / "Python" / "Async Guide (1).md",
        title="Async Guide",
        source="https://example.com/async",
        body="short\n",
    )

    # When the user runs duplicate cleanup without --apply
    result = cli.invoke(app, ["graph", "repair", "duplicates"])

    # Then the loser is only announced, not archived
    assert result.exit_code == 0
    assert "Would archive duplicate" in result.output
    assert (vault / "Resources" / "Python" / "Async Guide (1).md").exists()


def test_repair_duplicates_apply_archives_losers_and_relinks_social(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: --apply archives duplicate resources and points social posts at the winner."""
    # Given duplicate resources and an unlinked social post on the same URL
    _resource(
        vault / "Resources" / "Python" / "Async Guide.md",
        title="Async Guide",
        source="https://example.com/async",
        body="many words " * 40,
    )
    _resource(
        vault / "Resources" / "Python" / "Async Guide (1).md",
        title="Async Guide",
        source="https://example.com/async",
        body="short\n",
    )
    _note(
        vault / "Social Media Posts" / "Async Post.md",
        '---\ntype: social\ntitle: Async Post\nsource: "https://example.com/async"\n'
        "status: draft\ncreated: 2024-01-01\n---\n\nCheck this out\n",
    )

    # When the user applies duplicate cleanup
    result = cli.invoke(app, ["graph", "repair", "duplicates", "--apply"])

    # Then the social post is relinked and the weaker resource is archived
    assert result.exit_code == 0
    social = (vault / "Social Media Posts" / "Async Post.md").read_text(encoding="utf-8")
    assert "related_article" in social
    assert not (vault / "Resources" / "Python" / "Async Guide (1).md").exists()
    assert list((vault / "Archive" / "Duplicates").glob("*.md"))


def test_backfill_types_defaults_to_dry_run(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: type backfill without --apply leaves frontmatter untouched."""
    # Given a resource note without a type field
    _note(vault / "Resources" / "Python" / "Untyped.md", "# Untyped\n\nbody\n")

    # When the user runs backfill without --apply
    result = cli.invoke(app, ["graph", "repair", "backfill-types"])

    # Then the change is only previewed
    assert result.exit_code == 0
    assert "Would update type" in result.output
    assert "type:" not in (vault / "Resources" / "Python" / "Untyped.md").read_text(encoding="utf-8")


def test_backfill_types_apply_writes_type_field(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: --apply adds a folder-derived type to the frontmatter."""
    # Given a resource note without a type field
    _note(vault / "Resources" / "Python" / "Untyped.md", "# Untyped\n\nbody\n")

    # When the user applies the backfill
    result = cli.invoke(app, ["graph", "repair", "backfill-types", "--apply"])

    # Then the note now declares its type
    assert result.exit_code == 0
    assert "Updated type" in result.output
    assert "type: resource" in (vault / "Resources" / "Python" / "Untyped.md").read_text(encoding="utf-8")


def test_repair_metadata_defaults_to_dry_run(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: metadata repair without --apply only previews."""
    # Given a typed note missing status and created fields
    _note(vault / "Areas" / "Career.md", "---\ntype: area\n---\n\n# Career\n")

    # When the user runs metadata repair without --apply
    result = cli.invoke(app, ["graph", "repair", "metadata"])

    # Then the repair is only previewed
    assert result.exit_code == 0
    assert "Would repair metadata" in result.output
    assert "status:" not in (vault / "Areas" / "Career.md").read_text(encoding="utf-8")


def test_repair_metadata_apply_backfills_fields(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: --apply backfills status and created based on the note file."""
    # Given a typed note missing status and created fields
    _note(vault / "Areas" / "Career.md", "---\ntype: area\n---\n\n# Career\n")

    # When the user applies metadata repair
    result = cli.invoke(app, ["graph", "repair", "metadata", "--apply"])

    # Then the missing fields are written
    assert result.exit_code == 0
    content = (vault / "Areas" / "Career.md").read_text(encoding="utf-8")
    assert "status: active" in content
    assert "created:" in content


def test_repair_normalize_applies_both_phases(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: normalize runs the type backfill and the metadata repair together."""
    # Given an untyped note in Resources
    _note(vault / "Resources" / "Python" / "Untyped.md", "# Untyped\n\nSource: https://example.com/x\n")

    # When the user applies normalization
    result = cli.invoke(app, ["graph", "repair", "normalize", "--apply"])

    # Then the note gains a type plus repaired metadata
    assert result.exit_code == 0
    content = (vault / "Resources" / "Python" / "Untyped.md").read_text(encoding="utf-8")
    assert "type: resource" in content
    assert "status: active" in content


def test_repair_filenames_defaults_to_dry_run(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: filename cleanup without --apply only previews renames."""
    # Given a note with an ugly filename (in a subfolder — root-level notes are hub-like and exempt)
    _resource(vault / "Resources" / "Python" / "my_ugly.note.md", title="My Ugly Note", source="https://example.com/u")

    # When the user runs filename cleanup without --apply
    result = cli.invoke(app, ["graph", "repair", "filenames"])

    # Then the rename is only previewed
    assert result.exit_code == 0
    assert "Would rename" in result.output
    assert (vault / "Resources" / "Python" / "my_ugly.note.md").exists()


def test_repair_filenames_apply_renames_and_updates_links(cli: CliRunner, vault: Path, data_dir: Path):
    """Scenario: --apply renames the note and rewrites wikilinks pointing at it."""
    # Given an ugly filename referenced from another note
    _resource(vault / "Resources" / "Python" / "my_ugly.note.md", title="My Ugly Note", source="https://example.com/u")
    _note(vault / "Home.md", "# Home\n\n[[my_ugly.note]]\n")

    # When the user applies filename cleanup
    result = cli.invoke(app, ["graph", "repair", "filenames", "--apply"])

    # Then the file is renamed and the reference updated
    assert result.exit_code == 0
    assert "Renamed" in result.output
    assert (vault / "Resources" / "Python" / "my-ugly-note.md").exists()
    assert not (vault / "Resources" / "Python" / "my_ugly.note.md").exists()
    assert "[[my-ugly-note]]" in (vault / "Home.md").read_text(encoding="utf-8")

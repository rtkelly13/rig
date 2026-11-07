import sys
from pathlib import Path, PurePosixPath

import pytest

from rig.obsidian.lint_manager import VaultLinter, run_vault_lint
from rig.obsidian.lint_rules import (
    FilenameIssue,
    MetadataReport,
    filename_issue_specs,
    windows_portability_issue_reason,
)
from rig.obsidian.utils import suggest_clean_filename_stem

needs_colon_filenames = pytest.mark.skipif(
    sys.platform == "win32",
    reason="NTFS reads ':' in a filename as an alternate data stream, so the fixture file cannot exist",
)


def test_find_filename_issues_suggests_clean_social_name(tmp_path):
    vault = tmp_path / "Vault"
    social = vault / "Social Media Posts" / "Other"
    social.mkdir(parents=True)

    ugly = social / "https---twitter.com-JetBrains_Aqua-social.md"
    ugly.write_text(
        """---
type: social
source: https://twitter.com/JetBrains_Aqua
related_article: "[[JetBrains Aqua]]"
status: draft
created: 2026-03-22 12:00
title: Social Draft for https://twitter.com/JetBrains_Aqua
---

# Draft
""",
        encoding="utf-8",
    )

    issues = VaultLinter(vault).find_filename_issues()

    assert {issue.code for issue in issues} == {"TVS101", "TVS103", "TVS104", "TVS105"}
    assert all(issue.path == ugly for issue in issues)
    assert all(issue.suggested_stem == "jetbrains-aqua-social" for issue in issues)
    assert all(issue.suggested_path.name == "jetbrains-aqua-social.md" for issue in issues)


def test_suggest_clean_filename_stem_recovers_from_sanitized_url_stem():
    assert suggest_clean_filename_stem("https---twitter.com-JetBrains_Aqua-social") == "jetbrains-aqua-social"


def test_suggest_clean_filename_stem_collapses_middling_dots():
    assert (
        suggest_clean_filename_stem(
            "n-gate.com. we can't both be right",
            source_url="https://n-gate.com/blog/we-cant-both-be-right",
        )
        == "we-cant-both-be-right"
    )


def test_suggest_clean_filename_stem_preserves_good_dotnet_terms():
    assert (
        suggest_clean_filename_stem(
            "structured-logging-for-net-lambda",
            title="Structured Logging for .NET Lambda",
            source_url="https://aws.amazon.com/blogs/developer/structured-logging-for-net-lambda/",
        )
        == "structured-logging-for-dotnet-lambda"
    )


def test_suggest_clean_filename_stem_keeps_asp_dotnet_slug_readable():
    assert (
        suggest_clean_filename_stem(
            "ajax-paging-for-asp-net-mvc-sites",
            title="AJAX paging for ASP.NET MVC sites",
            source_url="https://www.lybecker.com/blog/2014/09/29/ajax-paging-for-asp-net-mvc-sites/",
        )
        == "ajax-paging-for-asp-dotnet-mvc-sites"
    )


def test_suggest_clean_filename_stem_rewrites_dotted_name_to_hyphenated_name():
    assert suggest_clean_filename_stem("Draw.IO", title="Draw.IO", source_url="https://app.diagrams.net/") == "draw-io"


def test_suggest_clean_filename_stem_avoids_generic_google_docs_slug():
    assert (
        suggest_clean_filename_stem(
            "Accounting Epics v1.0 - Google Sheets",
            source_url="https://docs.google.com/spreadsheets/d/1P6UlHpGx1OivugXQLO9fw4T7bx2BTAaejmOea9ekL-M/edit#gid=0",
        )
        == "accounting-epics-v1-0-google-sheets"
    )


def test_suggest_clean_filename_stem_avoids_generic_youtube_watch_slug():
    assert (
        suggest_clean_filename_stem(
            "Diagrams as Code 2.0 (YouTube)",
            source_url="https://www.youtube.com/watch?v=Za1-v4Zkq5E",
        )
        == "diagrams-as-code-2-0-youtube"
    )


def test_suggest_clean_filename_stem_avoids_generic_forum_slug():
    assert (
        suggest_clean_filename_stem(
            "ScriptMODU9RC6.1 Sticky 'n Smooth ISCream-=V6 SUPERCHARGER!=-The ONLY RAM Fixer - xda-developers",
            source_url="http://forum.xda-developers.com/showthread.php?t=991276",
        )
        == "scriptmodu9rc6-1-sticky-n-smooth-iscream-v6-supercharger-the-only-ram-fixer"
    )


def test_find_filename_issues_ignores_clean_social_name(tmp_path):
    vault = tmp_path / "Vault"
    social = vault / "Social Media Posts" / "Other"
    social.mkdir(parents=True)

    clean = social / "jetbrains-aqua-social.md"
    clean.write_text(
        """---
type: social
source: https://twitter.com/JetBrains_Aqua
related_article: "[[JetBrains Aqua]]"
status: draft
created: 2026-03-22 12:00
title: JetBrains Aqua
---

# Draft
""",
        encoding="utf-8",
    )

    assert VaultLinter(vault).find_filename_issues() == []


def test_find_filename_issues_flags_dots_in_filename(tmp_path):
    vault = tmp_path / "Vault"
    tools = vault / "Resources" / "Tools"
    tools.mkdir(parents=True)

    dotted = tools / "Draw.IO.md"
    dotted.write_text(
        """---
type: resource
source: https://app.diagrams.net/
status: active
created: 2026-03-22 12:00
title: Draw.IO
---

# Draw.IO
""",
        encoding="utf-8",
    )

    issues = VaultLinter(vault).find_filename_issues()

    assert {issue.code for issue in issues} == {"TVS101", "TVS105"}
    assert all(issue.suggested_path.name == "draw-io.md" for issue in issues)


def test_find_filename_issues_flags_dot_heavy_stem(tmp_path):
    vault = tmp_path / "Vault"
    resources = vault / "Resources" / "Other"
    resources.mkdir(parents=True)

    ugly = resources / "n-gate.com. we can't both be right.md"
    ugly.write_text(
        """---
type: resource
source: https://n-gate.com/blog/we-cant-both-be-right
status: active
created: 2026-03-22 12:00
title: n-gate.com. we can't both be right
---

# Note
""",
        encoding="utf-8",
    )

    issues = VaultLinter(vault).find_filename_issues()

    assert {issue.code for issue in issues} == {"TVS101", "TVS105"}
    assert all(issue.suggested_path.name == "we-cant-both-be-right.md" for issue in issues)


def test_lint_ignore_suppresses_specific_filename_code(tmp_path):
    vault = tmp_path / "Vault"
    social = vault / "Social Media Posts" / "Other"
    social.mkdir(parents=True)

    ugly = social / "https---twitter.com-JetBrains_Aqua-social.md"
    ugly.write_text(
        """---
type: social
lint_ignore:
  - TVS101
source: https://twitter.com/JetBrains_Aqua
related_article: "[[JetBrains Aqua]]"
status: draft
created: 2026-03-22 12:00
title: Social Draft for https://twitter.com/JetBrains_Aqua
---

# Draft
""",
        encoding="utf-8",
    )

    issues = VaultLinter(vault).find_filename_issues()

    assert {issue.code for issue in issues} == {"TVS103", "TVS104", "TVS105"}


def test_windows_portability_reason_flags_invalid_characters():
    assert (
        windows_portability_issue_reason(PurePosixPath("Resources/Engineering/Guide to 1:1 Meetings.md"))
        == "path is not portable to Windows"
    )


def test_windows_portability_reason_flags_reserved_device_names():
    assert windows_portability_issue_reason(PurePosixPath("Resources/Engineering/CON.md")) == (
        "path is not portable to Windows"
    )


def test_windows_portability_reason_allows_normal_path():
    assert windows_portability_issue_reason(PurePosixPath("Resources/Engineering/Guide to 1-1 Meetings.md")) is None


@needs_colon_filenames
def test_lint_ignore_suppresses_windows_portability_warning(tmp_path):
    vault = tmp_path / "Vault"
    resources = vault / "Resources" / "Engineering"
    resources.mkdir(parents=True)

    invalid = resources / "Guide to 1:1 Meetings.md"
    invalid.write_text(
        """---
type: resource
lint_ignore:
  - TVS108
source: https://example.com/guide-to-one-on-ones
status: active
created: 2026-03-22 12:00
title: Guide to 1:1 Meetings
---

# Guide to 1:1 Meetings
""",
        encoding="utf-8",
    )

    assert VaultLinter(vault).find_filename_issues() == []


@needs_colon_filenames
def test_find_filename_issues_flags_windows_invalid_character_and_suggests_portable_name(tmp_path):
    vault = tmp_path / "Vault"
    resources = vault / "Resources" / "Engineering"
    resources.mkdir(parents=True)

    invalid = resources / "Guide to 1:1 Meetings.md"
    invalid.write_text(
        """---
type: resource
source: https://example.com/guide-to-one-on-ones
status: active
created: 2026-03-22 12:00
title: Guide to 1:1 Meetings
---

# Guide to 1:1 Meetings
""",
        encoding="utf-8",
    )

    issues = VaultLinter(vault).find_filename_issues()

    assert [issue.code for issue in issues] == ["TVS108"]
    assert issues[0].suggested_path.name == "guide-to-1-1-meetings.md"


def test_find_filename_issues_flags_windows_reserved_name_and_suggests_portable_name(tmp_path):
    vault = tmp_path / "Vault"
    resources = vault / "Resources" / "Engineering"
    resources.mkdir(parents=True)

    invalid = resources / "CON.md"
    invalid.write_text(
        """---
type: resource
source: https://example.com/con-device-name
status: active
created: 2026-03-22 12:00
title: CON
---

# CON
""",
        encoding="utf-8",
    )

    issues = VaultLinter(vault).find_filename_issues()

    assert [issue.code for issue in issues] == ["TVS108"]
    assert issues[0].suggested_path.name == "con-device-name.md"


def test_metadata_failures_include_codes_and_ignore(tmp_path):
    vault = tmp_path / "Vault"
    resources = vault / "Resources" / "AI"
    resources.mkdir(parents=True)

    ignored = resources / "Ignored.md"
    ignored.write_text(
        """---
type: resource
lint_ignore:
  - TVS002
status: active
created: 2026-03-22 12:00
---

# Ignored
""",
        encoding="utf-8",
    )
    warned = resources / "Warned.md"
    warned.write_text(
        """---
type: strange-note
status: active
created: 2026-03-22 12:00
---

# Warned
""",
        encoding="utf-8",
    )

    failures = VaultLinter(vault).find_metadata_failures()

    assert len(failures) == 1
    assert failures[0].path == warned
    assert failures[0].code == "TVS005"
    assert failures[0].severity == "warning"


def test_folder_lint_stats_counts_filename_issues(tmp_path):
    vault = tmp_path / "Vault"
    resources = vault / "Resources" / "AI"
    resources.mkdir(parents=True)

    (resources / "https---example.com-Awesome_AI.md").write_text(
        """---
type: resource
source: https://example.com/awesome-ai
status: active
created: 2026-03-22 12:00
title: https://example.com/awesome-ai
---

# Ugly filename
""",
        encoding="utf-8",
    )

    stats = VaultLinter(vault).get_folder_lint_stats()

    folder_key = next(iter(stats))
    assert folder_key.endswith("AI")
    assert stats[folder_key].filename_issues == 4
    assert stats[folder_key].metadata_errors == 0
    assert stats[folder_key].metadata_warnings == 0


def test_cleanup_filenames_renames_note_and_updates_links(tmp_path):
    vault = tmp_path / "Vault"
    resources = vault / "Resources" / "AI"
    social = vault / "Social Media Posts" / "Other"
    resources.mkdir(parents=True)
    social.mkdir(parents=True)

    ugly = social / "https---twitter.com-JetBrains_Aqua-social.md"
    ugly.write_text(
        """---
type: social
source: https://twitter.com/JetBrains_Aqua
related_article: "[[JetBrains Aqua]]"
status: draft
created: 2026-03-22 12:00
title: Social Draft for https://twitter.com/JetBrains_Aqua
---

# Draft
See [[https---twitter.com-JetBrains_Aqua-social]].
""",
        encoding="utf-8",
    )
    resource = resources / "JetBrains Aqua.md"
    resource.write_text(
        """---
type: resource
source: https://blog.jetbrains.com/aqua
status: active
created: 2026-03-22 12:00
title: JetBrains Aqua
---

# JetBrains Aqua

Related social: [[https---twitter.com-JetBrains_Aqua-social]].
""",
        encoding="utf-8",
    )

    VaultLinter(vault).cleanup_filenames(dry_run=False)

    renamed = social / "jetbrains-aqua-social.md"
    assert renamed.exists()
    assert not ugly.exists()
    assert "[[jetbrains-aqua-social]]" in resource.read_text(encoding="utf-8")
    assert "[[jetbrains-aqua-social]]" in renamed.read_text(encoding="utf-8")


def test_cleanup_filenames_dry_run_does_not_rename(tmp_path):
    vault = tmp_path / "Vault"
    social = vault / "Social Media Posts" / "Other"
    social.mkdir(parents=True)

    ugly = social / "https---twitter.com-JetBrains_Aqua-social.md"
    ugly.write_text(
        """---
type: social
source: https://twitter.com/JetBrains_Aqua
related_article: "[[JetBrains Aqua]]"
status: draft
created: 2026-03-22 12:00
title: Social Draft for https://twitter.com/JetBrains_Aqua
---

# Draft
""",
        encoding="utf-8",
    )

    VaultLinter(vault).cleanup_filenames(dry_run=True)

    assert ugly.exists()
    assert not (social / "jetbrains-aqua-social.md").exists()


# ── helpers ──────────────────────────────────────────────────────────────────


def _write_note(path: Path, frontmatter_lines: list[str] | None, body: str = "# Note\n") -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    if frontmatter_lines is None:
        path.write_text(body, encoding="utf-8")
    else:
        path.write_text("---\n" + "\n".join(frontmatter_lines) + "\n---\n\n" + body, encoding="utf-8")
    return path


RESOURCE_FM = [
    "type: resource",
    "source: https://example.com/x",
    "status: active",
    "created: 2026-03-22 12:00",
]


# ── filename issue codes: non-ASCII, handle prefix, simplify ─────────────────


def test_find_filename_issues_flags_non_ascii(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(vault / "Resources" / "Other" / "Café Notes.md", RESOURCE_FM + ["title: Cafe Notes"])

    issues = VaultLinter(vault).find_filename_issues()

    assert "TVS102" in {issue.code for issue in issues}
    assert all(issue.suggested_stem == "cafe-notes" for issue in issues)


def test_find_filename_issues_flags_handle_prefix(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(vault / "Resources" / "Other" / "@jbrains-tweet.md", RESOURCE_FM + ["title: JBrains Tweet"])

    issues = VaultLinter(vault).find_filename_issues()

    assert "TVS106" in {issue.code for issue in issues}


def test_find_filename_issues_flags_underscore_separators(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(vault / "Resources" / "Other" / "weird--name_v2.md", RESOURCE_FM + ["title: Weird Name v2"])

    issues = VaultLinter(vault).find_filename_issues()

    codes = {issue.code for issue in issues}
    assert "TVS103" in codes
    assert "TVS104" in codes


def test_find_filename_issues_skips_paths_outside_para_roots(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(vault / "Journal" / "ugly__file.name.md", RESOURCE_FM)

    assert VaultLinter(vault).find_filename_issues() == []


def test_find_filename_issues_skips_hub_like_names(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(vault / "Resources" / "Inbox-Resources.md", ["type: resource-hub", "status: active"])

    assert VaultLinter(vault).find_filename_issues() == []


def test_find_filename_issues_logs_invalid_frontmatter_and_continues(tmp_path, caplog):
    vault = tmp_path / "Vault"
    broken = vault / "Resources" / "Other" / "bad__file.md"
    broken.parent.mkdir(parents=True)
    broken.write_text("---\nfoo: [unclosed\n---\n\n# Bad\n", encoding="utf-8")

    assert VaultLinter(vault).find_filename_issues() == []


def test_filename_issue_specs_simplify_fallback():
    """TVS107 fires only when no other spec matched but the stem still differs."""
    specs = filename_issue_specs("Clean Name", "clean-name")

    assert [spec.code for spec in specs] == ["TVS107"]


def test_filename_issue_specs_no_issue_for_matching_stem():
    assert filename_issue_specs("clean-name", "clean-name") == []


# ── wikilink rewriting ───────────────────────────────────────────────────────


def test_rewrite_wikilinks_handles_aliases_and_path_targets(tmp_path):
    vault = tmp_path / "Vault"
    vault.mkdir()
    linter = VaultLinter(vault)
    old = vault / "Resources" / "Old Name.md"
    new = vault / "Resources" / "new-name.md"

    content = (
        "Plain [[Old Name]] and alias [[Old Name|Friendly]] and "
        "path [[Resources/Old Name]] and file [[Resources/Old Name.md]] and "
        "embed ![[Old Name.md]] and other [[Unrelated]]."
    )
    result = linter._rewrite_wikilinks_in_text(content, old, new)

    assert "[[new-name]]" in result
    assert "[[new-name|Friendly]]" in result
    assert "[[Resources/new-name]]" in result
    assert "[[Resources/new-name.md]]" in result
    assert "![[new-name.md]]" in result
    assert "[[Unrelated]]" in result
    assert "Old Name" not in result


def test_update_wikilink_references_skips_hidden_dirs_and_logs_errors(tmp_path):
    vault = tmp_path / "Vault"
    (vault / ".obsidian").mkdir(parents=True)
    (vault / ".obsidian" / "config.md").write_text("[[Old Name]]", encoding="utf-8")
    (vault / "Resources").mkdir()
    (vault / "Resources" / "linker.md").write_text("[[Old Name]]", encoding="utf-8")
    # A directory matching *.md triggers the error branch
    (vault / "Resources" / "dir.md").mkdir()

    linter = VaultLinter(vault)
    updated = linter._update_wikilink_references(
        vault / "Resources" / "Old Name.md", vault / "Resources" / "new-name.md"
    )

    assert updated == 1
    assert (vault / ".obsidian" / "config.md").read_text(encoding="utf-8") == "[[Old Name]]"


# ── cleanup_filenames: conflict/skip branches ────────────────────────────────


def test_cleanup_filenames_skips_missing_source_file(tmp_path, monkeypatch, capsys):
    vault = tmp_path / "Vault"
    vault.mkdir()
    linter = VaultLinter(vault)
    ghost_issue = FilenameIssue(
        path=vault / "Resources" / "ghost__name.md",
        code="TVS103",
        reason="filename uses inconsistent separators",
        severity="warning",
        suggested_stem="ghost-name",
        suggested_path=vault / "Resources" / "ghost-name.md",
    )
    monkeypatch.setattr(linter, "find_filename_issues", lambda: [ghost_issue])

    linter.cleanup_filenames(dry_run=False)

    out = capsys.readouterr().out
    assert "Renamed 0 filename issues" in out
    assert "Skipped 1 filename issues" in out


def test_cleanup_filenames_skips_when_target_exists(tmp_path, capsys):
    vault = tmp_path / "Vault"
    ugly = _write_note(vault / "Resources" / "Other" / "weird__name.md", RESOURCE_FM + ["title: Weird Name"])
    _write_note(vault / "Resources" / "Other" / "weird-name.md", RESOURCE_FM + ["title: Weird Name Two"])

    VaultLinter(vault).cleanup_filenames(dry_run=False)

    out = capsys.readouterr().out
    assert "Skipping rename:" in out
    assert ugly.exists()


# ── find_metadata_failures: codes and suggestions ────────────────────────────


def test_metadata_failure_invalid_frontmatter(tmp_path):
    vault = tmp_path / "Vault"
    broken = vault / "Resources" / "AI" / "Broken.md"
    broken.parent.mkdir(parents=True)
    broken.write_text("---\nfoo: [unclosed\n---\n\n# Broken\n", encoding="utf-8")

    failures = VaultLinter(vault).find_metadata_failures()

    assert len(failures) == 1
    assert failures[0].code == "TVS000"
    assert failures[0].severity == "error"
    assert "Invalid frontmatter" in failures[0].reason


def test_metadata_failure_missing_type_includes_suggestion(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(vault / "Areas" / "Career" / "Growth.md", ["status: active"])

    failures = VaultLinter(vault).find_metadata_failures()

    assert len(failures) == 1
    assert failures[0].code == "TVS001"
    assert "Suggested: 'area'" in failures[0].reason


def test_metadata_failure_missing_required_field(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(
        vault / "Resources" / "AI" / "NoCreated.md",
        ["type: resource", "source: https://example.com/x", "status: active"],
    )

    failures = VaultLinter(vault).find_metadata_failures()

    assert [f.code for f in failures] == ["TVS002"]
    assert "missing required field: 'created'" in failures[0].reason


def test_metadata_failure_empty_string_field(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(
        vault / "Resources" / "AI" / "EmptySource.md",
        ["type: resource", 'source: ""', "status: active", "created: 2026-03-22 12:00"],
    )

    failures = VaultLinter(vault).find_metadata_failures()

    assert [f.code for f in failures] == ["TVS003"]
    assert "empty field: 'source'" in failures[0].reason


def test_metadata_failure_empty_required_list(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(
        vault / "Social Media Posts" / "AI" / "post.md",
        [
            "type: social",
            "source: https://example.com/x",
            "related_article: []",
            "status: draft",
            "created: 2026-03-22 12:00",
        ],
    )

    failures = VaultLinter(vault).find_metadata_failures()

    assert [f.code for f in failures] == ["TVS004"]
    assert "empty list: 'related_article'" in failures[0].reason


def test_metadata_failure_string_lint_ignore(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(
        vault / "Resources" / "AI" / "IgnoredWarning.md",
        ["type: strange-type", "lint_ignore: TVS005", "status: active", "created: 2026-03-22 12:00"],
    )

    assert VaultLinter(vault).find_metadata_failures() == []


def test_metadata_failure_all_ignore_suppresses_everything(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(vault / "Resources" / "AI" / "IgnoreAll.md", ["type: resource", "lint_ignore: ALL"])

    assert VaultLinter(vault).find_metadata_failures() == []


# ── _get_suggested_type via backfill_types ───────────────────────────────────


def test_backfill_types_dry_run_reports_without_writing(tmp_path, capsys):
    vault = tmp_path / "Vault"
    note = _write_note(vault / "Resources" / "AI" / "Untyped.md", ["status: active"])
    original = note.read_text(encoding="utf-8")

    VaultLinter(vault).backfill_types(dry_run=True)

    out = capsys.readouterr().out
    assert "Would update type from 'None' to 'resource'" in out
    assert "Found 1 notes missing 'type'." in out
    assert note.read_text(encoding="utf-8") == original


def test_backfill_types_applies_suggestions_per_folder(tmp_path, capsys):
    vault = tmp_path / "Vault"
    social = _write_note(vault / "Social Media Posts" / "AI" / "post.md", ["status: draft"])
    resource = _write_note(vault / "Resources" / "AI" / "Article.md", ["status: active"])
    design = _write_note(vault / "Projects" / "App" / "Design" / "Wireframes.md", ["status: active"])
    project_note = _write_note(vault / "Projects" / "App" / "Tasks.md", ["status: active"])
    area = _write_note(vault / "Areas" / "Career" / "Growth.md", ["status: active"])
    resources_hub = _write_note(vault / "Resources" / "AI" / "AI.md", ["status: active"])
    projects_hub = _write_note(vault / "Projects" / "App" / "App.md", ["status: active"])
    areas_hub = _write_note(vault / "Areas" / "Career" / "Career.md", ["status: active"])
    untouched = _write_note(vault / "Journal" / "2026-03-22.md", ["status: active"])

    VaultLinter(vault).backfill_types(dry_run=False)

    assert "type: social" in social.read_text(encoding="utf-8")
    assert "type: resource" in resource.read_text(encoding="utf-8")
    assert "type: design-note" in design.read_text(encoding="utf-8")
    assert "type: project-note" in project_note.read_text(encoding="utf-8")
    assert "type: area" in area.read_text(encoding="utf-8")
    assert "type: resource-hub" in resources_hub.read_text(encoding="utf-8")
    assert "type: project" in projects_hub.read_text(encoding="utf-8")
    assert "type: area" in areas_hub.read_text(encoding="utf-8")
    assert "type:" not in untouched.read_text(encoding="utf-8")
    assert "Fixed 8 notes missing 'type'." in capsys.readouterr().out


def test_backfill_types_refines_existing_generic_types(tmp_path):
    vault = tmp_path / "Vault"
    note_typed = _write_note(vault / "Resources" / "AI" / "OldNote.md", ["type: note", "status: active"])
    hub_as_resource = _write_note(vault / "Resources" / "AI" / "AI.md", ["type: resource", "status: active"])
    project_to_note = _write_note(vault / "Projects" / "App" / "Tasks.md", ["type: project", "status: active"])
    good = _write_note(vault / "Resources" / "AI" / "Fine.md", ["type: resource", "status: active"])

    VaultLinter(vault).backfill_types(dry_run=False)

    assert "type: resource" in note_typed.read_text(encoding="utf-8")
    assert "type: resource-hub" in hub_as_resource.read_text(encoding="utf-8")
    assert "type: project-note" in project_to_note.read_text(encoding="utf-8")
    assert "type: resource" in good.read_text(encoding="utf-8")


def test_backfill_types_handles_invalid_frontmatter(tmp_path, capsys):
    vault = tmp_path / "Vault"
    broken = vault / "Resources" / "AI" / "Broken.md"
    broken.parent.mkdir(parents=True)
    broken.write_text("---\nfoo: [unclosed\n---\n\n# Broken\n", encoding="utf-8")

    VaultLinter(vault).backfill_types(dry_run=False)

    assert "Fixed 0 notes missing 'type'." in capsys.readouterr().out


# ── repair_metadata ──────────────────────────────────────────────────────────


def test_repair_metadata_dry_run_reports_only(tmp_path, capsys):
    vault = tmp_path / "Vault"
    note = _write_note(vault / "Resources" / "AI" / "Bare.md", [], body="Source: https://example.com/article\n")
    original = note.read_text(encoding="utf-8")

    VaultLinter(vault).repair_metadata(dry_run=True)

    out = capsys.readouterr().out
    assert "Would repair metadata for: Bare.md" in out
    assert "Found 1 notes with metadata issues." in out
    assert note.read_text(encoding="utf-8") == original


def test_repair_metadata_backfills_type_status_created_and_source(tmp_path):
    vault = tmp_path / "Vault"
    note = _write_note(vault / "Resources" / "AI" / "Bare.md", [], body="Source: https://example.com/article\n")

    VaultLinter(vault).repair_metadata(dry_run=False)

    content = note.read_text(encoding="utf-8")
    assert "type: resource" in content
    assert "status: active" in content
    assert "created:" in content
    assert 'source: "https://example.com/article"' in content


def test_repair_metadata_extracts_bare_url_lines(tmp_path):
    vault = tmp_path / "Vault"
    note = _write_note(
        vault / "Resources" / "AI" / "List.md", ["type: resource"], body="- https://example.com/from-list\n"
    )

    VaultLinter(vault).repair_metadata(dry_run=False)

    assert 'source: "https://example.com/from-list"' in note.read_text(encoding="utf-8")


def test_repair_metadata_promotes_sourceless_resource_to_hub(tmp_path):
    vault = tmp_path / "Vault"
    note = _write_note(vault / "Resources" / "AI" / "Overview.md", ["type: resource"], body="Just prose, no links.\n")

    VaultLinter(vault).repair_metadata(dry_run=False)

    assert "type: resource-hub" in note.read_text(encoding="utf-8")


def test_repair_metadata_restores_social_source_from_related_article(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(vault / "Resources" / "AI" / "Article.md", RESOURCE_FM + ["title: Article"])
    social = _write_note(
        vault / "Social Media Posts" / "AI" / "Article-social.md",
        ["type: social", 'related_article: "[[Article]]"', "status: draft", "created: 2026-03-22 12:00"],
    )

    VaultLinter(vault).repair_metadata(dry_run=False)

    assert 'source: "https://example.com/x"' in social.read_text(encoding="utf-8")


def test_repair_metadata_leaves_complete_notes_untouched(tmp_path, capsys):
    vault = tmp_path / "Vault"
    _write_note(vault / "Resources" / "AI" / "Complete.md", RESOURCE_FM + ["title: Complete"])

    VaultLinter(vault).repair_metadata(dry_run=False)

    assert "Repaired 0 notes with metadata issues." in capsys.readouterr().out


def test_repair_metadata_handles_invalid_frontmatter(tmp_path, capsys):
    vault = tmp_path / "Vault"
    broken = vault / "Resources" / "AI" / "Broken.md"
    broken.parent.mkdir(parents=True)
    broken.write_text("---\nfoo: [unclosed\n---\n\n# Broken\n", encoding="utf-8")

    VaultLinter(vault).repair_metadata(dry_run=False)

    assert "Repaired 0 notes with metadata issues." in capsys.readouterr().out


# ── find_duplicates / get_duplicate_failures ─────────────────────────────────


def _dup_pair(vault: Path, *, linked: bool) -> tuple[Path, Path]:
    resource = _write_note(
        vault / "Resources" / "AI" / "Article.md",
        ["type: resource", "source: https://example.com/dup", "status: active", "created: 2026-03-22 12:00"],
    )
    body = "Check out [[Article]].\n" if linked else "No link here.\n"
    social = _write_note(
        vault / "Social Media Posts" / "AI" / "Article-social.md",
        [
            "type: social",
            "source: https://example.com/dup",
            f'related_article: "{"[[Article]]" if linked else ""}"',
            "status: draft",
            "created: 2026-03-22 12:00",
        ],
        body=body,
    )
    return resource, social


def test_find_duplicates_by_url(tmp_path):
    vault = tmp_path / "Vault"
    a = _write_note(vault / "Resources" / "AI" / "Copy One.md", RESOURCE_FM + ["title: Copy One"])
    b = _write_note(vault / "Resources" / "ML" / "Copy Two.md", RESOURCE_FM + ["title: Copy Two"])

    dupes = VaultLinter(vault).find_duplicates()

    assert len(dupes) == 1
    files = next(iter(dupes.values()))
    assert set(files) == {a, b}


def test_find_duplicates_by_title_with_suffix_normalization(tmp_path):
    vault = tmp_path / "Vault"
    a = _write_note(
        vault / "Resources" / "AI" / "Guide.md",
        [
            "type: resource",
            "source: https://example.com/1",
            "status: active",
            "created: 2026-03-22 12:00",
            "title: Guide",
        ],
    )
    b = _write_note(
        vault / "Resources" / "AI" / "Guide (1).md",
        [
            "type: resource",
            "source: https://example.com/2",
            "status: active",
            "created: 2026-03-22 12:00",
            "title: Guide (1)",
        ],
    )

    dupes = VaultLinter(vault).find_duplicates()

    assert len(dupes) == 1
    key = next(iter(dupes))
    assert key.startswith("title:")
    assert set(dupes[key]) == {a, b}


def test_find_duplicates_spares_genuinely_linked_social(tmp_path):
    vault = tmp_path / "Vault"
    _dup_pair(vault, linked=True)

    assert VaultLinter(vault).find_duplicates() == {}


def test_find_duplicates_flags_unlinked_social(tmp_path):
    vault = tmp_path / "Vault"
    resource, social = _dup_pair(vault, linked=False)

    dupes = VaultLinter(vault).find_duplicates()

    assert len(dupes) == 1
    assert set(next(iter(dupes.values()))) == {resource, social}


def test_find_duplicates_skips_archive_and_unreadable(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(vault / "Archive" / "Old.md", RESOURCE_FM)
    broken = vault / "Resources" / "AI" / "Broken.md"
    broken.parent.mkdir(parents=True)
    broken.write_text("---\nfoo: [unclosed\n---\n", encoding="utf-8")
    # Same-stem-as-parent notes are excluded from title duplication
    _write_note(vault / "Resources" / "AI" / "AI.md", ["type: resource-hub", "status: active", "title: AI"])
    _write_note(
        vault / "Resources" / "ML" / "AI.md", ["type: resource", "status: active", "title: AI"] + RESOURCE_FM[1:]
    )

    assert VaultLinter(vault).find_duplicates() == {}


def test_get_duplicate_failures_lists_each_file(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(vault / "Resources" / "AI" / "Copy One.md", RESOURCE_FM + ["title: Copy One"])
    _write_note(vault / "Resources" / "ML" / "Copy Two.md", RESOURCE_FM + ["title: Copy Two"])

    failures = VaultLinter(vault).get_duplicate_failures()

    assert len(failures) == 2
    assert all(f.code == "TVS201" for f in failures)
    assert all("Duplicate note group" in f.reason for f in failures)


# ── cleanup_social_duplicates ────────────────────────────────────────────────


def test_cleanup_social_duplicates_dry_run(tmp_path, capsys):
    vault = tmp_path / "Vault"
    _resource, social = _dup_pair(vault, linked=False)
    original = social.read_text(encoding="utf-8")

    VaultLinter(vault).cleanup_social_duplicates(dry_run=True)

    assert "Would update social link" in capsys.readouterr().out
    assert social.read_text(encoding="utf-8") == original


def test_cleanup_social_duplicates_applies_related_article(tmp_path, capsys):
    vault = tmp_path / "Vault"
    resource, social = _dup_pair(vault, linked=False)

    VaultLinter(vault).cleanup_social_duplicates(dry_run=False)

    assert "Repaired social linking" in capsys.readouterr().out
    assert f"related_article: '[[{resource.name}]]'" in social.read_text(encoding="utf-8")


# ── cleanup_resource_duplicates ──────────────────────────────────────────────


def _resource_dup_vault(tmp_path) -> tuple[Path, Path, Path]:
    vault = tmp_path / "Vault"
    winner = _write_note(
        vault / "Resources" / "AI" / "Rich.md",
        RESOURCE_FM + ["title: Rich"],
        body="# Rich\n\n" + "Lots of substantial words here. " * 50,
    )
    loser = _write_note(
        vault / "Resources" / "AI" / "Rich (1).md",
        RESOURCE_FM + ["title: Rich (1)"],
        body="# Thin\n",
    )
    return vault, winner, loser


def test_cleanup_resource_duplicates_dry_run(tmp_path, capsys):
    vault, winner, loser = _resource_dup_vault(tmp_path)

    VaultLinter(vault).cleanup_resource_duplicates(dry_run=True)

    out = capsys.readouterr().out
    assert "Winner:" in out
    assert "Would archive duplicate:" in out
    assert loser.exists()


def test_cleanup_resource_duplicates_archives_loser(tmp_path, capsys):
    vault, winner, loser = _resource_dup_vault(tmp_path)

    VaultLinter(vault).cleanup_resource_duplicates(dry_run=False)

    assert winner.exists()
    assert not loser.exists()
    assert (vault / "Archive" / "Duplicates" / loser.name).exists()
    assert "✓ Archived:" in capsys.readouterr().out


def test_cleanup_resource_duplicates_avoids_archive_name_collision(tmp_path):
    vault, _winner, loser = _resource_dup_vault(tmp_path)
    archive = vault / "Archive" / "Duplicates"
    archive.mkdir(parents=True)
    (archive / loser.name).write_text("already archived", encoding="utf-8")

    VaultLinter(vault).cleanup_resource_duplicates(dry_run=False)

    assert not loser.exists()
    archived = list(archive.glob("Rich (1)*.md"))
    assert len(archived) == 2


def test_cleanup_resource_duplicates_ignores_single_resource_groups(tmp_path, capsys):
    vault = tmp_path / "Vault"
    _dup_pair(vault, linked=False)

    VaultLinter(vault).cleanup_resource_duplicates(dry_run=False)

    assert "Consolidating Resources" not in capsys.readouterr().out


# ── get_folder_lint_stats ────────────────────────────────────────────────────


def test_folder_lint_stats_scores_clean_folder(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(vault / "Resources" / "AI" / "Fine.md", RESOURCE_FM + ["title: Fine"])

    stats = VaultLinter(vault).get_folder_lint_stats()

    key = next(k for k in stats if k.endswith("AI"))
    assert stats[key].total_notes == 1
    assert stats[key].score == 100.0


def test_folder_lint_stats_penalizes_errors_and_duplicates(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(vault / "Resources" / "AI" / "Copy One.md", RESOURCE_FM + ["title: Copy One"])
    _write_note(vault / "Resources" / "AI" / "Copy Two.md", RESOURCE_FM + ["title: Copy Two"])
    _write_note(vault / "Resources" / "AI" / "Untyped.md", ["status: active"])

    stats = VaultLinter(vault).get_folder_lint_stats()

    key = next(k for k in stats if k.endswith("AI"))
    assert stats[key].metadata_errors == 1
    assert stats[key].duplicates == 2
    assert stats[key].total_notes == 3
    # Weighted penalty: one error at 5 points plus two duplicates at 10 points, over 3 notes
    assert stats[key].score == pytest.approx(100.0 - 25.0 / 3, abs=0.01)


def test_folder_lint_stats_size_penalty_for_crowded_folders(tmp_path):
    vault = tmp_path / "Vault"
    for i in range(14):
        _write_note(
            vault / "Resources" / "AI" / f"note-{i:02d}.md",
            [
                "type: resource",
                f"source: https://example.com/{i}",
                "status: active",
                "created: 2026-03-22 12:00",
                f"title: Note {i:02d}",
            ],
        )

    stats = VaultLinter(vault).get_folder_lint_stats()

    key = next(k for k in stats if k.endswith("AI"))
    assert stats[key].total_notes == 14
    assert stats[key].score == pytest.approx(99.0, abs=0.01)


def test_folder_lint_stats_root_level_file_keyed_by_name(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(vault / "Resources.md", ["status: active"], body="# Hub\n")

    stats = VaultLinter(vault).get_folder_lint_stats()

    assert "Resources.md" in stats
    assert stats["Resources.md"].total_notes == 1


# ── run_lint / lint_report / run_vault_lint ──────────────────────────────────


def _messy_vault(tmp_path) -> Path:
    vault = tmp_path / "Vault"
    _write_note(vault / "Resources" / "AI" / "Copy One.md", RESOURCE_FM + ["title: Copy One"])
    _write_note(vault / "Resources" / "ML" / "Copy Two.md", RESOURCE_FM + ["title: Copy Two"])
    _write_note(vault / "Resources" / "AI" / "Untyped.md", ["status: active"])
    _write_note(
        vault / "Resources" / "AI" / "Strange.md",
        ["type: mystery", "status: active", "created: 2026-03-22 12:00"],
    )
    _write_note(vault / "Resources" / "AI" / "ugly__name.md", RESOURCE_FM + ["title: Ugly Name"])
    return vault


def test_run_lint_reports_all_issue_classes(tmp_path, capsys):
    vault = _messy_vault(tmp_path)

    ok = VaultLinter(vault).run_lint()

    out = capsys.readouterr().out
    assert ok is False
    assert "metadata errors" in out
    assert "metadata warnings" in out
    assert "filename issues" in out
    assert "duplicate groups" in out
    assert "TVS001" in out
    assert "TVS005" in out


def test_run_lint_clean_vault(tmp_path, capsys):
    vault = tmp_path / "Vault"
    _write_note(vault / "Resources" / "AI" / "Fine.md", RESOURCE_FM + ["title: Fine"])

    ok = VaultLinter(vault).run_lint()

    assert ok is True
    assert "Vault is clean" in capsys.readouterr().out


def test_lint_report_structure(tmp_path):
    vault = _messy_vault(tmp_path)

    report = VaultLinter(vault).lint_report()

    assert report.ok is False
    assert report.vault_path == str(vault)
    errors = report.metadata.errors
    warnings = report.metadata.warnings
    assert any(e.code == "TVS001" for e in errors)
    assert any(w.code == "TVS005" for w in warnings)
    filenames = report.filenames
    assert len(filenames) == 1
    assert filenames[0].path == "Resources/AI/ugly__name.md"
    assert "TVS103" in filenames[0].codes
    assert filenames[0].suggested_path == "Resources/AI/ugly-name.md"
    assert len(report.duplicates) == 1
    for files in report.duplicates.values():
        assert all(not f.startswith(str(vault)) for f in files)


def test_lint_report_clean_vault_is_ok(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(vault / "Resources" / "AI" / "Fine.md", RESOURCE_FM + ["title: Fine"])

    report = VaultLinter(vault).lint_report()

    assert report.ok is True
    assert report.metadata == MetadataReport(errors=(), warnings=())
    assert report.filenames == ()
    assert report.duplicates == {}


def test_run_vault_lint_json_output(tmp_path, capsys):
    vault = tmp_path / "Vault"
    _write_note(vault / "Resources" / "AI" / "Fine.md", RESOURCE_FM + ["title: Fine"])

    ok = run_vault_lint(vault, json_output=True)

    out = capsys.readouterr().out
    assert ok is True
    assert '"ok"' in out


def test_run_vault_lint_plain_output(tmp_path, capsys):
    vault = _messy_vault(tmp_path)

    ok = run_vault_lint(vault)

    assert ok is False
    assert "metadata errors" in capsys.readouterr().out


# ── remaining edge branches ──────────────────────────────────────────────────


def test_windows_portability_reason_flags_trailing_dot_part():
    assert windows_portability_issue_reason(PurePosixPath("Resources/Folder./Note.md")) == (
        "path is not portable to Windows"
    )


def test_metadata_failures_skip_archive_notes(tmp_path):
    vault = tmp_path / "Vault"
    # Contains "Resources" in the relative path but lives under Archive
    _write_note(vault / "Archive" / "Resources" / "Old.md", ["status: active"])
    _write_note(vault / "Journal" / "Daily.md", ["status: active"])

    assert VaultLinter(vault).find_metadata_failures() == []


def test_backfill_types_skips_archive(tmp_path, capsys):
    vault = tmp_path / "Vault"
    archived = _write_note(vault / "Archive" / "Resources" / "Old.md", ["status: active"])

    VaultLinter(vault).backfill_types(dry_run=False)

    assert "type:" not in archived.read_text(encoding="utf-8")
    assert "Fixed 0 notes" in capsys.readouterr().out


def test_repair_metadata_skips_non_para_notes(tmp_path, capsys):
    vault = tmp_path / "Vault"
    journal = _write_note(vault / "Journal" / "Daily.md", ["status: active"])
    original = journal.read_text(encoding="utf-8")

    VaultLinter(vault).repair_metadata(dry_run=False)

    assert journal.read_text(encoding="utf-8") == original
    assert "Repaired 0 notes" in capsys.readouterr().out


def test_repair_metadata_replaces_whitespace_only_status(tmp_path):
    vault = tmp_path / "Vault"
    note = _write_note(
        vault / "Resources" / "AI" / "Blank.md",
        ["type: resource", 'status: "  "', "source: https://example.com/x", "created: 2026-03-22 12:00"],
    )

    VaultLinter(vault).repair_metadata(dry_run=False)

    assert "status: active" in note.read_text(encoding="utf-8")


def test_find_duplicates_keeps_social_when_link_check_fails(tmp_path, monkeypatch):
    """A social note whose frontmatter cannot be re-read is kept as a duplicate."""
    vault = tmp_path / "Vault"
    resource, social = _dup_pair(vault, linked=True)

    def boom(_path):
        raise OSError("cannot re-read")

    monkeypatch.setattr("rig.obsidian.lint_duplicates._load_frontmatter", boom)

    dupes = VaultLinter(vault).find_duplicates()

    assert len(dupes) == 1
    assert set(next(iter(dupes.values()))) == {resource, social}


def test_cleanup_resource_duplicates_all_files_missing(tmp_path, monkeypatch, capsys):
    vault = tmp_path / "Vault"
    vault.mkdir()
    linter = VaultLinter(vault)
    ghosts = [vault / "Resources" / "AI" / "Ghost One.md", vault / "Resources" / "AI" / "Ghost Two.md"]
    monkeypatch.setattr(linter, "find_duplicates", lambda: {"https://example.com/ghost": ghosts})

    linter.cleanup_resource_duplicates(dry_run=False)

    out = capsys.readouterr().out
    assert "Consolidating Resources" in out
    assert "Winner:" not in out


def test_folder_lint_stats_counts_warnings_and_skips_archive(tmp_path):
    vault = tmp_path / "Vault"
    _write_note(
        vault / "Resources" / "AI" / "Strange.md",
        ["type: mystery", "status: active", "created: 2026-03-22 12:00"],
    )
    _write_note(vault / "Archive" / "Resources" / "Old.md", ["status: active"])

    stats = VaultLinter(vault).get_folder_lint_stats()

    key = next(k for k in stats if k.endswith("AI"))
    assert stats[key].metadata_warnings == 1
    assert stats[key].metadata_errors == 0
    assert not any("Archive" in k for k in stats)


def test_every_type_used_in_the_vault_is_known_to_the_linter():
    """A type missing from TYPE_REQUIREMENTS is reported as unknown.

    `daily` alone covers 493 notes; omitting it made TVS005 fire on more valid
    notes than invalid ones, which trains the reader to ignore the rule.
    """
    from rig.obsidian.lint_manager import VaultLinter

    observed_in_vault = {
        "resource",
        "resource-hub",
        "social",
        "project",
        "project-note",
        "design-note",
        "area",
        "daily",
        "note",
        "task",
        "spec",
        "Repository",
        "Reference",
    }

    assert observed_in_vault <= set(VaultLinter.TYPE_REQUIREMENTS)


def test_types_without_a_source_do_not_require_one():
    """`note`, `daily` and the OKF types are written or generated, not captured."""
    from rig.obsidian.lint_manager import VaultLinter

    for note_type in ("note", "daily", "Repository", "Reference"):
        assert "source" not in VaultLinter.TYPE_REQUIREMENTS[note_type]

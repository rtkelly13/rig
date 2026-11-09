"""Tests for common .gitignore governance manager and GitignoreEntry."""

import pathlib

import pytest

from rig.repo_governance.agent_files import (
    BESPOKE_AGENT_FILES,
    GITIGNORE_ENTRY_NAME,
)
from rig.repo_governance.gitignore_manager import (
    align_gitignore,
    audit_estate_gitignores,
    inspect_gitignore,
    required_entries,
    sync_estate_gitignores,
)
from rig.repo_governance.policy_config import (
    GitignoreEntry,
    PolicyConfig,
)


@pytest.mark.ci
def test_gitignore_entry_any_mode_satisfied_by_synonym():
    entry = GitignoreEntry(
        name="temp",
        comment="Temp directories",
        write=("/temp/", "temp/"),
        satisfied_by=("/temp", "temp/"),
        match_mode="any",
    )
    assert entry.is_present("temp/\n")
    assert entry.is_present("/temp/\n")
    assert entry.missing_lines("temp/\n") == []
    assert not entry.is_present("build/\n")
    assert entry.missing_lines("build/\n") == ["/temp/", "temp/"]


@pytest.mark.ci
def test_gitignore_entry_all_mode_requires_every_pattern():
    entry = GitignoreEntry(
        name="todo",
        comment="Local TODO files",
        write=("TODO.md", "todo.md"),
        satisfied_by=("TODO.md", "todo.md"),
        match_mode="all",
    )
    # Both present
    assert entry.is_present("TODO.md\ntodo.md\n")
    assert entry.missing_lines("TODO.md\ntodo.md\n") == []

    # Only uppercase present
    assert not entry.is_present("TODO.md\n")
    assert entry.missing_lines("TODO.md\n") == ["todo.md"]
    rendered = entry.render_missing(["todo.md"])
    assert "todo.md" in rendered
    assert "TODO.md" not in rendered

    # Only lowercase present
    assert not entry.is_present("todo.md\n")
    assert entry.missing_lines("todo.md\n") == ["TODO.md"]

    # Neither present
    assert not entry.is_present("node_modules/\n")
    assert entry.missing_lines("node_modules/\n") == ["TODO.md", "todo.md"]


@pytest.mark.ci
def test_gitignore_wildcard_does_not_falsely_satisfy_exact_name():
    entry = GitignoreEntry(
        name="todo",
        comment="Local TODO files",
        write=("TODO.md", "todo.md"),
        satisfied_by=("TODO.md", "todo.md"),
        match_mode="all",
    )
    # *.todo.md should NOT satisfy todo.md
    assert not entry.is_present("*.todo.md\n")
    assert entry.missing_lines("*.todo.md\n") == ["TODO.md", "todo.md"]


@pytest.mark.ci
def test_inspect_and_align_gitignore_lifecycle(tmp_path: pathlib.Path):
    policy = PolicyConfig.parse(
        {
            "defaults": {
                "agents_md_hygiene": {"bespoke_agent_files": []},
                "gitignore_entries": [
                    {
                        "name": "temp",
                        "comment": "Temp directories",
                        "write": ["/temp/", "temp/"],
                        "satisfied_by": ["/temp", "temp/"],
                        "match_mode": "any",
                    },
                    {
                        "name": "todo",
                        "comment": "Local TODO files",
                        "write": ["TODO.md", "todo.md"],
                        "satisfied_by": ["TODO.md", "todo.md"],
                        "match_mode": "all",
                    },
                ],
            }
        }
    )

    repo_dir = tmp_path / "sample-repo"
    repo_dir.mkdir()

    # 1. Missing .gitignore file entirely
    res = inspect_gitignore(repo_dir, config=policy)
    assert res.status == "MISSING"
    assert not res.is_aligned
    assert "temp" in res.missing
    assert "todo" in res.missing

    # 2. Align dry-run does not write file
    align_dry = align_gitignore(repo_dir, dry_run=True, config=policy)
    assert align_dry.status == "aligned"
    assert not (repo_dir / ".gitignore").exists()

    # 3. Align applies changes
    align_real = align_gitignore(repo_dir, dry_run=False, config=policy)
    assert align_real.status == "aligned"
    assert (repo_dir / ".gitignore").exists()
    content = (repo_dir / ".gitignore").read_text(encoding="utf-8")
    assert "temp/" in content
    assert "TODO.md" in content
    assert "todo.md" in content

    # 4. Re-inspect reports fully configured
    res_after = inspect_gitignore(repo_dir, config=policy)
    assert res_after.status == "CONFIGURED"
    assert res_after.is_aligned
    assert res_after.missing == ()

    # 5. Re-align is idempotent (no duplicate lines)
    align_idempotent = align_gitignore(repo_dir, dry_run=False, config=policy)
    assert align_idempotent.status == "already_configured"
    assert align_idempotent.added == ()
    content_idempotent = (repo_dir / ".gitignore").read_text(encoding="utf-8")
    assert content_idempotent == content


@pytest.mark.ci
def test_align_partial_missing_lines_no_duplication(tmp_path: pathlib.Path):
    policy = PolicyConfig.parse(
        {
            "defaults": {
                "agents_md_hygiene": {"bespoke_agent_files": []},
                "gitignore_entries": [
                    {
                        "name": "todo",
                        "comment": "Local TODO files",
                        "write": ["TODO.md", "todo.md"],
                        "satisfied_by": ["TODO.md", "todo.md"],
                        "match_mode": "all",
                    },
                ],
            }
        }
    )

    repo_dir = tmp_path / "sample-repo"
    repo_dir.mkdir()
    gi_file = repo_dir / ".gitignore"
    gi_file.write_text("# Existing ignore\nTODO.md\n", encoding="utf-8")

    # Inspect detects only todo.md is missing
    res = inspect_gitignore(repo_dir, config=policy)
    assert not res.is_aligned
    assert res.missing_lines == ("todo.md",)

    # Align adds ONLY todo.md, never duplicating TODO.md
    align_res = align_gitignore(repo_dir, dry_run=False, config=policy)
    assert align_res.status == "aligned"
    assert align_res.added == ("todo.md",)

    lines = gi_file.read_text(encoding="utf-8").splitlines()
    assert lines.count("TODO.md") == 1
    assert lines.count("todo.md") == 1


@pytest.mark.ci
def test_estate_batch_audit_and_sync(tmp_path: pathlib.Path):
    policy = PolicyConfig.parse(
        {
            "defaults": {
                "agents_md_hygiene": {"bespoke_agent_files": []},
                "gitignore_entries": [
                    {
                        "name": "todo",
                        "comment": "Local TODO files",
                        "write": ["TODO.md", "todo.md"],
                        "satisfied_by": ["TODO.md", "todo.md"],
                        "match_mode": "all",
                    }
                ],
            }
        }
    )

    repo_a = tmp_path / "repo-a"
    repo_b = tmp_path / "repo-b"
    repo_a.mkdir()
    repo_b.mkdir()
    (repo_a / ".git").mkdir()
    (repo_b / ".git").mkdir()

    (repo_a / ".gitignore").write_text("TODO.md\ntodo.md\n", encoding="utf-8")
    (repo_b / ".gitignore").write_text("TODO.md\n", encoding="utf-8")

    audit_res = audit_estate_gitignores(tmp_path, config=policy)
    assert len(audit_res) == 2
    res_a = next(r for r in audit_res if r.repo_name == "repo-a")
    res_b = next(r for r in audit_res if r.repo_name == "repo-b")
    assert res_a.result.is_aligned is True
    assert res_b.result.is_aligned is False
    assert res_b.result.missing_lines == ("todo.md",)

    sync_res = sync_estate_gitignores(tmp_path, config=policy)
    assert len(sync_res) == 2
    synced_b = next(r for r in sync_res if r.repo_name == "repo-b")
    assert synced_b.result.status == "aligned"
    assert synced_b.result.added == ("todo.md",)

    # Re-audit verifies all aligned
    audit_after = audit_estate_gitignores(tmp_path, config=policy)
    assert all(r.result.is_aligned for r in audit_after)


@pytest.mark.ci
def test_gitignore_token_satisfaction_permutations():
    from rig.repo_governance.policy_config import _is_gitignore_token_satisfied

    # Trailing/leading slash permutations
    assert _is_gitignore_token_satisfied("bin/", {"bin"})
    assert _is_gitignore_token_satisfied("bin/", {"/bin/"})
    assert _is_gitignore_token_satisfied("bin/", {"**/bin"})
    assert _is_gitignore_token_satisfied("bin/", {"[Bb]in/"})
    assert _is_gitignore_token_satisfied("obj/", {"[Oo]bj/"})
    assert _is_gitignore_token_satisfied("node_modules/", {"node_modules"})
    assert _is_gitignore_token_satisfied("node_modules/", {"/node_modules"})
    assert _is_gitignore_token_satisfied(".DS_Store", {".DS_Store*"})
    assert _is_gitignore_token_satisfied(".DS_Store", {"**/.DS_Store"})
    assert _is_gitignore_token_satisfied(".venv/", {"venv/"})
    assert _is_gitignore_token_satisfied("temp/", {".temp/"})
    assert _is_gitignore_token_satisfied("temp/", {"/temp/"})


@pytest.mark.ci
def test_language_detection_and_preset_alignment(tmp_path: pathlib.Path):
    repo = tmp_path / "dotnet-repo"
    repo.mkdir()
    (repo / "MyApp.slnx").write_text("", encoding="utf-8")

    # Inspect without .gitignore detects dotnet and flags missing rules
    inspection = inspect_gitignore(repo)
    assert "dotnet" in inspection.languages
    assert not inspection.is_aligned
    assert "bin/" in inspection.missing_lines
    assert "*.binlog" in inspection.missing_lines
    assert "TODO.md" in inspection.missing_lines
    assert "temp/" in inspection.missing_lines

    # Align creates .gitignore with standard tools and temp folder
    align_res = align_gitignore(repo)
    assert align_res.status == "aligned"
    assert "dotnet" in align_res.languages
    content = (repo / ".gitignore").read_text(encoding="utf-8")
    assert "bin/" in content
    assert "*.binlog" in content
    assert "temp/" in content
    assert "TODO.md" in content

    # Subsequent inspection passes
    inspection_after = inspect_gitignore(repo)
    assert inspection_after.status == "CONFIGURED"
    assert inspection_after.is_aligned


@pytest.mark.ci
def test_split_monorepo_multi_language_detection(tmp_path: pathlib.Path):
    repo = tmp_path / "fullstack-app"
    repo.mkdir()
    backend = repo / "backend"
    backend.mkdir()
    (backend / "pyproject.toml").write_text("[project]\nname = 'api'\n", encoding="utf-8")
    frontend = repo / "frontend"
    frontend.mkdir()
    (frontend / "package.json").write_text("{}", encoding="utf-8")

    inspection = inspect_gitignore(repo)
    assert "python" in inspection.languages
    assert "node" in inspection.languages
    assert not inspection.is_aligned
    # Python tools
    assert "__pycache__/" in inspection.missing_lines
    # Node tools
    assert "node_modules/" in inspection.missing_lines


@pytest.mark.ci
def test_explicit_language_override(tmp_path: pathlib.Path):
    repo = tmp_path / "custom-repo"
    repo.mkdir()

    # Pass explicit language 'python' to a blank repo
    inspection = inspect_gitignore(repo, languages=["python"])
    assert inspection.languages == ("python",)
    assert "__pycache__/" in inspection.missing_lines

    # Pass alias 'csharp'
    inspection_cs = inspect_gitignore(repo, languages=["csharp"])
    assert inspection_cs.languages == ("dotnet",)
    assert "bin/" in inspection_cs.missing_lines


@pytest.mark.ci
def test_bespoke_agent_entry_covers_every_configured_path(tmp_path: pathlib.Path):
    """The ignore rule and the audit read one list, so they cannot drift."""
    policy = PolicyConfig.parse({"defaults": {"gitignore_entries": []}})

    entries = required_entries(tmp_path, config=policy)

    bespoke = next(entry for entry in entries if entry.name == GITIGNORE_ENTRY_NAME)
    assert bespoke.write == BESPOKE_AGENT_FILES
    assert bespoke.match_mode == "all"


@pytest.mark.ci
def test_bespoke_agent_entry_needs_every_line_present(tmp_path: pathlib.Path):
    """One stray `.cursorrules` must not satisfy the whole block."""
    policy = PolicyConfig.parse({"defaults": {"gitignore_entries": []}})
    (tmp_path / ".gitignore").write_text(".cursorrules\n", encoding="utf-8")

    inspection = inspect_gitignore(tmp_path, config=policy)

    assert not inspection.is_aligned
    assert ".github/copilot-instructions.md" in inspection.missing_lines


@pytest.mark.ci
def test_bespoke_agent_entry_is_disabled_by_an_empty_list(tmp_path: pathlib.Path):
    policy = PolicyConfig.parse(
        {"defaults": {"gitignore_entries": [], "agents_md_hygiene": {"bespoke_agent_files": []}}}
    )

    assert required_entries(tmp_path, config=policy) == ()


@pytest.mark.ci
def test_aligning_writes_the_bespoke_agent_block(tmp_path: pathlib.Path):
    policy = PolicyConfig.parse({"defaults": {"gitignore_entries": []}})

    alignment = align_gitignore(tmp_path, config=policy)

    assert alignment.status == "aligned"
    assert ".github/copilot-instructions.md" in alignment.added
    content = (tmp_path / ".gitignore").read_text(encoding="utf-8")
    assert "AGENTS.md is the single source of truth" in content

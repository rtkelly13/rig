"""Tests written from mutmut's surviving mutants in gitignore_manager.

Each test names the mutants it was written to kill (`mutmut show <name>`, names relative to
`rig.repo_governance.gitignore_manager`). The existing tests ran nearly every line but checked
only `status` and a few membership tests, so mutants that blanked a field or dropped an argument
survived. These pin each result exactly instead. See docs/testing.md#mutation-testing.

Survivors judged equivalent, so left alone:
- `repo_path=None` or omitted in `gitignore_entries` (inspect 12, 15; align 13, 16): the languages
  are already resolved and passed in, so the path is never consulted again.
- `encoding="UTF-8"`, `encoding=None` or omitted (inspect 61, 63; align 38, 40, 72, 74, 76):
  "UTF-8" is an alias, and the CI runners' locale encoding is UTF-8.
- `appended.lstrip(None)` and `lstrip("XX\\nXX")` (align 67, 69): every rendered block starts
  with "\\n#", so both strip exactly the same characters.
"""

import pathlib

import pytest

from rig.repo_governance.gitignore_manager import (
    GitignoreAlignment,
    GitignoreInspection,
    RepoGitignoreAlignment,
    RepoGitignoreInspection,
    align_gitignore,
    audit_estate_gitignores,
    inspect_gitignore,
    sync_estate_gitignores,
)
from rig.repo_governance.policy_config import PolicyConfig

pytestmark = pytest.mark.ci

TEMP = {
    "name": "temp",
    "comment": "Temp directories",
    "write": ["/temp/", "temp/"],
    "satisfied_by": ["/temp", "temp/"],
    "match_mode": "any",
}
TODO = {
    "name": "todo",
    "comment": "Local TODO files",
    "write": ["TODO.md", "todo.md"],
    "satisfied_by": ["TODO.md", "todo.md"],
    "match_mode": "all",
}
PYTHON = {
    "name": "Python",
    "aliases": ["py"],
    "detect_files": ["pyproject.toml"],
    "gitignore_entries": [{"name": "pycache", "comment": "Python bytecode", "write": ["__pycache__/"]}],
}

#: The bespoke agent-file block is required by default; these tests pin the entry mechanics, so
#: they turn it off. tests/test_gitignore_manager.py covers the block itself.
NO_AGENT_FILES = {"agents_md_hygiene": {"bespoke_agent_files": []}}

BASE = PolicyConfig.parse({"defaults": {"gitignore_entries": [TEMP, TODO], **NO_AGENT_FILES}})
PRESET_ONLY = PolicyConfig.parse({"defaults": NO_AGENT_FILES, "language_presets": {"python": PYTHON}})
EMPTY = PolicyConfig.parse({"defaults": NO_AGENT_FILES})


def _repo(path: pathlib.Path, gitignore: str | None = None, *, git: bool = True) -> pathlib.Path:
    path.mkdir(parents=True)
    if git:
        (path / ".git").mkdir()
    if gitignore is not None:
        (path / ".gitignore").write_text(gitignore, encoding="utf-8")
    return path


# -- inspect_gitignore: every field of every outcome --------------------------------------------


def test_inspect_without_policy_is_aligned_and_names_the_file(tmp_path: pathlib.Path):
    """Kills inspect 22-38: any field of the NO POLICY result blanked, dropped or altered."""
    repo = _repo(tmp_path / "r")
    assert inspect_gitignore(repo, config=EMPTY) == GitignoreInspection(
        status="NO POLICY",
        is_aligned=True,
        languages=(),
        missing=(),
        missing_lines=(),
        file=str(repo / ".gitignore"),
    )


def test_inspect_missing_file_lists_entries_lines_and_languages(tmp_path: pathlib.Path):
    """Kills inspect 42, 46: is_aligned or file blanked when there is no .gitignore."""
    repo = _repo(tmp_path / "r")
    (repo / "pyproject.toml").write_text("", encoding="utf-8")
    assert inspect_gitignore(repo, config=PRESET_ONLY) == GitignoreInspection(
        status="MISSING",
        is_aligned=False,
        languages=("python",),
        missing=("pycache",),
        missing_lines=("__pycache__/",),
        file=str(repo / ".gitignore"),
    )


def test_inspect_configured_result_is_exact(tmp_path: pathlib.Path):
    """Kills inspect 73, 75, 76, 87: languages, missing_lines or file blanked when configured."""
    repo = _repo(tmp_path / "r", "temp/\nTODO.md\ntodo.md\n")
    assert inspect_gitignore(repo, config=BASE) == GitignoreInspection(
        status="CONFIGURED",
        is_aligned=True,
        languages=(),
        missing=(),
        missing_lines=(),
        file=str(repo / ".gitignore"),
    )


def test_inspect_partial_status_lists_the_missing_lines_sorted(tmp_path: pathlib.Path):
    """Kills inspect 88-108: the `MISSING a, b` status text and the fields beside it."""
    repo = _repo(tmp_path / "r", "temp/\n")
    assert inspect_gitignore(repo, config=BASE) == GitignoreInspection(
        status="MISSING TODO.md, todo.md",
        is_aligned=False,
        languages=(),
        missing=("todo",),
        missing_lines=("TODO.md", "todo.md"),
        file=str(repo / ".gitignore"),
    )


# -- align_gitignore -----------------------------------------------------------------------------


def test_align_without_policy_reports_no_policy_exactly(tmp_path: pathlib.Path):
    """Kills align 23-34: any field of the no_policy result blanked, dropped or altered."""
    repo = _repo(tmp_path / "r")
    assert align_gitignore(repo, config=EMPTY) == GitignoreAlignment(
        status="no_policy", file=str(repo / ".gitignore"), languages=(), added=()
    )
    assert not (repo / ".gitignore").exists()


def test_align_already_configured_reports_file_and_languages(tmp_path: pathlib.Path):
    """Kills align 48, 49, 57: file or languages blanked when nothing needs adding."""
    repo = _repo(tmp_path / "r", "__pycache__/\n")
    (repo / "pyproject.toml").write_text("", encoding="utf-8")
    assert align_gitignore(repo, config=PRESET_ONLY) == GitignoreAlignment(
        status="already_configured", file=str(repo / ".gitignore"), languages=("python",), added=()
    )


def test_align_creates_a_new_file_with_exactly_the_rendered_blocks(tmp_path: pathlib.Path):
    """Kills align 41, 61, 65, 68, 79, 88: the new file's exact bytes and the result's file.

    A file created from nothing starts at its first comment, with one blank line between blocks.
    """
    repo = _repo(tmp_path / "r")
    result = align_gitignore(repo, config=BASE)
    assert (repo / ".gitignore").read_text(encoding="utf-8") == (
        "# Temp directories\n/temp/\ntemp/\n\n# Local TODO files\nTODO.md\ntodo.md\n"
    )
    assert result == GitignoreAlignment(
        status="aligned",
        file=str(repo / ".gitignore"),
        languages=(),
        added=("/temp/", "temp/", "TODO.md", "todo.md"),
    )


def test_align_appends_after_existing_content_unchanged(tmp_path: pathlib.Path):
    """Pins the append path beside the create path: existing bytes kept, one block added."""
    repo = _repo(tmp_path / "r", "# mine\nbuild/\ntemp/\nTODO.md\n")
    align_gitignore(repo, config=BASE)
    assert (repo / ".gitignore").read_text(encoding="utf-8") == (
        "# mine\nbuild/\ntemp/\nTODO.md\n\n# Local TODO files\ntodo.md\n"
    )


def test_align_uses_the_languages_it_is_given(tmp_path: pathlib.Path):
    """Kills align 7, 10, 14, 17: explicit languages dropped before resolving or selecting entries."""
    repo = _repo(tmp_path / "r")  # nothing to detect, so only the explicit language selects entries
    assert align_gitignore(repo, config=PRESET_ONLY, languages=["py"]) == GitignoreAlignment(
        status="aligned", file=str(repo / ".gitignore"), languages=("python",), added=("__pycache__/",)
    )


# -- JSON rows -----------------------------------------------------------------------------------


def test_inspection_json_row_is_flat_and_complete():
    """Kills RepoGitignoreInspection.to_json 1-5 and _json_fields 1-5."""
    row = RepoGitignoreInspection(
        repo_name="blog",
        repo_path="/estate/blog",
        result=GitignoreInspection(
            status="MISSING todo.md",
            is_aligned=False,
            languages=("python",),
            missing=("todo",),
            missing_lines=("todo.md",),
            file="/estate/blog/.gitignore",
        ),
    )
    assert row.to_json() == {
        "repo_name": "blog",
        "repo_path": "/estate/blog",
        "status": "MISSING todo.md",
        "is_aligned": False,
        "languages": ["python"],
        "missing": ["todo"],
        "missing_lines": ["todo.md"],
        "file": "/estate/blog/.gitignore",
    }


def test_alignment_json_row_is_flat_and_complete():
    """Kills RepoGitignoreAlignment.to_json 1-5."""
    row = RepoGitignoreAlignment(
        repo_name="blog",
        repo_path="/estate/blog",
        result=GitignoreAlignment(status="aligned", file="/estate/blog/.gitignore", languages=(), added=("todo.md",)),
    )
    assert row.to_json() == {
        "repo_name": "blog",
        "repo_path": "/estate/blog",
        "status": "aligned",
        "file": "/estate/blog/.gitignore",
        "languages": [],
        "added": ["todo.md"],
    }


# -- estate-wide audit and sync ------------------------------------------------------------------


@pytest.fixture
def estate(tmp_path: pathlib.Path) -> pathlib.Path:
    """A personal folder with one clone, a plain directory, and a clone in an org folder."""
    personal = tmp_path / "personal"
    _repo(personal / "blog", "")
    _repo(personal / "notes", git=False)
    _repo(tmp_path / "rtkelly-labs" / "Diffra", "")
    return personal


def test_audit_finds_only_git_clones_across_org_folders(estate: pathlib.Path):
    """Kills _discover_repos 7 (`and` -> `or`) and audit 16, 21 (repo_path blanked)."""
    rows = audit_estate_gitignores(estate, config=EMPTY)
    assert [(r.repo_name, r.repo_path) for r in rows] == [
        ("blog", str(estate / "blog")),
        ("Diffra", str(estate.parent / "rtkelly-labs" / "Diffra")),
    ]


def test_audit_named_repos_and_languages_are_passed_through(estate: pathlib.Path):
    """Kills _discover_repos 3, 4 and audit 3, 5, 10, 13: the repo filter or languages dropped."""
    rows = audit_estate_gitignores(estate, repos=["Diffra", "notes", "absent"], config=PRESET_ONLY, languages=["py"])
    assert [(r.repo_name, r.result.languages, r.result.missing_lines) for r in rows] == [
        ("Diffra", ("python",), ("__pycache__/",))
    ]


def test_sync_named_repos_dry_run_and_languages_are_passed_through(estate: pathlib.Path):
    """Kills sync 4, 6, 10, 12, 14, 16, 19, 24: filter, dry_run, languages or repo_path dropped."""
    rows = sync_estate_gitignores(estate, repos=["blog"], dry_run=True, config=PRESET_ONLY, languages=["py"])
    assert [(r.repo_name, r.repo_path, r.result.status, r.result.added) for r in rows] == [
        ("blog", str(estate / "blog"), "aligned", ("__pycache__/",))
    ]
    assert (estate / "blog" / ".gitignore").read_text(encoding="utf-8") == ""

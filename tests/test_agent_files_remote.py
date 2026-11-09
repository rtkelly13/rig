"""Tests for the GitHub-side agent-file sweep."""

from __future__ import annotations

import json
from collections.abc import Mapping

import pytest

from rig.proc import Ran
from rig.repo_governance.agent_files_remote import (
    GhCli,
    RemoteSweep,
    find_redundant_files,
    list_owner_repos,
    sweep_remote_agent_files,
)

pytestmark = pytest.mark.ci

_LIST = "repo list"
_TREE = "api repos/"


def _gh(answers: Mapping[str, object]) -> GhCli:
    """A `gh` that answers each call from *answers*, keyed by the call's arguments; anything else fails."""

    def run(args: list[str]) -> Ran:
        key = " ".join(args)
        for prefix, answer in answers.items():
            if key.startswith(prefix):
                return Ran(0, json.dumps(answer), "")
        return Ran(1, "", f"gh: no answer for {key}")

    return run


def _repo_row(name: str, branch: str | None, *, archived: bool = False, fork: bool = False) -> dict[str, object]:
    return {
        "nameWithOwner": name,
        "isArchived": archived,
        "isFork": fork,
        "defaultBranchRef": None if branch is None else {"name": branch},
    }


def test_legacy_and_bespoke_names_are_both_reported() -> None:
    paths = ["README.md", "CLAUDE.md", ".github/copilot-instructions.md"]
    assert sorted(find_redundant_files(paths)) == [".github/copilot-instructions.md", "CLAUDE.md"]


def test_a_directory_entry_reports_once_however_many_files_it_holds() -> None:
    paths = [".github/instructions/a.instructions.md", ".github/instructions/b.instructions.md"]
    assert find_redundant_files(paths) == [".github/instructions"]


def test_tool_feature_definitions_are_not_redundant() -> None:
    """Custom agents and prompt files extend a tool; they do not shadow AGENTS.md."""
    paths = [".github/agents/workflows.agent.md", ".github/prompts/review.prompt.md", ".claude/skills/x/SKILL.md"]
    assert find_redundant_files(paths) == []


def test_clean_tree_reports_nothing() -> None:
    assert find_redundant_files(["AGENTS.md", "README.md", ".github/workflows/ci.yml"]) == []


def test_list_owner_repos_decodes_gh_output() -> None:
    run = _gh(
        {
            f"{_LIST} rtkelly13": [
                _repo_row("rtkelly13/docs", "main"),
                _repo_row("rtkelly13/notes", "main", archived=True),
            ]
        }
    )

    repos = list_owner_repos("rtkelly13", run)

    assert [repo.name_with_owner for repo in repos] == ["rtkelly13/docs", "rtkelly13/notes"]
    assert [repo.is_archived for repo in repos] == [False, True]


def test_a_failed_gh_call_is_not_an_empty_list() -> None:
    assert list_owner_repos("rtkelly13", _gh({})) is None


def test_an_unlistable_owner_or_unreadable_tree_is_reported_as_unread() -> None:
    """A failed read must not pass as a clean repository."""
    run = _gh(
        {
            f"{_LIST} rtkelly13": [_repo_row("rtkelly13/docs", "main"), _repo_row("rtkelly13/locked", "main")],
            f"{_TREE}rtkelly13/docs/": {"tree": [{"path": "AGENTS.md"}]},
        }
    )

    sweep = sweep_remote_agent_files(["rtkelly13", "someone-else"], run=run)

    assert sweep == RemoteSweep(findings=(), unread=("rtkelly13/locked", "someone-else"))


def test_sweep_reports_archived_and_skips_repos_without_a_branch() -> None:
    run = _gh(
        {
            f"{_LIST} rtkelly13": [
                _repo_row("rtkelly13/docs", "main"),
                _repo_row("rtkelly13/the-vault-system", "main", archived=True),
                _repo_row("rtkelly13/empty", None),
            ],
            f"{_TREE}rtkelly13/docs/": {"tree": [{"path": "AGENTS.md"}, {"path": "CLAUDE.md"}]},
            f"{_TREE}rtkelly13/the-vault-system/": {"tree": [{"path": "AGENTS.md"}, {"path": "CLAUDE.md"}]},
        }
    )

    sweep = sweep_remote_agent_files(["rtkelly13"], run=run)

    assert sweep.unread == ()
    assert [(f.repo, f.path, f.archived) for f in sweep.findings] == [
        ("rtkelly13/docs", "CLAUDE.md", False),
        ("rtkelly13/the-vault-system", "CLAUDE.md", True),
    ]


def test_forks_are_skipped_unless_a_profile_registers_them() -> None:
    """Upstream's instruction files are not ours to remove."""
    run = _gh(
        {
            f"{_LIST} rtkelly13": [
                _repo_row("rtkelly13/nodatime", "master", fork=True),
                _repo_row("rtkelly13/Resultful", "main", fork=True),
            ],
            _TREE: {"tree": [{"path": "CLAUDE.md"}]},
        }
    )

    assert sweep_remote_agent_files(["rtkelly13"], run=run) == RemoteSweep()

    registered = sweep_remote_agent_files(
        ["rtkelly13"], is_registered=lambda name: name.endswith("/Resultful"), run=run
    )
    assert [finding.repo for finding in registered.findings] == ["rtkelly13/Resultful"]

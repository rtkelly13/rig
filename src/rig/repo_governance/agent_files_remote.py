"""GitHub-side sweep for agent files a local checkout cannot see.

`agents-audit --all` walks the estate directory, so a repository with no clone is
invisible to it. That blind spot is how an archived repo kept a `CLAUDE.md`
through an earlier sweep. This module reads each repository's default-branch tree
over the API instead, so the sweep covers every repository the account owns,
cloned or not.
"""

from __future__ import annotations

from collections.abc import Callable, Iterable, Sequence
from dataclasses import dataclass
from typing import Annotated

from pydantic import BaseModel, Field

from rig import proc
from rig.repo_governance.agent_files import BESPOKE_AGENT_FILES
from rig.typed_json import try_decode_json

#: Removed by the original sweep, before this check existed. Kept here because the
#: remote half has no `AgentsMdRules` per repo to read the list from.
LEGACY_INSTRUCTION_FILES: tuple[str, ...] = ("CLAUDE.md",)


class _Branch(BaseModel):
    name: str = ""


class OwnerRepo(BaseModel):
    """One repository as `gh repo list` reports it."""

    name_with_owner: Annotated[str, Field(alias="nameWithOwner")]
    is_archived: Annotated[bool, Field(alias="isArchived")] = False
    default_branch: Annotated[_Branch | None, Field(alias="defaultBranchRef")] = None


class _TreeEntry(BaseModel):
    path: str = ""


class _Tree(BaseModel):
    tree: tuple[_TreeEntry, ...] = ()


@dataclass(frozen=True, slots=True)
class RemoteAgentFile:
    """One redundant agent file found on a repository's default branch."""

    repo: str
    path: str
    archived: bool

    def to_json(self) -> dict[str, str | bool]:
        """The flat JSON row the ``--json`` report prints."""
        return {"repo": self.repo, "path": self.path, "archived": self.archived}


#: Runs one `gh` invocation. Production runs the real CLI; tests answer from a table.
type GhCli = Callable[[list[str]], proc.Ran]


def run_gh_cli(args: list[str]) -> proc.Ran:
    """Run ``gh <args>`` for real."""
    return proc.gh(*args)


def _gh_json[T](run: GhCli, kind: type[T], *args: str) -> T | None:
    """Run one `gh` invocation and decode its stdout, or None when it fails or cannot be read."""
    ran = run(list(args))
    if not ran.ok or not ran.out.strip():
        return None
    return try_decode_json(kind, ran.out)


def list_owner_repos(owner: str, run: GhCli = run_gh_cli) -> list[OwnerRepo]:
    """Every repository *owner* owns, archived ones included."""
    listed = _gh_json(
        run,
        list[OwnerRepo],
        "repo",
        "list",
        owner,
        "--limit",
        "200",
        "--json",
        "nameWithOwner,isArchived,defaultBranchRef",
    )
    return listed or []


def default_branch_paths(repo: str, branch: str, run: GhCli = run_gh_cli) -> frozenset[str]:
    """Every path in *repo*'s *branch* tree, or empty when the tree cannot be read."""
    tree = _gh_json(run, _Tree, "api", f"repos/{repo}/git/trees/{branch}?recursive=1")
    if tree is None:
        return frozenset()
    return frozenset(entry.path for entry in tree.tree if entry.path)


def find_redundant_files(paths: Iterable[str]) -> list[str]:
    """The paths among *paths* that a tool reads instead of `AGENTS.md`.

    A directory entry matches any file beneath it, so `.github/instructions/`
    reports once no matter how many instruction files it holds.
    """
    present = set(paths)
    found = [name for name in LEGACY_INSTRUCTION_FILES if name in present]
    for relative in BESPOKE_AGENT_FILES:
        if relative.endswith("/"):
            stripped = relative.rstrip("/")
            if any(path.startswith(f"{stripped}/") for path in present):
                found.append(stripped)
        elif relative in present:
            found.append(relative)
    return found


def sweep_remote_agent_files(owners: Sequence[str], run: GhCli = run_gh_cli) -> list[RemoteAgentFile]:
    """Every redundant agent file on the default branch of every repo *owners* own."""
    findings: list[RemoteAgentFile] = []
    for owner in owners:
        for repo in list_owner_repos(owner, run):
            branch = repo.default_branch.name if repo.default_branch else ""
            if not branch:
                continue
            paths = default_branch_paths(repo.name_with_owner, branch, run)
            findings.extend(
                RemoteAgentFile(repo=repo.name_with_owner, path=path, archived=repo.is_archived)
                for path in find_redundant_files(paths)
            )
    return sorted(findings, key=lambda finding: (finding.repo, finding.path))

"""Common .gitignore governance manager.

Audits, inspects, and synchronizes common .gitignore rules across repositories
in the personal estate virtual monorepo according to policy in config/repo_policies.json.
"""

from __future__ import annotations

import pathlib
from collections.abc import Sequence
from dataclasses import dataclass

from pydantic import TypeAdapter

from rig import worktree_state
from rig.repo_governance.agent_files import (
    GITIGNORE_COMMENT,
    GITIGNORE_ENTRY_NAME,
)
from rig.repo_governance.policy_config import (
    GitignoreEntry,
    PolicyConfig,
    load_policy_config,
)
from rig.repo_governance.policy_key import policy_key
from rig.typed_json import JsonObject, JsonValue, decode_json

__all__ = [
    "GitignoreAlignment",
    "GitignoreInspection",
    "RepoGitignoreAlignment",
    "RepoGitignoreInspection",
    "align_gitignore",
    "inspect_gitignore",
    "required_entries",
]

#: Reported as missing when git does not ignore rig's per-worktree state folder (`rig.worktree_state`).
WORKTREE_STATE_ENTRY = "worktree-state"


@dataclass(frozen=True, slots=True)
class GitignoreInspection:
    """Whether a repo's root .gitignore holds every configured entry."""

    status: str
    is_aligned: bool
    languages: tuple[str, ...]
    missing: tuple[str, ...]
    missing_lines: tuple[str, ...]
    file: str


@dataclass(frozen=True, slots=True)
class GitignoreAlignment:
    """Outcome of appending the configured entries a repo's .gitignore was missing."""

    status: str
    file: str
    languages: tuple[str, ...]
    added: tuple[str, ...]


@dataclass(frozen=True, slots=True)
class RepoGitignoreInspection:
    """A :class:`GitignoreInspection` tagged with the repository it describes."""

    repo_name: str
    repo_path: str
    result: GitignoreInspection

    def to_json(self) -> JsonObject:
        """The flat JSON row the ``--json`` reports print."""
        return {"repo_name": self.repo_name, "repo_path": self.repo_path, **_json_fields(self.result)}


@dataclass(frozen=True, slots=True)
class RepoGitignoreAlignment:
    """A :class:`GitignoreAlignment` tagged with the repository it describes."""

    repo_name: str
    repo_path: str
    result: GitignoreAlignment

    def to_json(self) -> JsonObject:
        """The flat JSON row the ``--json`` reports print."""
        return {"repo_name": self.repo_name, "repo_path": self.repo_path, **_json_fields(self.result)}


def _json_fields(result: GitignoreInspection | GitignoreAlignment) -> JsonObject:
    return decode_json(dict[str, JsonValue], _ROW.dump_json(result))


_ROW: TypeAdapter[GitignoreInspection | GitignoreAlignment] = TypeAdapter(GitignoreInspection | GitignoreAlignment)


def required_entries(
    repo_path: pathlib.Path,
    config: PolicyConfig | None = None,
    languages: Sequence[str] | None = None,
) -> tuple[GitignoreEntry, ...]:
    """Every `.gitignore` entry this repo must satisfy, including the agent-file block.

    The bespoke agent-file block is derived from `AgentsMdRules.bespoke_agent_files`
    rather than declared a second time in config, so the audit and the ignore rule
    cannot drift: a path the audit fails is a path git ignores. One source of truth,
    two consumers.
    """
    cfg = config or load_policy_config()
    key = policy_key(repo_path)
    entries = cfg.gitignore_entries(key, repo_path=repo_path, languages=languages)
    bespoke = cfg.agents_md(key).bespoke_agent_files
    if not bespoke or any(entry.name == GITIGNORE_ENTRY_NAME for entry in entries):
        return entries
    return (
        *entries,
        GitignoreEntry(
            name=GITIGNORE_ENTRY_NAME,
            comment=GITIGNORE_COMMENT,
            write=bespoke,
            satisfied_by=bespoke,
            match_mode="all",
        ),
    )


def inspect_gitignore(
    repo_path: pathlib.Path,
    config: PolicyConfig | None = None,
    languages: Sequence[str] | None = None,
) -> GitignoreInspection:
    """Inspects whether the root .gitignore holds every configured entry.

    The required entries come from `config/repo_policies.json`, resolved for
    this repo's profile and detected or selected languages.
    """
    cfg = config or load_policy_config()
    key = policy_key(repo_path)
    resolved_languages = cfg.resolve_languages(key, repo_path=repo_path, explicit_languages=languages)
    entries = required_entries(repo_path, config=cfg, languages=resolved_languages)
    gitignore_file = repo_path / ".gitignore"

    if not entries:
        return GitignoreInspection(
            status="NO POLICY",
            is_aligned=True,
            languages=tuple(resolved_languages),
            missing=(),
            missing_lines=(),
            file=str(gitignore_file),
        )

    if not gitignore_file.exists():
        missing_lines = [line for e in entries for line in e.write]
        return GitignoreInspection(
            status="MISSING",
            is_aligned=False,
            languages=tuple(resolved_languages),
            missing=tuple(e.name for e in entries),
            missing_lines=tuple(missing_lines),
            file=str(gitignore_file),
        )

    content = gitignore_file.read_text(encoding="utf-8")
    missing_entries: list[GitignoreEntry] = []
    missing_lines: list[str] = []

    for e in entries:
        unmet = e.missing_lines(content)
        if unmet:
            missing_entries.append(e)
            missing_lines.extend(unmet)

    if not missing_entries and worktree_state.ignored(repo_path) is worktree_state.Ignored.NO:
        # The lines are there, yet git still sees the state folder: a later `!` rule re-includes it.
        state_dir = f"{worktree_state.STATE_DIR.as_posix()}/"
        return GitignoreInspection(
            status=f"NOT IGNORED {state_dir} (fix the .gitignore rule that re-includes it)",
            is_aligned=False,
            languages=tuple(resolved_languages),
            missing=(WORKTREE_STATE_ENTRY,),
            missing_lines=(),
            file=str(gitignore_file),
        )

    if not missing_entries:
        return GitignoreInspection(
            status="CONFIGURED",
            is_aligned=True,
            languages=tuple(resolved_languages),
            missing=(),
            missing_lines=(),
            file=str(gitignore_file),
        )

    listed = ", ".join(sorted(missing_lines))
    return GitignoreInspection(
        status=f"MISSING {listed}",
        is_aligned=False,
        languages=tuple(resolved_languages),
        missing=tuple(e.name for e in missing_entries),
        missing_lines=tuple(missing_lines),
        file=str(gitignore_file),
    )


def align_gitignore(
    repo_path: pathlib.Path,
    dry_run: bool = False,
    config: PolicyConfig | None = None,
    languages: Sequence[str] | None = None,
) -> GitignoreAlignment:
    """Appends any configured .gitignore entries the file is missing.

    Only missing lines are appended, so re-running is a no-op rather than a
    source of duplicate lines.
    """
    cfg = config or load_policy_config()
    key = policy_key(repo_path)
    resolved_languages = cfg.resolve_languages(key, repo_path=repo_path, explicit_languages=languages)
    entries = required_entries(repo_path, config=cfg, languages=resolved_languages)
    gitignore_file = repo_path / ".gitignore"

    if not entries:
        return GitignoreAlignment(
            status="no_policy", file=str(gitignore_file), languages=tuple(resolved_languages), added=()
        )

    content = gitignore_file.read_text(encoding="utf-8") if gitignore_file.exists() else ""

    missing_by_entry: dict[GitignoreEntry, list[str]] = {}
    for e in entries:
        unmet = e.missing_lines(content)
        if unmet:
            missing_by_entry[e] = unmet

    if not missing_by_entry:
        return GitignoreAlignment(
            status="already_configured", file=str(gitignore_file), languages=tuple(resolved_languages), added=()
        )

    appended = "".join(e.render_missing(unmet) for e, unmet in missing_by_entry.items())
    new_content = (content + appended) if content else appended.lstrip("\n")

    if not dry_run:
        gitignore_file.write_text(new_content, encoding="utf-8")

    added_lines = [line for lines in missing_by_entry.values() for line in lines]
    return GitignoreAlignment(
        status="aligned", file=str(gitignore_file), languages=tuple(resolved_languages), added=tuple(added_lines)
    )

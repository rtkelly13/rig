"""Project discovery and symlink management for the shared-utilities virtual monorepo.

The ``projects/`` directory at the repository root acts as a virtual monorepo —
peer repositories under the same parent directory (``C:\\code\\personal\\``) are
symlinked (Windows junctions) into ``projects/`` so that tooling can enumerate
all related repos from a single location.

This module provides:
- **Path helpers** — locate the repo root, projects dir, and peer directory.
- **Discovery** — scan the peer directory for git repos not yet linked.
- **Linking / unlinking** — create or remove junctions in ``projects/``.
- **Listing** — enumerate linked projects with summaries from AGENTS.md / README.md.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import sys
from dataclasses import dataclass, field
from pathlib import Path

from pydantic import StrictBool, StrictStr

from desktop_manager.models import JsonValue
from rig import proc, tracing
from rig.proc import ProcError
from rig.typed_json import load_json, try_decode
from rig.workspace_config import load_skip_repos, skip_reason

#: Repo-local LFS filters that check out pointer files instead of fetching content.
#: ``.git/config`` is shared by every worktree, so setting them once covers them all.
LFS_SKIP_FILTERS = {
    "filter.lfs.smudge": "git-lfs smudge --skip -- %f",
    "filter.lfs.process": "git-lfs filter-process --skip",
}

# ---------------------------------------------------------------------------
# Path helpers
# ---------------------------------------------------------------------------


def get_repo_root() -> Path:
    """Return the root of this repository (two levels above ``src/desktop_manager/``)."""
    return Path(__file__).resolve().parents[2]


def get_projects_dir() -> Path:
    """Return the ``projects/`` directory at the repo root (may not exist yet)."""
    return get_repo_root() / "projects"


def get_peer_dir() -> Path:
    """Return the parent directory where sibling repos live (e.g. ``C:\\code\\personal\\``)."""
    return get_repo_root().parent


def managed_projects_config_path() -> Path:
    """Return the committed managed-project registry path."""
    return get_repo_root() / "config" / "managed-projects.json"


# ---------------------------------------------------------------------------
# Repo summary extraction (mirrors workspace repo pattern)
# ---------------------------------------------------------------------------


def extract_repo_summary(repo_path: Path) -> str:
    """Return a one-line description from ``AGENTS.md`` or ``README.md``.

    Looks for the first non-blank paragraph after the top-level ``#`` heading.
    Falls back to an ``## OVERVIEW`` section if the first paragraph looks like
    auto-generated front-matter.
    """
    for filename in ("AGENTS.md", "README.md"):
        candidate = repo_path / filename
        if not candidate.is_file():
            continue
        summary = _extract_summary_from_file(candidate)
        if summary:
            return summary
    return ""


def _extract_summary_from_file(path: Path) -> str:
    """Read *path* and return the first descriptive paragraph after the H1."""
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""

    lines = text.splitlines()

    # Pass 1: first paragraph after H1
    found_h1 = False
    paragraph_lines: list[str] = []

    for line in lines:
        stripped = line.strip()

        if not found_h1:
            if re.match(r"^#\s+\S", stripped):
                found_h1 = True
            continue

        if re.match(r"^#{1,6}\s", stripped):
            break

        if stripped:
            paragraph_lines.append(stripped)
        elif paragraph_lines:
            break

    candidate = _strip_markdown_inline(" ".join(paragraph_lines)) if paragraph_lines else ""

    # Discard auto-generated front-matter and fall back to OVERVIEW
    if candidate and re.match(r"^\s*(Generated:|Parent:|Commit:|Version:)", candidate, re.IGNORECASE):
        candidate = ""

    if candidate:
        return candidate

    # Pass 2: first paragraph under ## OVERVIEW
    in_overview = False
    overview_lines: list[str] = []

    for line in lines:
        stripped = line.strip()

        if re.match(r"^##\s+OVERVIEW\s*$", stripped, re.IGNORECASE):
            in_overview = True
            continue

        if not in_overview:
            continue

        if re.match(r"^#{1,6}\s", stripped):
            break

        if stripped:
            overview_lines.append(stripped)
        elif overview_lines:
            break

    if overview_lines:
        return _strip_markdown_inline(" ".join(overview_lines))

    return ""


def _strip_markdown_inline(text: str) -> str:
    """Remove common Markdown inline markup, returning plain text."""
    text = re.sub(r"\[([^\]]+)\]\([^)]*\)", r"\1", text)  # links
    text = re.sub(r"\*{1,3}([^*]+)\*{1,3}", r"\1", text)  # bold / italic
    text = re.sub(r"_{1,2}([^_]+)_{1,2}", r"\1", text)  # underscores
    text = re.sub(r"`([^`]+)`", r"\1", text)  # inline code
    return text.strip()


# ---------------------------------------------------------------------------
# Data model
# ---------------------------------------------------------------------------


@dataclass
class ProjectInfo:
    """Metadata for a discovered or linked project."""

    name: str
    path: Path
    summary: str
    is_linked: bool


@dataclass
class ManagedProject:
    """A repo declared in the virtual-monorepo registry."""

    name: str
    repo: str
    description: str = ""
    branch: str | None = None
    enabled: bool = True
    clone: bool = True
    link: bool = True
    skip_lfs: bool = False


@dataclass
class ManagedProjectStatus:
    """Computed local/link status for a managed project."""

    project: ManagedProject
    local_path: Path
    link_path: Path
    is_cloned: bool
    is_linked: bool


@dataclass(frozen=True, slots=True)
class _ManagedProjectsFile:
    projects: list[object] = field(default_factory=list[object])


@dataclass(frozen=True, slots=True)
class _ManagedProjectEntry:
    name: StrictStr
    repo: StrictStr
    description: StrictStr = ""
    branch: StrictStr | None = None
    enabled: StrictBool = True
    clone: StrictBool = True
    link: StrictBool = True
    skip_lfs: StrictBool = False


@dataclass(frozen=True, slots=True)
class ClonedProject:
    path: Path
    link_path: Path | None


def infer_repo_name(repo_url: str) -> str:
    """Infer a local directory name from a git URL or path-like remote."""
    candidate = repo_url.rstrip("/")
    candidate = candidate.rsplit(":", maxsplit=1)[-1] if "://" not in candidate else candidate
    candidate = candidate.rstrip("/")
    name = Path(candidate).name
    if name.endswith(".git"):
        name = name[:-4]
    return name


def load_managed_projects(config_path: Path | None = None) -> list[ManagedProject]:
    """Load the managed virtual-monorepo project list from JSON."""
    path = config_path or managed_projects_config_path()
    data = try_decode(_ManagedProjectsFile, load_json(path.read_text(encoding="utf-8")))
    if data is None:
        msg = f"Invalid managed-project config: {path}"
        raise ValueError(msg)

    projects: list[ManagedProject] = []
    for item in data.projects:
        entry = try_decode(_ManagedProjectEntry, item)
        if entry is None:
            msg = f"Invalid managed-project entry in {path}"
            raise ValueError(msg)
        projects.append(
            ManagedProject(
                name=entry.name,
                repo=entry.repo,
                description=entry.description,
                branch=entry.branch,
                enabled=entry.enabled,
                clone=entry.clone,
                link=entry.link,
                skip_lfs=entry.skip_lfs,
            )
        )
    return projects


def managed_project_statuses(config_path: Path | None = None) -> list[ManagedProjectStatus]:
    """Return clone/link status for all enabled managed projects."""
    projects = [project for project in load_managed_projects(config_path) if project.enabled]
    linked_names = {project.name for project in collect_linked_projects()}

    statuses: list[ManagedProjectStatus] = []
    for project in projects:
        local_path = get_peer_dir() / project.name
        link_path = get_projects_dir() / project.name
        is_cloned = local_path.exists() and (local_path / ".git").exists()
        is_linked = project.name in linked_names and link_path.exists()
        statuses.append(
            ManagedProjectStatus(
                project=project,
                local_path=local_path,
                link_path=link_path,
                is_cloned=is_cloned,
                is_linked=is_linked,
            )
        )
    return statuses


def sync_managed_projects(
    *,
    names: list[str] | None = None,
    config_path: Path | None = None,
    clone_missing: bool = True,
    link_missing: bool = True,
    skip: frozenset[str] | None = None,
) -> list[str]:
    """Reconcile configured managed projects with local clones and project links.

    Projects in *skip* (by default this machine's `tools.estate.skip_repos`) are never
    cloned or linked, even when named.
    """
    skipped = load_skip_repos() if skip is None else skip
    statuses = managed_project_statuses(config_path)
    if names:
        requested = set(names)
        known = {status.project.name for status in statuses}
        unknown = sorted(requested - known)
        if unknown:
            msg = f"Unknown managed project(s): {', '.join(unknown)}"
            raise ValueError(msg)
        statuses = [status for status in statuses if status.project.name in requested]

    actions: list[str] = []
    for status in statuses:
        project = status.project
        if project.name in skipped:
            actions.append(f"skipped {project.name}: {skip_reason()}")
            continue
        if not status.is_cloned:
            if clone_missing and project.clone:
                clone_project(
                    project.repo, name=project.name, branch=project.branch, link=False, skip_lfs=project.skip_lfs
                )
                actions.append(f"cloned {project.name} -> {status.local_path}")
            else:
                actions.append(f"missing local clone for {project.name}")
        elif project.skip_lfs and ensure_lfs_skip(status.local_path):
            actions.append(f"set LFS skip-smudge on {project.name}")

        if link_missing and project.link:
            if not status.link_path.exists() and status.local_path.exists() and (status.local_path / ".git").exists():
                link_project(status.local_path, name=project.name)
                actions.append(f"linked {project.name} -> {status.link_path}")
            elif not status.link_path.exists() and not status.local_path.exists():
                actions.append(f"skipped link for {project.name}: local clone missing")

    return actions


# ---------------------------------------------------------------------------
# Discovery
# ---------------------------------------------------------------------------


def collect_linked_projects() -> list[ProjectInfo]:
    """Return metadata for every project currently linked in ``projects/``.

    Each entry in ``projects/`` that is a directory or symlink pointing at a
    git repo is included.  The list is sorted by name.
    """
    projects_dir = get_projects_dir()
    if not projects_dir.exists():
        return []

    results: list[ProjectInfo] = []
    for item in sorted(projects_dir.iterdir(), key=lambda p: p.name):
        if not (item.is_dir() or item.is_symlink()):
            continue
        real = item.resolve()
        if not (real / ".git").exists():
            continue
        results.append(
            ProjectInfo(
                name=item.name,
                path=real,
                summary=extract_repo_summary(real),
                is_linked=True,
            )
        )
    return results


def vscode_workspace_path() -> Path:
    """Return the path to the generated multi-root VS Code workspace file."""
    return get_repo_root() / "rig.code-workspace"


def generate_vscode_workspace(output_path: Path | None = None) -> Path:
    """Regenerate the multi-root ``.code-workspace`` from the linked projects.

    The workspace opens the ``rig`` repo root plus every repo
    currently linked in ``projects/``.  Linked repos are referenced by their
    real sibling paths (``../<name>``) rather than the ``projects/`` junctions,
    so VS Code does not show each repo twice (once nested under the root's
    ``projects/`` tree and once as its own root folder).
    """
    repo_root = get_repo_root()
    target = output_path or vscode_workspace_path()

    folders: list[JsonValue] = [{"path": "."}]
    for project in collect_linked_projects():
        relative = os.path.relpath(project.path, repo_root).replace(os.sep, "/")
        folders.append({"path": relative, "name": project.name})

    target.write_text(json.dumps({"folders": folders, "settings": {}}, indent=2) + "\n", encoding="utf-8")
    return target


def discover_unlinked_projects() -> list[ProjectInfo]:
    """Scan the peer directory for git repos not yet linked in ``projects/``.

    The repo containing this code (``shared-utilities``) is excluded since it
    is the monorepo root itself.
    """
    peer_dir = get_peer_dir()
    if not peer_dir.exists():
        return []

    # Build set of already-linked real paths
    linked_paths: set[Path] = set()
    for proj in collect_linked_projects():
        linked_paths.add(proj.path)

    repo_root_resolved = get_repo_root().resolve()
    results: list[ProjectInfo] = []

    for item in sorted(peer_dir.iterdir(), key=lambda p: p.name):
        if not item.is_dir():
            continue
        resolved = item.resolve()
        # Skip ourselves
        if resolved == repo_root_resolved:
            continue
        # Must be a git repo
        if not (resolved / ".git").exists():
            continue
        # Skip already linked
        if resolved in linked_paths:
            continue
        results.append(
            ProjectInfo(
                name=item.name,
                path=resolved,
                summary=extract_repo_summary(resolved),
                is_linked=False,
            )
        )
    return results


# ---------------------------------------------------------------------------
# Link / unlink operations
# ---------------------------------------------------------------------------


def link_project(project_path: Path, *, name: str | None = None) -> Path:
    """Create a junction (Windows) or symlink (Unix) in ``projects/``.

    Parameters
    ----------
    project_path:
        Absolute path to the git repo to link.
    name:
        Name for the link inside ``projects/``.  Defaults to the directory
        name of *project_path*.

    Returns
    -------
    Path
        The created link path.

    Raises
    ------
    FileExistsError
        If a link with the same name already exists.
    ValueError
        If *project_path* does not contain a ``.git`` directory.
    """
    resolved = project_path.resolve()
    if not (resolved / ".git").exists():
        msg = f"Not a git repository: {resolved}"
        raise ValueError(msg)

    link_name = name or resolved.name
    projects_dir = get_projects_dir()
    projects_dir.mkdir(exist_ok=True)

    link_path = projects_dir / link_name
    if link_path.exists() or link_path.is_symlink():
        msg = f"Already exists: {link_path}"
        raise FileExistsError(msg)

    _create_directory_link(link_path, resolved)
    return link_path


def clone_project(
    repo_url: str,
    *,
    name: str | None = None,
    branch: str | None = None,
    link: bool = True,
    skip_lfs: bool = False,
) -> ClonedProject:
    """Clone a repository into the peer dir and optionally link it into ``projects/``.

    With *skip_lfs*, LFS content is left as pointer files. ``git clone -c`` writes the
    filters into the new repo's config before the first checkout, so the clone and every
    worktree later added to it skip the download.
    """
    repo_name = name or infer_repo_name(repo_url)
    target_path = get_peer_dir() / repo_name
    if target_path.exists():
        msg = f"Already exists: {target_path}"
        raise FileExistsError(msg)

    command = ["clone"]
    if skip_lfs:
        for key, value in LFS_SKIP_FILTERS.items():
            command.extend(["-c", f"{key}={value}"])
    if branch:
        command.extend(["--branch", branch])
    command.extend([repo_url, str(target_path)])

    # cwd is the clone's parent, so a clone into an untrusted folder is hardened by rig.proc.
    try:
        proc.git(*command, cwd=target_path.parent, timeout=None, check=True)
    except ProcError as exc:
        _cleanup_failed_clone(target_path)
        if sys.platform == "win32" and "invalid path" in exc.ran.err.lower():
            msg = (
                f"Clone failed for {repo_name}: repository contains Windows-incompatible paths. "
                f"Use a non-Windows checkout or sanitize the repo history first."
            )
            raise ValueError(msg) from exc
        raise

    link_path = link_project(target_path, name=repo_name) if link else None
    return ClonedProject(target_path, link_path)


def ensure_lfs_skip(repo_path: Path) -> bool:
    """Set the LFS skip filters in an existing clone's config; return True if anything changed."""
    changed = False
    for key, value in LFS_SKIP_FILTERS.items():
        if proc.git_out("config", "--local", "--get", key, cwd=repo_path) != value:
            proc.git("config", "--local", key, value, cwd=repo_path, check=True)
            changed = True
    return changed


def _cleanup_failed_clone(target_path: Path) -> None:
    """Best-effort cleanup for a failed clone target."""
    if not target_path.exists():
        return
    if sys.platform == "win32":
        tracing.run(
            [
                "powershell",
                "-NoLogo",
                "-NoProfile",
                "-Command",
                f"if (Test-Path '{target_path}') {{ Remove-Item -LiteralPath '{target_path}' -Recurse -Force }}",
            ],
            check=False,
            capture_output=True,
            text=True,
        )
    shutil.rmtree(target_path, ignore_errors=True)


def _create_directory_link(link_path: Path, resolved: Path) -> None:
    """Create a directory junction or symlink for *resolved* at *link_path*."""
    if sys.platform == "win32":
        import _winapi

        _winapi.CreateJunction(str(resolved), str(link_path))
    else:
        link_path.symlink_to(resolved)


def unlink_project(name: str) -> Path:
    """Remove a junction/symlink from ``projects/``.

    Parameters
    ----------
    name:
        Name of the link to remove.

    Returns
    -------
    Path
        The removed link path.

    Raises
    ------
    FileNotFoundError
        If no link with *name* exists.
    ValueError
        If *name* is a path rather than a bare name, or the path exists but is
        not a symlink or junction.
    """
    if not name or name in {".", ".."} or "/" in name or "\\" in name or Path(name).name != name:
        msg = f"'{name}' is not a project name; pass the link's name, not a path"
        raise ValueError(msg)

    link_path = get_projects_dir() / name

    if not link_path.exists() and not link_path.is_symlink():
        msg = f"No linked project named '{name}'"
        raise FileNotFoundError(msg)

    if link_path.is_symlink():
        link_path.unlink()
    elif _is_junction(link_path):
        link_path.rmdir()  # junctions are removed with rmdir on Windows
    else:
        msg = f"'{link_path}' is not a symlink or junction — refusing to remove"
        raise ValueError(msg)

    return link_path


def _is_junction(path: Path) -> bool:
    """Return True if *path* is a Windows junction point."""
    try:
        return path.is_junction()
    except OSError:
        return False

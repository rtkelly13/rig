"""The package bindings declared in ``config/managed-projects.json``.

Tracking, auditing and the dashboard run from C# (``rig workspace packages``). What stays here is the
config reading that connectivity and the estate's port and Vercel naming still share, until they move too.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Literal

from rig.repo_slug import RepoSlug
from rig.typed_json import decode_json

Registry = Literal["npm", "nuget"]
AuthMode = Literal["oidc", "token"]


@dataclass(slots=True, frozen=True)
class PackageSpec:
    """One published package, as declared under a project's ``packages`` list."""

    registry: Registry
    name: str
    manifest: str
    workflow: str
    auth: AuthMode = "oidc"
    environment: str | None = None
    # NuGet only: the policy rig cannot read, declared so the audit can check around it.
    #: The nuget.org user or organisation that owns the policy.
    nuget_owner: str | None = None
    #: The profile name NuGet/login sends as ``user:``; an organisation's policy is exchanged by a member.
    nuget_user: str | None = None
    #: The package's expected nuget.org owners.
    owners: tuple[str, ...] = ()
    #: The policy's scopes, e.g. ``push-new`` and ``push-version``.
    scopes: tuple[str, ...] = ()


@dataclass(slots=True, frozen=True)
class PackageBinding:
    project: str
    repo: str
    spec: PackageSpec


@dataclass(slots=True, frozen=True)
class _ProjectEntry:
    name: str
    repo: str
    enabled: bool = True
    packages: tuple[PackageSpec, ...] = ()


@dataclass(slots=True, frozen=True)
class _ManagedProjects:
    projects: tuple[_ProjectEntry, ...] = ()


def load_package_bindings(cfg: Path) -> list[PackageBinding]:
    """Every ``projects[].packages[]`` entry in ``managed-projects.json``; a malformed one raises ``ValueError``."""
    managed = decode_json(_ManagedProjects, cfg.read_text(encoding="utf-8"))
    return [
        PackageBinding(project=project.name, repo=project.repo, spec=spec)
        for project in managed.projects
        if project.enabled
        for spec in project.packages
    ]


# ---------------------------------------------------------------- parsing


def repo_slug(url: str) -> str | None:
    """``git@github.com:o/r.git``, ``git+https://github.com/o/r.git`` and friends -> ``o/r``."""
    slug = RepoSlug.from_remote(url)
    return slug.full_name if slug else None

"""Vercel project names: which managed project a ``*.vercel.app`` URL belongs to.

Each project that deploys to Vercel lists its Vercel project names under
``vercel`` in ``config/managed-projects.json``::

    "vercel": ["design-system", "design-system-storybook"]

Vercel's generated URLs all start with the project name
(``<name>-<hash>-<team>``, ``<name>-git-<branch>-<team>``), and so do aliases
named the same way, so a host maps to the project whose declared name is its
longest prefix. ``repo-governance vercel-projects`` checks the declarations
against Vercel itself; this module only reads and validates them: names are
lowercase Vercel names, and no name is declared twice.
"""

from __future__ import annotations

import re
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

from pydantic import StrictBool, StrictStr

from rig.typed_json import decode_json
from rig.workspace.packages import repo_slug

_VERCEL_APP = ".vercel.app"
_NAME = re.compile(r"^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$")


@dataclass(slots=True, frozen=True)
class VercelClaim:
    """One managed project's Vercel project names, with the GitHub ``owner/repo`` they deploy."""

    project: str
    slug: str
    names: tuple[str, ...]


@dataclass(slots=True, frozen=True)
class _ProjectEntry:
    name: StrictStr
    repo: StrictStr
    enabled: StrictBool = True
    vercel: tuple[StrictStr, ...] = ()


@dataclass(slots=True, frozen=True)
class _ManagedProjects:
    projects: tuple[_ProjectEntry, ...] = ()


def load_vercel_claims(cfg: Path) -> list[VercelClaim]:
    """Every project's declared Vercel names; a malformed or twice-declared name raises ``ValueError``."""
    managed = decode_json(_ManagedProjects, cfg.read_text(encoding="utf-8"))
    owners: dict[str, str] = {}
    claims: list[VercelClaim] = []
    for project in managed.projects:
        if not project.vercel:
            continue
        slug = repo_slug(project.repo)
        if slug is None:
            raise ValueError(f"{project.name}: vercel projects need a GitHub repo to belong to, not {project.repo!r}")
        for name in project.vercel:
            if not _NAME.match(name):
                raise ValueError(f"{project.name}: {name!r} is not a Vercel project name")
            if name in owners:
                raise ValueError(f"{project.name}: vercel project {name!r} is already declared by {owners[name]}")
            owners[name] = project.name
        claims.append(VercelClaim(project.name, slug, project.vercel))
    return claims


def load_repo_slugs(cfg: Path) -> dict[str, str]:
    """Each enabled managed project's GitHub ``owner/repo``, by project name."""
    managed = decode_json(_ManagedProjects, cfg.read_text(encoding="utf-8"))
    slugs = {project.name: repo_slug(project.repo) for project in managed.projects if project.enabled}
    return {name: slug for name, slug in slugs.items() if slug is not None}


def claim_for_host(claims: Sequence[VercelClaim], host: str) -> VercelClaim | None:
    """The project whose declared name is the longest prefix of a ``*.vercel.app`` host, or None.

    Longest wins, so ``design-system-storybook-…`` is never taken by ``design-system``."""
    name = host.lower()
    if not name.endswith(_VERCEL_APP):
        return None
    label = name.removesuffix(_VERCEL_APP)
    best: VercelClaim | None = None
    best_length = 0
    for claim in claims:
        for declared in claim.names:
            if (label == declared or label.startswith(declared + "-")) and len(declared) > best_length:
                best, best_length = claim, len(declared)
    return best

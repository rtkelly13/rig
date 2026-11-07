"""Local port ranges: which managed project a local app's port belongs to.

Each project that runs local apps (dev servers, review UIs, Storybook) claims a
range under ``ports`` in ``config/managed-projects.json``::

    "ports": {"start": 8090, "end": 8099}

and starts its apps inside it. A port then names its project, so a browser tab
on ``localhost:8094`` is known to be rig's without asking the app, and two
repos' servers never contend for one port. The ranges are governance: this
module validates them strictly (inside 1024-65535, start <= end, no two
projects overlapping) and raises ``ValueError`` naming what is wrong, and a
test holds the committed config to that.
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import override

from pydantic import StrictBool, StrictInt, StrictStr

from rig.typed_json import decode_json
from rig.workspace.packages import repo_slug

_LOWEST, _HIGHEST = 1024, 65535


@dataclass(slots=True, frozen=True)
class PortRange:
    start: StrictInt
    end: StrictInt

    def __contains__(self, port: int) -> bool:
        return self.start <= port <= self.end

    def overlaps(self, other: PortRange) -> bool:
        return self.start <= other.end and other.start <= self.end

    @override
    def __str__(self) -> str:
        return f"{self.start}-{self.end}"


@dataclass(slots=True, frozen=True)
class PortClaim:
    """One project's port range, with the GitHub ``owner/repo`` its tabs group under."""

    project: str
    slug: str
    ports: PortRange


@dataclass(slots=True, frozen=True)
class _ProjectEntry:
    name: StrictStr
    repo: StrictStr
    enabled: StrictBool = True
    ports: PortRange | None = None


@dataclass(slots=True, frozen=True)
class _ManagedProjects:
    projects: tuple[_ProjectEntry, ...] = ()


def load_port_claims(cfg: Path) -> list[PortClaim]:
    """Every enabled project's port range; a malformed or overlapping one raises ``ValueError``."""
    managed = decode_json(_ManagedProjects, cfg.read_text(encoding="utf-8"))
    claims: list[PortClaim] = []
    for project in managed.projects:
        ports = project.ports
        if ports is None or not project.enabled:
            continue
        if not _LOWEST <= ports.start <= ports.end <= _HIGHEST:
            raise ValueError(f"{project.name}: ports {ports} must lie within {_LOWEST}-{_HIGHEST}, start <= end")
        slug = repo_slug(project.repo)
        if slug is None:
            raise ValueError(f"{project.name}: ports need a GitHub repo to belong to, not {project.repo!r}")
        for other in claims:
            if other.ports.overlaps(ports):
                raise ValueError(f"{project.name}: ports {ports} overlap {other.project}'s {other.ports}")
        claims.append(PortClaim(project.name, slug, ports))
    return claims


def claim_for(claims: Sequence[PortClaim], port: int) -> PortClaim | None:
    """The project whose range holds ``port``, or None when no project claims it."""
    return next((claim for claim in claims if port in claim.ports), None)

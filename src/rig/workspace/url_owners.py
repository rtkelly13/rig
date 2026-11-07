"""Which managed project a URL belongs to, from what the projects declare.

Two declarations in ``config/managed-projects.json`` tie a URL to a project
without asking the page: a local app's port inside the project's ``ports``
range (``ports``), and a ``*.vercel.app`` host starting with one of its
``vercel`` project names (``vercel_names``). Tools that see URLs, such as
chrome-tabs grouping, ask this one place.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlsplit

from rig.workspace.ports import PortClaim, claim_for, load_port_claims
from rig.workspace.vercel_names import VercelClaim, claim_for_host, load_vercel_claims

_LOCAL_HOSTS = frozenset({"localhost", "127.0.0.1", "::1"})


@dataclass(frozen=True, slots=True)
class UrlOwners:
    ports: tuple[PortClaim, ...] = ()
    vercel: tuple[VercelClaim, ...] = ()

    def slug_for(self, url: str) -> str | None:
        """The GitHub ``owner/repo`` of the project a local or Vercel URL belongs to, or None."""
        parts = urlsplit(url)
        host = (parts.hostname or "").lower()
        if host in _LOCAL_HOSTS:
            try:
                port = parts.port
            except ValueError:
                return None
            claim = claim_for(self.ports, port) if port is not None else None
            return claim.slug if claim else None
        vercel = claim_for_host(self.vercel, host)
        return vercel.slug if vercel else None


def load_url_owners(cfg: Path) -> UrlOwners:
    """Both declarations; a malformed one raises ``ValueError``."""
    return UrlOwners(tuple(load_port_claims(cfg)), tuple(load_vercel_claims(cfg)))

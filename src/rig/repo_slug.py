"""A GitHub repository's owner and name, parsed from any form a remote URL takes.

A leaf module: the estate, workspace, Orca and work-item code all read remotes, and each
used to carry its own regex.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import override
from urllib.parse import urlsplit

#: Hosts that serve github.com repos. Enterprise hosts are not part of the estate and parse to None.
GITHUB_HOSTS = frozenset({"github.com", "www.github.com", "ssh.github.com"})
_SEGMENT = re.compile(r"[\w.-]+")
_SCP_LIKE = re.compile(r"^(?:[\w.-]+@)?(?P<host>[\w.-]+):(?!//)(?P<path>.+)$")


@dataclass(frozen=True, slots=True)
class RepoSlug:
    """GitHub repository owner and name, as spelled in the remote."""

    owner: str
    name: str

    @classmethod
    def from_remote(cls, url: str | None) -> RepoSlug | None:
        """Parse a GitHub remote, or None for anything else.

        Accepts ``https://github.com/o/r(.git)``, ``ssh://git@github.com[:port]/o/r.git``,
        ``git@github.com:o/r.git``, ``git+https://…`` and Orca's scheme-less ``github.com/o/r``.
        """
        if not url:
            return None
        remote = _split(url.strip().removeprefix("git+"))
        if remote.host.lower() not in GITHUB_HOSTS:
            return None
        return cls.from_path(remote.path)

    @classmethod
    def from_path(cls, path: str) -> RepoSlug | None:
        """Parse ``owner/name`` (an optional ``.git`` and surrounding slashes are ignored)."""
        parts = path.strip("/").removesuffix(".git").split("/")
        if len(parts) != 2 or not all(_SEGMENT.fullmatch(part) for part in parts):
            return None
        owner, name = parts
        return cls(owner=owner, name=name)

    @property
    def full_name(self) -> str:
        """``owner/name`` as spelled in the remote."""
        return f"{self.owner}/{self.name}"

    @property
    def key(self) -> str:
        """``owner/name`` in lowercase, for comparing slugs: GitHub ignores case."""
        return self.full_name.lower()

    @override
    def __str__(self) -> str:
        return self.full_name


@dataclass(frozen=True, slots=True)
class _Remote:
    host: str
    path: str


def _split(url: str) -> _Remote:
    if "://" in url:
        parts = urlsplit(url)
        return _Remote(parts.hostname or "", parts.path)
    scp = _SCP_LIKE.match(url)
    if scp:
        return _Remote(scp.group("host"), scp.group("path"))
    host, _, path = url.partition("/")
    return _Remote(host, path)

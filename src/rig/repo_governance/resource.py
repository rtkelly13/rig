"""Reading and writing GitHub through the :class:`~rig.proc.GitHubApi` port for the ruleset checks.

The desired → observed → plan → apply resources behind `repo-governance plan` run from C#
(`src/Rig.Core/Governance/Resources.cs`); what is left here serves the commands still in Python.
"""

from __future__ import annotations

from pydantic import ValidationError

from rig.proc import ApiRequest, GitHubApi, Ran
from rig.typed_json import decode_json

__all__ = ["GitHubReadError", "read_json", "send"]


class GitHubReadError(Exception):
    """GitHub could not be read, so no plan can be trusted for this resource."""


def read_json[T](api: GitHubApi, kind: type[T], route: str) -> T:
    """GET *route* and decode it as *kind*; raises :class:`GitHubReadError` when either step fails."""
    ran = api(ApiRequest("GET", route))
    if not ran.ok:
        raise GitHubReadError(f"GET {route}: {ran.err.strip() or f'exit {ran.code}'}")
    try:
        return decode_json(kind, ran.out)
    except ValidationError as exc:
        raise GitHubReadError(f"GET {route}: unreadable response ({exc.error_count()} error(s))") from exc


def send(api: GitHubApi, method: str, route: str, body: str | None = None) -> Ran:
    """One write through the port."""
    return api(ApiRequest(method, route, body))

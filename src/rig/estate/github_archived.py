"""Which estate repos GitHub reports as archived, in one GraphQL query for all of them.

An archived repo is finished: it does not belong in Orca, and a local clone of it
is something to clean up. `config/archived-projects.json` lists retired projects,
but GitHub is the authority, so `estate orca` asks it too and a repo archived
there is treated as archived here without a config change.

A repo GitHub does not return (deleted, or no access) is simply absent from the
answer: deleted is not archived, and only a human should decide about it.
"""

from __future__ import annotations

import re
from collections.abc import Callable, Iterable
from dataclasses import dataclass, field
from typing import Annotated

from pydantic import Field

from rig import proc
from rig.typed_json import Lenient, try_decode_json

type GraphQL = Callable[[str], str | None]

#: GitHub owner and repo names; anything else is not sent, so a config value cannot shape the query.
_NAME = re.compile(r"^[A-Za-z0-9_.-]+$")


@dataclass(frozen=True, slots=True)
class _Repository:
    name_with_owner: Annotated[str, Field(alias="nameWithOwner")] = ""
    is_archived: Annotated[bool, Field(alias="isArchived")] = False


@dataclass(frozen=True, slots=True)
class _Reply:
    data: Lenient[dict[str, _Repository | None]] = None
    errors: list[object] = field(default_factory=list[object])


def run_gh(query: str) -> str | None:
    """`gh api graphql`'s stdout. It exits non-zero when any repo is missing but still prints the rest."""
    return proc.gh("api", "graphql", "-f", f"query={query}", timeout=60).out or None


def build_query(slugs: Iterable[str]) -> str:
    """One aliased `repository` lookup per valid `owner/name`."""
    parts: list[str] = []
    for index, slug in enumerate(sorted(set(slugs))):
        owner, _, name = slug.partition("/")
        if _NAME.match(owner) and _NAME.match(name):
            parts.append(f'r{index}: repository(owner: "{owner}", name: "{name}") {{ nameWithOwner isArchived }}')
    return "query { " + " ".join(parts) + " }" if parts else ""


def archived_slugs(slugs: Iterable[str], *, graphql: GraphQL = run_gh) -> frozenset[str] | None:
    """The lowercase `owner/name` of each archived repo among *slugs*; None when GitHub could not be asked."""
    query = build_query(slugs)
    if not query:
        return frozenset()
    out = graphql(query)
    reply = try_decode_json(_Reply, out) if out else None
    if reply is None or reply.data is None:
        return None
    return frozenset(r.name_with_owner.lower() for r in reply.data.values() if r is not None and r.is_archived)

"""Strict, paginated GitHub reads. A failed repository never becomes an empty queue."""

from __future__ import annotations

import hashlib
import subprocess
from dataclasses import dataclass, field, replace
from pathlib import Path
from typing import Annotated

from pydantic import Field

from rig.github_prs import PrRef
from rig.triage.github_facts import COMMON_METADATA, ISSUE_METADATA, Metadata, pr_metadata
from rig.triage.models import Activity, Item, Kind
from rig.typed_json import decode_json
from rig.workspace.engine import config_file_path


@dataclass(frozen=True, slots=True)
class Project:
    repo: str
    enabled: bool = True
    archived: bool = False


@dataclass(frozen=True, slots=True)
class Registry:
    projects: list[Project]


def repositories(config: Path | None = None) -> list[str]:
    """Enabled, nonarchived managed repositories; invalid config is a hard error."""
    registry = decode_json(Registry, (config or config_file_path()).read_text(encoding="utf-8"))
    slugs: set[str] = set()
    for project in registry.projects:
        if not project.enabled or project.archived:
            continue
        slug = project.repo.removeprefix("git@github.com:").removeprefix("https://github.com/").removesuffix(".git")
        slugs.add(PrRef.parse(f"{slug}#1").slug)
    if not slugs:
        raise ValueError("No enabled managed repositories")
    return sorted(slugs)


@dataclass(frozen=True, slots=True)
class Label:
    name: str


@dataclass(frozen=True, slots=True)
class Labels:
    total_count: Annotated[int, Field(alias="totalCount")] = 0
    nodes: list[Label] = field(default_factory=list[Label])


@dataclass(frozen=True, slots=True)
class Node(Metadata):
    number: int
    title: str
    state: str
    created_at: Annotated[str, Field(alias="createdAt")]
    updated_at: Annotated[str, Field(alias="updatedAt")]
    labels: Labels = field(default_factory=Labels)
    head_ref_oid: Annotated[str, Field(alias="headRefOid")] = ""
    is_draft: Annotated[bool, Field(alias="isDraft")] = False
    merge_state_status: Annotated[str, Field(alias="mergeStateStatus")] = "UNKNOWN"
    additions: int = 0
    deletions: int = 0
    body: str | None = None

    def item(self, repo: str, kind: Kind) -> Item:
        priorities = [
            int(label.name[-1])
            for label in self.labels.nodes
            if label.name.lower()
            in {"p0", "p1", "p2", "p3", "priority: p0", "priority: p1", "priority: p2", "priority: p3"}
        ]
        readiness = 0 if self.merge_state_status == "CLEAN" else 1 if self.merge_state_status == "BEHIND" else 2
        if self.is_draft or self.merge_state_status == "UNKNOWN":
            readiness = 3
        return Item(
            repo,
            self.number,
            kind,
            self.title,
            self.state,
            self.created_at,
            self.updated_at,
            self.head_ref_oid,
            min(priorities, default=4),
            readiness if kind == "pr" else 0,
            self.additions + self.deletions,
            body=self.body,
            facts=self.facts([label.name for label in self.labels.nodes], self.is_draft, self.labels.total_count),
        )


@dataclass(frozen=True, slots=True)
class Connection:
    nodes: list[Node]


@dataclass(frozen=True, slots=True)
class Repository:
    items: Connection


@dataclass(frozen=True, slots=True)
class Data:
    repository: Repository


@dataclass(frozen=True, slots=True)
class Page:
    data: Data
    errors: list[object] = field(default_factory=list[object])


COMMON_FIELDS = "number title body state createdAt updatedAt labels(first:100) { totalCount nodes { name } }"
PR_FIELDS = "headRefOid isDraft mergeStateStatus additions deletions"


def run(args: list[str]) -> str:
    """Bounded, read-only authenticated gh transport."""
    result = subprocess.run(
        ["gh", *args],
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=True,
        timeout=60,
        stdin=subprocess.DEVNULL,
    )
    return result.stdout


def fetch(repo: str, kind: Kind) -> list[Item]:
    """GraphQL cursor pagination rather than an arbitrary PR/issue list limit."""
    ref = PrRef.parse(f"{repo}#1")
    connection = "pullRequests" if kind == "pr" else "issues"
    fields = metadata_fields(kind, detailed=False)
    query = (
        'query($endCursor: String) { repository(owner: "' + ref.owner + '", name: "' + ref.repo + '") { '
        f"items: {connection}(first:25, after:$endCursor, states:OPEN) {{ "
        f"nodes {{ {fields} }} pageInfo {{ hasNextPage endCursor }} " + "} } }"
    )
    pages = decode_json(list[Page], run(["api", "graphql", "--paginate", "--slurp", "-f", f"query={query}"]))
    if not pages or any(page.errors for page in pages):
        raise ValueError(f"Incomplete GitHub response for {repo}")
    return [node.item(ref.slug, kind) for page in pages for node in page.data.repository.items.nodes]


def metadata_fields(kind: Kind, *, detailed: bool) -> str:
    """One schema for estate and selected-item reads."""
    extra = f"{PR_FIELDS} {pr_metadata(detailed=detailed)}" if kind == "pr" else ISSUE_METADATA
    return f"{COMMON_FIELDS} {COMMON_METADATA} {extra}"


@dataclass(frozen=True, slots=True)
class LookupRepository:
    item: Node | None


@dataclass(frozen=True, slots=True)
class LookupData:
    repository: LookupRepository


@dataclass(frozen=True, slots=True)
class Lookup:
    data: LookupData
    errors: list[object] = field(default_factory=list[object])


def observe(item: Item) -> Item:
    """Load one current snapshot, including check jobs and native stack membership."""
    ref = PrRef.parse(item.ref)
    field_name = "pullRequest" if item.kind == "pr" else "issue"
    query = (
        f'query {{ repository(owner:"{ref.owner}",name:"{ref.repo}") {{ '
        f"item:{field_name}(number:{ref.number}) {{ {metadata_fields(item.kind, detailed=True)} }} }} }}"
    )
    result = decode_json(Lookup, run(["api", "graphql", "-f", f"query={query}"]))
    if result.errors or result.data.repository.item is None:
        raise ValueError(f"Incomplete GitHub response for {item.ref}")
    return replace(result.data.repository.item.item(item.repo, item.kind), in_scope=item.in_scope)


@dataclass(frozen=True, slots=True)
class Author:
    login: str


@dataclass(frozen=True, slots=True)
class Discussion:
    id: int
    user: Author | None
    body: str | None = None
    updated_at: str = ""
    submitted_at: str = ""
    state: str = "COMMENTED"
    commit_id: str = ""


def activity(item: Item) -> list[Activity]:
    """Paginated comments and submitted reviews, with actor and commit attribution."""
    ref = PrRef.parse(item.ref)
    result: list[Activity] = []
    routes = [f"issues/{ref.number}/comments"]
    if item.kind == "pr":
        routes.append(f"pulls/{ref.number}/reviews")
        routes.append(f"pulls/{ref.number}/comments")
    for route in routes:
        pages = decode_json(
            list[list[Discussion]], run(["api", f"repos/{ref.slug}/{route}?per_page=100", "--paginate", "--slurp"])
        )
        for page in pages:
            for row in page:
                at = row.updated_at or row.submitted_at
                if not at:  # A pending review is private, not a submitted interaction.
                    continue
                result.append(
                    Activity(
                        f"{ref.slug}/{route}/{row.id}/{at}/{row.state}/"
                        + hashlib.sha256((row.body or "").encode("utf-8")).hexdigest(),
                        at,
                        row.user.login if row.user else "deleted-user",
                        f"github:{row.state.lower()}",
                        row.commit_id,
                        row.body or "",
                    )
                )
    return sorted(result, key=lambda event: (event.at, event.key))

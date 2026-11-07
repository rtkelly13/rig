"""Pull-request state and merge chains from GitHub, through the already-authenticated ``gh`` CLI.

Every caller that needs PR state shares this one resolver. The ``gh`` transport (`run_gh`, `GhRunner`,
`GhResult`) lives in `rig.proc`. Responses decode through pydantic into private dataclasses. Public API:

- ``PrRef`` / ``PrRef.parse`` / ``parse_ref``: an ``owner/repo#n`` reference, lowercased and validated.
- ``resolve_pr_states(refs) -> dict[PrRef, PrState]``: the state of each PR.
- ``resolve(refs) -> Resolution``: the states plus the stack and queue chains that include a requested PR.
- ``build_chains(open_prs) -> list[Chain]``: pure chain building from one repo's open PRs.

Per repository, one ``gh api graphql`` query lists the open PRs and looks each requested PR up by alias.
Nothing raises for a GitHub problem: a PR that cannot be resolved comes back ``UNKNOWN`` with the reason.
"""

from __future__ import annotations

import json
import re
import time
from collections import Counter, defaultdict, deque
from collections.abc import Iterable, Sequence
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from typing import Annotated, Literal, override

from pydantic import BeforeValidator, Field

from rig.proc import GhRunner, run_gh
from rig.typed_json import Lenient, decode, is_object, try_decode, try_decode_json

LOCK_LABEL = "locked-merging"
AUTOMERGE_LABEL = "automerge"
MAX_OPEN_PAGES = 10
LOOKUPS_PER_QUERY = 100

_REF = re.compile(r"^([a-z0-9-]+)/([a-z0-9._-]+)#([1-9][0-9]*)$")

State = Literal["OPEN", "MERGED", "CLOSED", "UNKNOWN"]
ChainKind = Literal["stack", "queue"]


# -- model ---------------------------------------------------------------------


@dataclass(frozen=True, order=True)
class PrRef:
    owner: str
    repo: str
    number: int

    def __post_init__(self) -> None:
        object.__setattr__(self, "owner", self.owner.lower())
        object.__setattr__(self, "repo", self.repo.lower())

    @property
    def slug(self) -> str:
        return f"{self.owner}/{self.repo}"

    @override
    def __str__(self) -> str:
        return f"{self.slug}#{self.number}"

    @classmethod
    def parse(cls, text: str) -> PrRef:
        """Like ``parse_ref``, but raises ``ValueError`` for anything that is not ``owner/repo#n``."""
        ref = parse_ref(text)
        if ref is None:
            raise ValueError(f"not an owner/repo#n reference: {text!r}")
        return ref


def parse_ref(text: str) -> PrRef | None:
    """``owner/repo#n``, lowercased. ``None`` for anything else, so nothing unvalidated reaches a query."""
    match = _REF.match(text.strip().lower())
    if match is None:
        return None
    return PrRef(match[1], match[2], int(match[3]))


@dataclass(frozen=True)
class PrDetails:
    """What a PR asks of the viewer, from the same lookup that reads its state."""

    additions: int = 0
    deletions: int = 0
    changed_files: int = 0
    draft: bool = False
    #: ``APPROVED``, ``CHANGES_REQUESTED``, ``REVIEW_REQUIRED``, or "" when the repo requires no review.
    review_decision: str = ""
    #: The head commit's check rollup: ``SUCCESS``, ``FAILURE``, ``ERROR``, ``PENDING``, ``EXPECTED``, or "".
    checks: str = ""
    authored_by_viewer: bool = False
    #: The viewer is personally requested (team requests are not attributed to the viewer).
    review_requested: bool = False

    @property
    def changed_lines(self) -> int:
        return self.additions + self.deletions

    @property
    def checks_failing(self) -> bool:
        return self.checks in ("FAILURE", "ERROR")


@dataclass(frozen=True)
class PrState:
    state: State
    error: str = ""
    #: None when the lookup failed. Not part of equality, so a state compares by state alone.
    details: PrDetails | None = field(default=None, compare=False)


@dataclass(frozen=True)
class OpenPr:
    number: int
    head: str
    base: str
    created_at: str = ""
    cross_repo: bool = False
    queue_position: int | None = None
    labels: frozenset[str] = frozenset()
    # Label name -> when it was last added.
    labeled_at: dict[str, str] = field(default_factory=dict[str, str])


@dataclass(frozen=True)
class Chain:
    kind: ChainKind
    base: str
    # Lowest (next to merge) first.
    members: tuple[int, ...]


@dataclass(frozen=True)
class RepoChain:
    kind: ChainKind
    slug: str
    base: str
    members: tuple[PrRef, ...]

    @classmethod
    def of(cls, owner: str, repo: str, chain: Chain) -> RepoChain:
        members = tuple(PrRef(owner, repo, number) for number in chain.members)
        return cls(chain.kind, f"{owner}/{repo}".lower(), chain.base, members)


@dataclass(frozen=True)
class Resolution:
    states: dict[PrRef, PrState]
    chains: list[RepoChain]
    # Repository slug -> why it could not be resolved. Its chains are unknowable, so none are returned for it.
    failed_repos: dict[str, str] = field(default_factory=dict[str, str])
    elapsed_ms: int = 0

    @property
    def errors(self) -> list[str]:
        return [f"{slug}: {error}" for slug, error in sorted(self.failed_repos.items())]


# -- chains --------------------------------------------------------------------


def build_stack_chains(open_prs: Iterable[OpenPr]) -> list[Chain]:
    """PR X sits on PR Y when X's base is Y's head. One chain per bottom PR, walked breadth-first.

    A stack can branch (two PRs on the same head), so members are ordered by depth, then number. PRs from
    forks are never a base, since their head branch is not in this repository, and neither is a head two open
    PRs share, since which one is below would be a guess.
    """
    prs = sorted(open_prs, key=lambda pr: pr.number)
    head_counts = Counter(pr.head for pr in prs if not pr.cross_repo)
    by_head = {pr.head: pr for pr in prs if not pr.cross_repo and head_counts[pr.head] == 1}
    children: dict[int, list[OpenPr]] = defaultdict(list)
    roots: list[OpenPr] = []
    for pr in prs:
        parent = by_head.get(pr.base)
        if parent is None or parent.number == pr.number:
            roots.append(pr)
        else:
            children[parent.number].append(pr)

    chains: list[Chain] = []
    for root in roots:
        members: list[int] = []
        pending = deque([root])
        while pending:
            node = pending.popleft()
            members.append(node.number)
            pending.extend(children.get(node.number, []))
        if len(members) > 1:
            chains.append(Chain("stack", root.base, tuple(members)))
    return chains


@dataclass(slots=True, frozen=True, order=True)
class _LabelOrder:
    labeled_at: str
    number: int


def _label_order(pr: OpenPr, label: str) -> _LabelOrder:
    return _LabelOrder(pr.labeled_at.get(label) or pr.created_at, pr.number)


def build_queue_chains(open_prs: Iterable[OpenPr]) -> list[Chain]:
    """Per base branch: the native merge queue by position, and the label queue.

    The label queue is whoever holds ``locked-merging``, then ``automerge`` PRs in the order that label was
    last added, falling back to when the PR was created. A PR in the native queue is not also label-queued.
    """
    by_base: dict[str, list[OpenPr]] = defaultdict(list)
    for pr in open_prs:
        by_base[pr.base].append(pr)

    chains: list[Chain] = []
    for base in sorted(by_base):
        prs = by_base[base]
        native = sorted(
            (pr for pr in prs if pr.queue_position is not None), key=lambda pr: (pr.queue_position or 0, pr.number)
        )
        labelled = [pr for pr in prs if pr.queue_position is None]
        holders = sorted(
            (pr for pr in labelled if LOCK_LABEL in pr.labels), key=lambda pr: _label_order(pr, LOCK_LABEL)
        )
        waiting = sorted(
            (pr for pr in labelled if AUTOMERGE_LABEL in pr.labels and LOCK_LABEL not in pr.labels),
            key=lambda pr: _label_order(pr, AUTOMERGE_LABEL),
        )
        for members in ([pr.number for pr in native], [pr.number for pr in holders + waiting]):
            if len(members) > 1:
                chains.append(Chain("queue", base, tuple(members)))
    return chains


def build_chains(open_prs: Iterable[OpenPr]) -> list[Chain]:
    """Stack chains, then queue chains, for one repository's full open-PR list. Only chains of 2+ members."""
    prs = list(open_prs)
    return build_stack_chains(prs) + build_queue_chains(prs)


# -- GraphQL -------------------------------------------------------------------

_OPEN_FIELDS = (
    "pageInfo { hasNextPage endCursor } nodes { number headRefName baseRefName createdAt isCrossRepository "
    "mergeQueueEntry { position } labels(first: 20) { nodes { name } } "
    "timelineItems(itemTypes: [LABELED_EVENT], last: 25) "
    "{ nodes { ... on LabeledEvent { createdAt label { name } } } } }"
)
_LOOKUP_FIELDS = (
    "number state merged baseRefName headRefName additions deletions changedFiles isDraft reviewDecision "
    "viewerDidAuthor reviewRequests(first: 20) { nodes { requestedReviewer { ... on User { isViewer } } } } "
    "commits(last: 1) { nodes { commit { statusCheckRollup { state } } } }"
)


def build_query(owner: str, repo: str, numbers: Sequence[int], *, open_list: bool, after: str | None = None) -> str:
    parts: list[str] = []
    if open_list:
        cursor = f", after: {json.dumps(after)}" if after else ""
        parts.append(f"open: pullRequests(states: OPEN, first: 100{cursor}) {{ {_OPEN_FIELDS} }}")
    parts.extend(f"pr{number}: pullRequest(number: {number}) {{ {_LOOKUP_FIELDS} }}" for number in numbers)
    return f"query {{ repository(owner: {json.dumps(owner)}, name: {json.dumps(repo)}) {{ {' '.join(parts)} }} }}"


@dataclass(slots=True, frozen=True)
class _Label:
    name: str = ""


@dataclass(slots=True, frozen=True)
class _Labels:
    nodes: tuple[_Label | None, ...] = ()


@dataclass(slots=True, frozen=True)
class _LabeledEvent:
    """Any other timeline item arrives as ``{}`` and decodes to the defaults."""

    created_at: Annotated[str, Field(alias="createdAt")] = ""
    label: _Label | None = None


@dataclass(slots=True, frozen=True)
class _Timeline:
    nodes: tuple[_LabeledEvent | None, ...] = ()


@dataclass(slots=True, frozen=True)
class _QueueEntry:
    position: int | None = None


@dataclass(slots=True, frozen=True)
class _OpenNode:
    number: int
    head_ref_name: Annotated[str, Field(alias="headRefName")] = ""
    base_ref_name: Annotated[str, Field(alias="baseRefName")] = ""
    created_at: Annotated[str, Field(alias="createdAt")] = ""
    is_cross_repository: Annotated[bool, Field(alias="isCrossRepository")] = False
    merge_queue_entry: Annotated[_QueueEntry | None, Field(alias="mergeQueueEntry")] = None
    labels: _Labels | None = None
    timeline_items: Annotated[_Timeline | None, Field(alias="timelineItems")] = None


@dataclass(slots=True, frozen=True)
class _PageInfo:
    has_next_page: Annotated[bool, Field(alias="hasNextPage")] = False
    end_cursor: Annotated[str | None, Field(alias="endCursor")] = None


@dataclass(slots=True, frozen=True)
class _OpenList:
    page_info: Annotated[_PageInfo, Field(alias="pageInfo")] = _PageInfo()
    nodes: tuple[_OpenNode | None, ...] = ()


@dataclass(slots=True, frozen=True)
class _OpenField:
    """The ``open`` alias of the repository object; null or absent reads as no list."""

    open: _OpenList | None = None


@dataclass(slots=True, frozen=True)
class _Reviewer:
    """A team reviewer arrives as ``{}``: team requests are not attributed to the viewer."""

    is_viewer: Annotated[bool, Field(alias="isViewer")] = False


@dataclass(slots=True, frozen=True)
class _ReviewRequest:
    requested_reviewer: Annotated[_Reviewer | None, Field(alias="requestedReviewer")] = None


@dataclass(slots=True, frozen=True)
class _ReviewRequests:
    nodes: tuple[_ReviewRequest | None, ...] = ()


@dataclass(slots=True, frozen=True)
class _Rollup:
    state: str | None = None


@dataclass(slots=True, frozen=True)
class _Commit:
    status_check_rollup: Annotated[_Rollup | None, Field(alias="statusCheckRollup")] = None


@dataclass(slots=True, frozen=True)
class _CommitNode:
    commit: _Commit | None = None


@dataclass(slots=True, frozen=True)
class _Commits:
    nodes: tuple[_CommitNode | None, ...] = ()


@dataclass(slots=True, frozen=True)
class _Lookup:
    state: str | None = None
    merged: bool | None = None
    additions: int | None = None
    deletions: int | None = None
    changed_files: Annotated[int | None, Field(alias="changedFiles")] = None
    is_draft: Annotated[bool | None, Field(alias="isDraft")] = None
    review_decision: Annotated[str | None, Field(alias="reviewDecision")] = None
    viewer_did_author: Annotated[bool | None, Field(alias="viewerDidAuthor")] = None
    review_requests: Annotated[_ReviewRequests | None, Field(alias="reviewRequests")] = None
    commits: _Commits | None = None


@dataclass(slots=True, frozen=True)
class _GraphQlError:
    message: str | None = None
    type: str | None = None
    path: tuple[str | int, ...] = ()


def _repository_views(value: object) -> object:
    """The repository object's aliases have different shapes, so it is read twice: as lookups and as the listing."""
    return {"lookups": value, "listing": value} if is_object(value) else value


@dataclass(slots=True, frozen=True)
class _Repository:
    #: Alias (``pr<n>``) -> its lookup; null or misshapen reads as ``None``. ``open`` lands here too, unused.
    lookups: dict[str, Lenient[_Lookup]]
    #: The ``open`` alias; ``None`` when it is misshapen.
    listing: Lenient[_OpenField]


@dataclass(slots=True, frozen=True)
class _Data:
    repository: Annotated[_Repository | None, BeforeValidator(_repository_views)] = None


@dataclass(slots=True, frozen=True)
class _Body:
    data: _Data | None = None
    errors: tuple[_GraphQlError, ...] | None = None


def parse_open_pr(node: object) -> OpenPr:
    """One node of the open-PR listing. Raises ``pydantic.ValidationError`` (a ``ValueError``) when misshapen."""
    return _open_pr(decode(_OpenNode, node))


def _open_pr(pr: _OpenNode) -> OpenPr:
    labels = frozenset(label.name.lower() for label in (pr.labels.nodes if pr.labels else ()) if label)
    labeled_at: dict[str, str] = {}
    for event in pr.timeline_items.nodes if pr.timeline_items else ():
        name = event.label.name.lower() if event and event.label else ""
        when = event.created_at if event else ""
        if name and when > labeled_at.get(name, ""):
            labeled_at[name] = when
    return OpenPr(
        number=pr.number,
        head=pr.head_ref_name,
        base=pr.base_ref_name,
        created_at=pr.created_at,
        cross_repo=pr.is_cross_repository,
        queue_position=pr.merge_queue_entry.position if pr.merge_queue_entry else None,
        labels=labels,
        labeled_at=labeled_at,
    )


class _GhError(RuntimeError):
    pass


@dataclass(slots=True, frozen=True)
class _Answer:
    repository: _Repository
    #: Alias -> why GitHub could not answer it.
    alias_errors: dict[str, str]


def _run(runner: GhRunner, query: str) -> _Answer:
    """The repository object, plus GraphQL errors keyed by alias. Raises _GhError when there is no data.

    ``gh`` exits 1 when any alias fails (a PR number that does not exist) but still prints the data.
    """
    result = runner(query)
    body = (try_decode_json(_Body, result.stdout) if result.stdout.strip() else None) or _Body()
    alias_errors: dict[str, str] = {}
    messages: list[str] = []
    for error in body.errors or ():
        message = error.message or error.type or "error"
        if len(error.path) >= 2:
            alias_errors[str(error.path[1])] = message
        else:
            messages.append(message)
    if body.data is None or body.data.repository is None:
        detail = "; ".join(messages) or result.stderr.strip() or f"gh exited {result.returncode}"
        raise _GhError(detail)
    return _Answer(body.data.repository, alias_errors)


def _lookup_state(answer: _Answer, number: int) -> PrState:
    alias = f"pr{number}"
    lookup = answer.repository.lookups.get(alias)
    if lookup is None:
        return PrState("UNKNOWN", answer.alias_errors.get(alias, "not found"))
    details = _details(lookup)
    if lookup.merged is True:
        return PrState("MERGED", details=details)
    match lookup.state:
        case "OPEN" | "MERGED" | "CLOSED" as state:
            return PrState(state, details=details)
        case state:
            return PrState("UNKNOWN", f"unexpected state {state or ''!r}")


def _open_list(answer: _Answer) -> _OpenList:
    decoded = answer.repository.listing
    if decoded is None:
        # Chains built from a partial list would be wrong, so this fails the repository.
        raise _GhError("unreadable open-PR list")
    return decoded.open or _OpenList()


def parse_details(node: object) -> PrDetails:
    """What a PR lookup says about size, review and checks. Every field is optional."""
    return _details(try_decode(_Lookup, node) or _Lookup())


def _details(lookup: _Lookup) -> PrDetails:
    requested = any(
        request is not None and request.requested_reviewer is not None and request.requested_reviewer.is_viewer
        for request in (lookup.review_requests.nodes if lookup.review_requests else ())
    )
    head = next(iter(lookup.commits.nodes if lookup.commits else ()), None)
    rollup = head.commit.status_check_rollup if head and head.commit else None
    return PrDetails(
        additions=lookup.additions or 0,
        deletions=lookup.deletions or 0,
        changed_files=lookup.changed_files or 0,
        draft=lookup.is_draft is True,
        review_decision=lookup.review_decision or "",
        checks=(rollup.state if rollup else None) or "",
        authored_by_viewer=lookup.viewer_did_author is True,
        review_requested=requested,
    )


@dataclass(frozen=True)
class RepoResolution:
    states: dict[int, PrState]
    chains: list[Chain]
    error: str = ""


def resolve_repo(owner: str, repo: str, numbers: Sequence[int], runner: GhRunner = run_gh) -> RepoResolution:
    """States for ``numbers`` and the repo's chains, built from its full open-PR list."""
    wanted = sorted(set(numbers))
    chunks = [wanted[i : i + LOOKUPS_PER_QUERY] for i in range(0, len(wanted), LOOKUPS_PER_QUERY)] or [[]]
    states: dict[int, PrState] = {}
    open_prs: list[OpenPr] = []
    try:
        cursor: str | None = None
        for page in range(MAX_OPEN_PAGES):
            lookups = chunks[0] if page == 0 else []
            answer = _run(runner, build_query(owner, repo, lookups, open_list=True, after=cursor))
            for number in lookups:
                states[number] = _lookup_state(answer, number)
            listing = _open_list(answer)
            open_prs.extend(_open_pr(node) for node in listing.nodes if node)
            if not listing.page_info.has_next_page:
                break
            cursor = listing.page_info.end_cursor or ""
        else:
            raise _GhError(f"more than {MAX_OPEN_PAGES * 100} open PRs")
        for chunk in chunks[1:]:
            answer = _run(runner, build_query(owner, repo, chunk, open_list=False))
            for number in chunk:
                states[number] = _lookup_state(answer, number)
    except _GhError as exc:
        # Chains need the complete open list, so a partial answer yields none.
        return RepoResolution({n: states.get(n, PrState("UNKNOWN", str(exc))) for n in wanted}, [], str(exc))
    return RepoResolution(states, build_chains(open_prs))


@dataclass(slots=True, frozen=True, order=True)
class _RepoKey:
    owner: str
    repo: str


def resolve(refs: Iterable[PrRef], *, runner: GhRunner = run_gh, max_workers: int = 8) -> Resolution:
    """States for ``refs``, and every chain that includes one of them. Repositories are queried in parallel."""
    started = time.perf_counter()
    by_repo: dict[_RepoKey, list[int]] = defaultdict(list)
    for ref in refs:
        by_repo[_RepoKey(ref.owner, ref.repo)].append(ref.number)
    repos = sorted(by_repo)
    with ThreadPoolExecutor(max_workers=max(1, min(max_workers, len(repos) or 1))) as pool:

        def resolve_one(key: _RepoKey) -> RepoResolution:
            return resolve_repo(key.owner, key.repo, by_repo[key], runner)

        resolved = list(pool.map(resolve_one, repos))

    states: dict[PrRef, PrState] = {}
    chains: list[RepoChain] = []
    failed: dict[str, str] = {}
    for key, result in zip(repos, resolved, strict=True):
        if result.error:
            failed[f"{key.owner}/{key.repo}"] = result.error
        for number, status in sorted(result.states.items()):
            states[PrRef(key.owner, key.repo, number)] = status
        wanted = set(by_repo[key])
        chains.extend(
            RepoChain.of(key.owner, key.repo, chain) for chain in result.chains if wanted.intersection(chain.members)
        )
    return Resolution(states, chains, failed, round((time.perf_counter() - started) * 1000))


def resolve_pr_states(refs: Iterable[PrRef], *, runner: GhRunner = run_gh) -> dict[PrRef, PrState]:
    """Just the state of each PR in ``refs``."""
    return resolve(refs, runner=runner).states

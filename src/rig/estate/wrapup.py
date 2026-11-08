"""Find Orca agent sessions that have gone quiet and ask each one whether its change is finished.

Orca shows when an agent stops, not whether its work is done. `estate wrapup` closes that gap in
three steps:

1. **Read Orca.** `orca worktree ps` lists each worktree with its agents (state, last prompt, last
   reply), its linked PR and its parent and child worktrees. Orca calls these subtasks.
   `orca terminal list` maps each agent pane to the terminal that can be written to.
2. **Check the facts first.** Rig's own git checks (`estate.checks`) run before anything is sent:
   uncommitted changes, commits on no remote, and whether HEAD is already on the default branch.
   With the PR state Orca already holds, a worktree that is clean, pushed and merged is *settled*
   and is not asked. That keeps one run from setting off a burst of agents that all go and check
   GitHub.
3. **Ask the rest.** Each remaining idle Claude session gets the prompt from
   `config/orca-wrapup.md`, behind a fixed marker. The agent answers by running
   `rig estate wrapup report` (`wrapup_report`), which writes its verdict to the state file; failing
   that, while the marker is the last prompt, the `WRAPUP:` line in its last reply is read instead.
   `--apply` hands the asking to a background follower (`wrapup_follow`) that keeps at most `--max`
   asks in flight (`wrapup_watch`): a slot frees when a session reports, answers, starts waiting on
   its owner, or has had `--wait`. Subtasks go first, so a parent's prompt carries their verdicts.

Each ask and each verdict is also written to the worktree's `temp/.rig/wrapup.json`
(`wrapup_state`). That record outlives the next prompt: it tells a send that never arrived
(`unconfirmed`) from work the owner resumed since, and keeps the last answer after Orca has moved on.

Sub-agent panes, busy agents and agents waiting on a question or permission are never written to.
A waiting agent is reported instead, since it is waiting on its owner.
"""

from __future__ import annotations

import datetime as dt
import re
from collections.abc import Callable, Iterable, Mapping
from dataclasses import dataclass, replace
from enum import StrEnum
from pathlib import Path
from string import Template
from typing import Annotated

from pydantic import Field, StrictBool, StrictFloat, StrictInt, StrictStr

from rig import proc, roots, scopes
from rig.estate import checks
from rig.estate.orca_runtime import Repo
from rig.estate.wrapup_report import report_command
from rig.estate.wrapup_state import (
    AnswerRecord,
    AskRecord,
    StateReader,
    WrapupState,
    default_store,
    new_ask_id,
    parse_timestamp,
)
from rig.typed_json import Lenient, try_decode_json

#: Prefixed to every wrap-up prompt. Orca keeps the first 200 characters of a prompt, so it must lead.
MARKER = "[Wrap-up check]"
#: The agents asked. Start with Claude; others answer prompts differently.
AGENTS: frozenset[str] = frozenset({"claude"})
DEFAULT_IDLE = dt.timedelta(minutes=30)
DEFAULT_MAX_SENDS = 5
#: How long `orca terminal send` waits to see the prompt submitted.
SUBMIT_WAIT_SECONDS = 10
TEMPLATE_FILE = "orca-wrapup.md"
_PS_LIMIT = "1000"
_ORCA_TIMEOUT = 60.0
_VERDICT = re.compile(
    r"^[^\w\n]*WRAPUP[^\w\n]*(?P<kind>done|pending)\b[^\w\n]*(?P<detail>.*)$", re.IGNORECASE | re.MULTILINE
)


class WrapupError(RuntimeError):
    """Orca could not be read: not running, or it answered with something unexpected."""


class Scope(StrEnum):
    PERSONAL = "personal"
    WORK = "work"


class Status(StrEnum):
    """What wrap-up does, or did, with one agent."""

    #: Idle long enough, not settled: the prompt goes out on --apply.
    ASK = "ask"
    SENT = "sent"
    SEND_FAILED = "send-failed"
    #: Would be asked, but this run's --max was reached.
    DEFERRED = "deferred"
    #: The wrap-up prompt was its last prompt and it has replied.
    ANSWERED = "answered"
    #: The wrap-up prompt was its last prompt and it is still replying.
    ANSWERING = "answering"
    #: Clean, pushed and merged: nothing to ask.
    SETTLED = "settled"
    WORKING = "working"
    #: Blocked on a question or a permission prompt: it is waiting for its owner.
    WAITING = "waiting"
    #: Idle for less than --idle.
    RECENT = "recent"
    #: No live, writable agent terminal for this pane.
    NO_TERMINAL = "no-terminal"
    #: Recorded as asked, but the pane has not moved since: the prompt never arrived. Not asked again.
    UNCONFIRMED = "unconfirmed"


# ---------------------------------------------------------------- Orca wire shapes


@dataclass(frozen=True, slots=True)
class _ProgramStatus:
    state: Lenient[StrictStr] = None
    kind: Lenient[StrictStr] = None
    message: Lenient[StrictStr] = None


@dataclass(frozen=True, slots=True)
class OrcaAgent:
    """One agent pane, as `orca worktree ps` reports it."""

    pane_key: Annotated[StrictStr, Field(alias="paneKey")]
    state: StrictStr
    agent_type: Annotated[Lenient[StrictStr], Field(alias="agentType")] = None
    #: Set on sub-agent panes, which belong to the agent that started them.
    parent_pane_key: Annotated[Lenient[StrictStr], Field(alias="parentPaneKey")] = None
    #: The last prompt, cut to 200 characters by Orca.
    prompt: Lenient[StrictStr] = None
    conversation_name: Annotated[Lenient[StrictStr], Field(alias="conversationName")] = None
    last_message: Annotated[Lenient[StrictStr], Field(alias="lastAssistantMessage")] = None
    tool_name: Annotated[Lenient[StrictStr], Field(alias="toolName")] = None
    #: Milliseconds since the epoch.
    state_started_at: Annotated[Lenient[StrictInt | StrictFloat], Field(alias="stateStartedAt")] = None
    program_status: Annotated[Lenient[_ProgramStatus], Field(alias="programStatus")] = None


@dataclass(frozen=True, slots=True)
class LinkedPr:
    number: StrictInt
    #: `open`, `draft`, `merged` or `closed`.
    state: Lenient[StrictStr] = None
    url: Lenient[StrictStr] = None
    checks_status: Annotated[Lenient[StrictStr], Field(alias="checksStatus")] = None


@dataclass(frozen=True, slots=True)
class OrcaWorktree:
    """One worktree, as `orca worktree ps` reports it."""

    worktree_id: Annotated[StrictStr, Field(alias="worktreeId")]
    repo_id: Annotated[StrictStr, Field(alias="repoId")]
    path: StrictStr
    repo: Lenient[StrictStr] = None
    display_name: Annotated[Lenient[StrictStr], Field(alias="displayName")] = None
    branch: Lenient[StrictStr] = None
    parent_id: Annotated[Lenient[StrictStr], Field(alias="parentWorktreeId")] = None
    linked_pr: Annotated[Lenient[LinkedPr], Field(alias="linkedPR")] = None
    agents: Lenient[tuple[Lenient[OrcaAgent], ...]] = None

    @property
    def name(self) -> str:
        return self.display_name or Path(self.path).name

    @property
    def branch_name(self) -> str:
        return (self.branch or "").removeprefix("refs/heads/")


@dataclass(frozen=True, slots=True)
class OrcaTerminal:
    """One live terminal, as `orca terminal list` reports it."""

    handle: StrictStr
    tab_id: Annotated[Lenient[StrictStr], Field(alias="tabId")] = None
    leaf_id: Annotated[Lenient[StrictStr], Field(alias="leafId")] = None
    #: The agent running in it; None for a plain shell, which must never be sent a prompt.
    agent_identity: Annotated[Lenient[StrictStr], Field(alias="agentIdentity")] = None
    writable: Lenient[StrictBool] = None

    @property
    def pane_key(self) -> str:
        return f"{self.tab_id}:{self.leaf_id}"


@dataclass(frozen=True, slots=True)
class _PsResult:
    worktrees: tuple[Lenient[OrcaWorktree], ...] = ()
    truncated: Lenient[StrictBool] = None


@dataclass(frozen=True, slots=True)
class _TerminalsResult:
    terminals: tuple[Lenient[OrcaTerminal], ...] = ()


@dataclass(frozen=True, slots=True)
class _ReposResult:
    repos: tuple[Lenient[Repo], ...] = ()


@dataclass(frozen=True, slots=True)
class _Error:
    message: Lenient[StrictStr] = None


@dataclass(frozen=True, slots=True)
class _Ps:
    ok: StrictBool = False
    result: Lenient[_PsResult] = None


@dataclass(frozen=True, slots=True)
class _Terminals:
    ok: StrictBool = False
    result: Lenient[_TerminalsResult] = None


@dataclass(frozen=True, slots=True)
class _Repos:
    ok: StrictBool = False
    result: Lenient[_ReposResult] = None


@dataclass(frozen=True, slots=True)
class _Receipt:
    ok: StrictBool = False
    error: Lenient[_Error] = None


# ---------------------------------------------------------------- reading Orca

type OrcaRunner = Callable[[list[str]], proc.Ran]


def run_orca(args: list[str]) -> proc.Ran:
    return proc.run(["orca", *args], timeout=_ORCA_TIMEOUT, scope="rig.estate.wrapup")


@dataclass(frozen=True, slots=True)
class OrcaSnapshot:
    worktrees: tuple[OrcaWorktree, ...]
    terminals: tuple[OrcaTerminal, ...]
    repos: tuple[Repo, ...]
    #: Orca returned only part of the worktree list.
    truncated: bool = False


def _call(run: OrcaRunner | None, args: list[str]) -> str:
    ran = (run or run_orca)(args)
    if not ran.ok:
        detail = (ran.err or ran.out).strip().splitlines()
        raise WrapupError(f"orca {' '.join(args[:2])} failed: {detail[0] if detail else f'exit {ran.code}'}")
    return ran.out


def read_worktrees(run: OrcaRunner | None = None) -> tuple[OrcaWorktree, ...]:
    """Only `orca worktree ps`: what a wait loop polls between sends."""
    ps = try_decode_json(_Ps, _call(run, ["worktree", "ps", "--limit", _PS_LIMIT, "--json"]))
    if ps is None or ps.result is None:
        raise WrapupError("orca answered in a shape this version of rig does not read")
    return tuple(w for w in ps.result.worktrees if w is not None)


def read_snapshot(run: OrcaRunner | None = None) -> OrcaSnapshot:
    """Worktrees with their agents, live terminals and registered repos. Raises WrapupError when Orca can't answer."""
    ps = try_decode_json(_Ps, _call(run, ["worktree", "ps", "--limit", _PS_LIMIT, "--json"]))
    terminals = try_decode_json(_Terminals, _call(run, ["terminal", "list", "--json"]))
    repos = try_decode_json(_Repos, _call(run, ["repo", "list", "--json"]))
    if ps is None or ps.result is None or terminals is None or terminals.result is None:
        raise WrapupError("orca answered in a shape this version of rig does not read")
    repo_rows = repos.result.repos if repos is not None and repos.result is not None else ()
    return OrcaSnapshot(
        worktrees=tuple(w for w in ps.result.worktrees if w is not None),
        terminals=tuple(t for t in terminals.result.terminals if t is not None),
        repos=tuple(r for r in repo_rows if r is not None),
        truncated=bool(ps.result.truncated),
    )


# ---------------------------------------------------------------- deterministic facts


@dataclass(frozen=True, slots=True)
class GitFacts:
    dirty: bool
    #: Commits reachable from HEAD that are on no remote: work that only exists here.
    unpushed: int
    #: HEAD is already on the default branch, by ancestry. False is "unknown" after a squash merge.
    merged: bool
    head: str = ""


type FactsReader = Callable[[Path], GitFacts | None]


def read_git_facts(path: Path) -> GitFacts | None:
    """Rig's estate checks for one worktree; None when the folder is gone."""
    if not path.is_dir():
        return None
    merged = any(checks.merged_by_ancestry(path, "HEAD", base=base) for base in ("origin/HEAD", "origin/main"))
    return GitFacts(
        dirty=checks.is_dirty(path),
        unpushed=checks.orphaned_count(path),
        merged=merged,
        head=proc.git_out("rev-parse", "HEAD", cwd=path),
    )


def is_settled(facts: GitFacts | None, pr: LinkedPr | None) -> bool:
    """Nothing left by the facts alone: clean, nothing unpushed, and the PR (or the branch) is merged."""
    if facts is None or facts.dirty or facts.unpushed:
        return False
    if pr is not None:
        return pr.state in {"merged", "closed"}
    return facts.merged


def describe_facts(facts: GitFacts | None, pr: LinkedPr | None) -> str:
    """One line a person, or the asked agent, can read."""
    parts: list[str] = []
    if facts is None:
        parts.append("folder missing")
    else:
        parts.append("uncommitted changes" if facts.dirty else "clean")
        if facts.unpushed:
            parts.append(f"{facts.unpushed} commit(s) on no remote")
        if pr is None:
            parts.append("nothing beyond the default branch" if facts.merged else "no PR")
    if pr is not None:
        pr_text = f"PR #{pr.number} {pr.state or '?'}"
        if pr.checks_status and pr.state not in {"merged", "closed"}:
            pr_text += f", checks {pr.checks_status}"
        parts.append(pr_text)
    return "; ".join(parts)


# ---------------------------------------------------------------- targeting


@dataclass(frozen=True, slots=True)
class Target:
    scope: Scope
    #: Orca repo display names; empty with `all_repos`.
    repos: tuple[str, ...] = ()
    all_repos: bool = False
    #: Worktree display names, folder names, paths or branches.
    worktrees: tuple[str, ...] = ()
    #: Follow child worktrees (Orca subtasks) of everything selected, across repos.
    children: bool = False


def classify(path: Path, code_root: Path) -> Scope | None:
    """Personal for the personal and org folders, work for this machine's work folders, None otherwise.

    Both lists are configuration: see `rig.scopes`.
    """
    resolved = path.expanduser()
    if any(resolved.is_relative_to(parent) for parent in scopes.repo_parent_dirs(code_root / "personal")):
        return Scope.PERSONAL
    if any(resolved.is_relative_to(work) for work in scopes.work_dirs(code_root)):
        return Scope.WORK
    return None


def _matches_worktree(worktree: OrcaWorktree, wanted: Iterable[str]) -> bool:
    names = {worktree.name.casefold(), Path(worktree.path).name.casefold(), worktree.branch_name.casefold()}
    for want in wanted:
        if want.casefold() in names:
            return True
        candidate = Path(want).expanduser()
        # `anchor`, not is_absolute(): on Windows a rooted path with no drive is not absolute.
        if candidate.anchor and candidate == Path(worktree.path):
            return True
    return False


@dataclass(frozen=True, slots=True)
class Placed:
    """A selected worktree and its depth in the subtask tree (0 = no selected parent)."""

    worktree: OrcaWorktree
    depth: int


def select(snapshot: OrcaSnapshot, target: Target, code_root: Path) -> list[Placed]:
    """The worktrees in scope, in tree order: each parent followed by its children."""
    repo_paths = {r.id: Path(r.path) for r in snapshot.repos}

    def scope_of(worktree: OrcaWorktree) -> Scope | None:
        return classify(repo_paths.get(worktree.repo_id, Path(worktree.path)), code_root)

    in_scope = [w for w in snapshot.worktrees if scope_of(w) is target.scope]
    by_id = {w.worktree_id: w for w in in_scope}
    kids: dict[str, list[OrcaWorktree]] = {}
    for w in in_scope:
        if w.parent_id:
            kids.setdefault(w.parent_id, []).append(w)

    wanted_repos = {name.casefold() for name in target.repos}
    chosen = [w for w in in_scope if target.all_repos or (w.repo or "").casefold() in wanted_repos]
    if target.worktrees:
        pool = in_scope if not (target.all_repos or wanted_repos) else chosen
        chosen = [w for w in pool if _matches_worktree(w, target.worktrees)]
    selected = {w.worktree_id for w in chosen}
    if target.children:
        stack = list(selected)
        while stack:
            for child in kids.get(stack.pop(), []):
                if child.worktree_id not in selected:
                    selected.add(child.worktree_id)
                    stack.append(child.worktree_id)

    def key(w: OrcaWorktree) -> str:
        return f"{(w.repo or '').casefold()}\0{w.name.casefold()}"

    placed: list[Placed] = []

    def walk(w: OrcaWorktree, depth: int) -> None:
        placed.append(Placed(w, depth))
        for child in sorted(kids.get(w.worktree_id, []), key=key):
            if child.worktree_id in selected:
                walk(child, depth + 1)

    tops = [by_id[i] for i in selected if not (by_id[i].parent_id and by_id[i].parent_id in selected)]
    for top in sorted(tops, key=key):
        walk(top, 0)
    return placed


# ---------------------------------------------------------------- planning


@dataclass(frozen=True, slots=True)
class Verdict:
    done: bool
    detail: str = ""


def parse_verdict(message: str | None) -> Verdict | None:
    """The first `WRAPUP: done` / `WRAPUP: pending: ...` line in a reply, or None when there isn't one."""
    found = _VERDICT.search(message or "")
    if found is None:
        return None
    return Verdict(
        done=found.group("kind").lower() == "done", detail=found.group("detail").strip().rstrip("*` ").strip()
    )


@dataclass(frozen=True, slots=True)
class Row:
    """One agent session and what wrap-up does with it."""

    repo: str
    worktree: str
    worktree_id: str
    path: str
    branch: str
    depth: int
    session: str
    state: str
    status: Status
    facts: GitFacts | None = None
    pr: LinkedPr | None = None
    idle_minutes: int | None = None
    terminal: str | None = None
    verdict: Verdict | None = None
    #: The waiting question, the reply's first line, or why a send failed.
    note: str = ""
    #: Ids of the selected child worktrees, for the prompt and the roll-up.
    children: tuple[str, ...] = ()
    pane: str = ""
    #: The last ask recorded in the state file for this pane.
    asked: AskRecord | None = None
    #: The last verdict recorded in the state file for this pane.
    recorded: AnswerRecord | None = None
    #: The id sent in this run's prompt, for `estate wrapup report --ask ID`.
    ask_id: str = ""


@dataclass(frozen=True, slots=True)
class Options:
    idle: dt.timedelta = DEFAULT_IDLE
    #: Asks past this many show as deferred. None defers nothing: a rolling `--wait` run asks them all.
    max_sends: int | None = DEFAULT_MAX_SENDS
    #: Ask settled worktrees too.
    include_settled: bool = False
    agents: frozenset[str] = AGENTS


def _idle(agent: OrcaAgent, now: dt.datetime) -> dt.timedelta | None:
    if agent.state_started_at is None:
        return None
    return now - dt.datetime.fromtimestamp(agent.state_started_at / 1000, tz=dt.UTC)


def _first_line(text: str | None, limit: int = 160) -> str:
    line = next((ln.strip() for ln in (text or "").splitlines() if ln.strip()), "")
    return line if len(line) <= limit else line[: limit - 1] + "…"


def _moved_since(agent: OrcaAgent, ask: AskRecord) -> bool:
    """Whether the pane changed state after *ask*: the prompt arrived, or its owner carried on."""
    asked_at = parse_timestamp(ask.at)
    if asked_at is None or agent.state_started_at is None:
        return True
    return agent.state_started_at / 1000 > asked_at.timestamp()


def _status(
    agent: OrcaAgent,
    terminal: OrcaTerminal | None,
    idle: dt.timedelta | None,
    settled: bool,
    options: Options,
    ask: AskRecord | None = None,
    answered: bool = False,
) -> Status:
    asked = (agent.prompt or "").lstrip().startswith(MARKER)
    if asked and answered:
        # The agent recorded its verdict (`estate wrapup report`), even if it is still finishing its turn.
        return Status.ANSWERED
    if agent.state == "working":
        return Status.ANSWERING if asked else Status.WORKING
    if agent.state == "waiting":
        return Status.WAITING
    if asked:
        return Status.ANSWERED
    if ask is not None and not answered and not _moved_since(agent, ask):
        return Status.UNCONFIRMED
    if terminal is None or not terminal.writable or terminal.agent_identity != agent.agent_type:
        return Status.NO_TERMINAL
    if idle is None or idle < options.idle:
        return Status.RECENT
    if settled and not options.include_settled:
        return Status.SETTLED
    return Status.ASK


def _verdict(agent: OrcaAgent, answer: AnswerRecord | None) -> Verdict | None:
    """The recorded answer to the latest ask, else the `WRAPUP:` line in the last reply."""
    if answer is not None:
        return Verdict(done=answer.verdict == "done", detail=answer.detail)
    return parse_verdict(agent.last_message)


def plan(
    placed: list[Placed],
    terminals: Iterable[OrcaTerminal],
    *,
    now: dt.datetime,
    options: Options,
    facts: FactsReader | None = None,
    state: StateReader | None = None,
) -> list[Row]:
    """One row per top-level agent of the chosen kinds, in tree order. Nothing is sent here."""
    by_pane = {t.pane_key: t for t in terminals}
    selected = {p.worktree.worktree_id for p in placed}
    rows: list[Row] = []
    for item in placed:
        w = item.worktree
        agents = [
            a
            for a in (w.agents or ())
            if a is not None and a.parent_pane_key is None and a.agent_type in options.agents
        ]
        if not agents:
            continue
        git = (facts or read_git_facts)(Path(w.path))
        settled = is_settled(git, w.linked_pr)
        saved = (state or default_store().read)(Path(w.path)) or WrapupState()
        children = tuple(
            p.worktree.worktree_id
            for p in placed
            if p.worktree.parent_id == w.worktree_id and p.worktree.worktree_id in selected
        )
        for agent in agents:
            terminal = by_pane.get(agent.pane_key)
            idle = _idle(agent, now)
            ask = saved.last_ask(agent.pane_key)
            recorded = saved.last_answer(agent.pane_key)
            answer = saved.answer_to(ask) if ask is not None else None
            status = _status(agent, terminal, idle, settled, options, ask, answered=answer is not None)
            verdict = _verdict(agent, answer) if status is Status.ANSWERED else None
            if status is Status.WAITING:
                ps = agent.program_status
                note = (ps.message if ps else None) or (f"waiting on {agent.tool_name}" if agent.tool_name else "")
            elif status is Status.ANSWERED and verdict is None:
                note = "replied without a WRAPUP line: " + _first_line(agent.last_message)
            elif status is Status.UNCONFIRMED and ask is not None:
                note = f"asked {ask.at[:16]}Z, but the pane has not moved since: check it"
            elif status is not Status.ANSWERED and recorded is not None:
                note = f"last answer {recorded.at[:16]}Z: {recorded.verdict}" + (
                    f": {recorded.detail}" if recorded.detail else ""
                )
            else:
                note = ""
            rows.append(
                Row(
                    repo=w.repo or "",
                    worktree=w.name,
                    worktree_id=w.worktree_id,
                    path=w.path,
                    branch=w.branch_name,
                    depth=item.depth,
                    session=agent.conversation_name or _first_line(agent.prompt, 60),
                    state=agent.state,
                    status=status,
                    facts=git,
                    pr=w.linked_pr,
                    idle_minutes=int(idle.total_seconds() // 60) if idle is not None else None,
                    terminal=terminal.handle if terminal is not None else None,
                    verdict=verdict,
                    note=_first_line(note, 240),
                    children=children,
                    pane=agent.pane_key,
                    asked=ask,
                    recorded=recorded,
                )
            )
    return _cap(rows, options.max_sends)


def _cap(rows: list[Row], max_sends: int | None) -> list[Row]:
    """Defer asks past --max; the deepest subtasks go first, so children answer before their parents."""
    if max_sends is None:
        return rows
    deferred = set(send_order(rows)[max(max_sends, 0) :])
    return [replace(r, status=Status.DEFERRED) if i in deferred else r for i, r in enumerate(rows)]


def send_order(rows: list[Row]) -> list[int]:
    """Indexes of the rows to ask, deepest subtasks first."""
    return sorted((i for i, r in enumerate(rows) if r.status is Status.ASK), key=lambda i: (-rows[i].depth, i))


# ---------------------------------------------------------------- the prompt


def template_path() -> Path:
    return roots.managed_projects_config().parent / TEMPLATE_FILE


def _child_summary(row: Row, rows_by_worktree: Mapping[str, list[Row]], worktrees: Mapping[str, OrcaWorktree]) -> str:
    if not row.children:
        return "none"
    lines: list[str] = []
    for child_id in row.children:
        child = worktrees.get(child_id)
        if child is None:
            continue
        child_rows = rows_by_worktree.get(child_id, [])
        verdicts = [r.verdict for r in child_rows if r.verdict is not None]
        if verdicts:
            said = (
                "done"
                if all(v.done for v in verdicts)
                else "pending: " + "; ".join(v.detail for v in verdicts if not v.done)
            )
        elif child_rows:
            said = ", ".join(sorted({r.status.value for r in child_rows}))
        else:
            said = "no agent"
        facts = describe_facts(_facts_of(child_rows), child.linked_pr)
        lines.append(f"{child.repo}/{child.name} ({child.branch_name}; {facts}; {said})")
    return "; ".join(lines) if lines else "none"


def _facts_of(rows: list[Row]) -> GitFacts | None:
    return rows[0].facts if rows else None


def render_prompt(template: str, row: Row, rows: list[Row], worktrees: Mapping[str, OrcaWorktree]) -> str:
    """The template behind the marker, with `$repo`, `$worktree`, `$branch`, `$facts`, `$children`, `$report`."""
    by_worktree: dict[str, list[Row]] = {}
    for r in rows:
        by_worktree.setdefault(r.worktree_id, []).append(r)
    body = Template(template).safe_substitute(
        repo=row.repo,
        worktree=row.worktree,
        branch=row.branch or "(detached)",
        facts=describe_facts(row.facts, row.pr),
        children=_child_summary(row, by_worktree, worktrees),
        report=report_command(row.ask_id),
    )
    return f"{MARKER} {body.strip()}"


@dataclass(frozen=True, slots=True)
class SendResult:
    ok: bool
    detail: str = ""


def send_prompt(handle: str, text: str, run: OrcaRunner | None = None) -> SendResult:
    """Paste *text* into an agent terminal and press Enter, waiting briefly to see it submitted."""
    ran = (run or run_orca)(
        [
            "terminal",
            "send",
            "--terminal",
            handle,
            "--text",
            text,
            "--enter",
            "--wait-submit",
            str(SUBMIT_WAIT_SECONDS),
            "--json",
        ]
    )
    receipt = try_decode_json(_Receipt, ran.out) if ran.out.strip() else None
    if ran.ok and (receipt is None or receipt.ok):
        return SendResult(ok=True)
    message = receipt.error.message if receipt is not None and receipt.error is not None else None
    return SendResult(ok=False, detail=message or _first_line(ran.err or ran.out) or f"exit {ran.code}")


def apply_sends(
    rows: list[Row],
    template: str,
    worktrees: Mapping[str, OrcaWorktree],
    *,
    run: OrcaRunner | None = None,
) -> list[Row]:
    """Send the prompt to every ASK row, deepest first, and record what happened."""
    out = list(rows)
    for i in send_order(out):
        row = replace(out[i], ask_id=new_ask_id())
        if row.terminal is None:
            out[i] = replace(row, status=Status.SEND_FAILED, note="no terminal")
            continue
        result = send_prompt(row.terminal, render_prompt(template, row, out, worktrees), run)
        out[i] = (
            replace(row, status=Status.SENT)
            if result.ok
            else replace(row, status=Status.SEND_FAILED, note=result.detail)
        )
    return out


def open_children(row: Row, rows: list[Row]) -> int:
    """Selected descendant sessions not yet known to be finished (settled or answered done)."""
    by_parent: dict[str, list[str]] = {}
    for r in rows:
        by_parent.setdefault(r.worktree_id, list(r.children))
    pending = 0
    stack = list(row.children)
    seen: set[str] = set()
    while stack:
        wid = stack.pop()
        if wid in seen:
            continue
        seen.add(wid)
        for r in (r for r in rows if r.worktree_id == wid):
            finished = r.status is Status.SETTLED or (r.verdict is not None and r.verdict.done)
            pending += 0 if finished else 1
        stack.extend(by_parent.get(wid, []))
    return pending

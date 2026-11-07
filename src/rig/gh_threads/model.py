"""Review threads as typed records, their one-line summaries, and the pure plan behind `resolve`.

No I/O: `github` observes, this module decides, `cli` renders and applies.
"""

from __future__ import annotations

import re
from collections.abc import Sequence
from dataclasses import dataclass

#: Shortest SHA prefix `resolve` accepts; git's own default abbreviation.
MIN_SHA_LENGTH = 7
#: Summaries are cut to this many characters.
SUMMARY_WIDTH = 120
#: A first paragraph shorter than this is a severity label (CodeRabbit's ``⚠️ Potential issue | 🟠 Major``),
#: so the next paragraph, the title, is joined to it.
_LABEL_WIDTH = 40

_HTML_COMMENT = re.compile(r"<!--.*?-->", re.DOTALL)
_DETAILS = re.compile(r"<details\b.*?</details>", re.DOTALL | re.IGNORECASE)
_IMAGE = re.compile(r"!\[([^\]]*)\]\([^)]*\)")
_TAG = re.compile(r"<[^>]+>")
_EMPHASIS = re.compile(r"(?<![\w*])[*_]+|[*_]+(?![\w*])")
_HEX = re.compile(r"[0-9a-fA-F]+")


@dataclass(frozen=True, slots=True)
class Thread:
    """One review thread, as `list` shows it. ``line`` is None for a file-level thread."""

    id: str
    path: str
    line: int | None
    outdated: bool
    resolved: bool
    author: str
    summary: str
    url: str
    body: str

    @property
    def location(self) -> str:
        """``path:line``, or just the path for a file-level thread."""
        return f"{self.path}:{self.line}" if self.line is not None else self.path


@dataclass(frozen=True, slots=True)
class ObservedThread:
    """A thread plus the body of its newest comment, which tells `resolve` whether it already replied."""

    thread: Thread
    latest_body: str


@dataclass(frozen=True, slots=True)
class PullRequest:
    """A PR's head branch and every commit on it (the commits the PR itself adds)."""

    head_ref: str
    head_oid: str
    commits: tuple[str, ...]


@dataclass(frozen=True, slots=True)
class ResolvePlan:
    """What `resolve` will do. ``reply`` is None when the reply is already the newest comment."""

    thread: Thread
    sha: str
    reply: str | None
    resolve: bool

    @property
    def is_empty(self) -> bool:
        """True when the thread is already replied to and resolved: a second run changes nothing."""
        return self.reply is None and not self.resolve


@dataclass(frozen=True, slots=True)
class Refusal:
    """Why `resolve` will not touch the thread."""

    reason: str


def summarise(body: str) -> str:
    """The first line or so of a review comment, with bot badges, HTML and emphasis stripped.

    A badge image becomes its alt text in brackets, so Codex's ``![P1 Badge](…)`` reads ``[P1]``.
    """
    text = _DETAILS.sub("", _HTML_COMMENT.sub("", body))
    text = _IMAGE.sub(_badge, text)
    text = _TAG.sub("", text)
    paragraphs = [" ".join(_EMPHASIS.sub("", part).split()) for part in re.split(r"\n\s*\n", text)]
    paragraphs = [part for part in paragraphs if part]
    if not paragraphs:
        return ""
    summary = paragraphs[0]
    if len(summary) < _LABEL_WIDTH and len(paragraphs) > 1:
        summary = f"{summary} — {paragraphs[1]}"
    return summary if len(summary) <= SUMMARY_WIDTH else summary[: SUMMARY_WIDTH - 1].rstrip() + "…"


def _badge(match: re.Match[str]) -> str:
    alt = match.group(1).strip().removesuffix("Badge").strip()
    return f"[{alt}]" if alt else ""


def reply_body(sha: str, note: str | None) -> str:
    """The reply `resolve` posts: ``Fixed in <sha>.`` and the note, if any, as its own paragraph."""
    text = f"Fixed in {sha}."
    return f"{text}\n\n{note.strip()}" if note and note.strip() else text


def plan_resolve(
    thread_id: str, sha: str, note: str | None, threads: Sequence[ObservedThread], pr: PullRequest
) -> ResolvePlan | Refusal:
    """Decide what resolving *thread_id* with the fix *sha* takes, or why it must not happen.

    The SHA gate always applies, even to a thread that is already resolved: the fix must be one of the PR's
    own commits, so a thread is never closed against a fix that was never pushed (or was rebased away).
    """
    observed = next((item for item in threads if item.thread.id == thread_id), None)
    if observed is None:
        return Refusal(f"{thread_id} is not a review thread on this pull request")
    found = match_commit(sha, pr)
    if isinstance(found, Refusal):
        return found
    body = reply_body(found, note)
    return ResolvePlan(
        thread=observed.thread,
        sha=found,
        reply=None if _already_replied(observed.latest_body, body, has_note=bool(note and note.strip())) else body,
        resolve=not observed.thread.resolved,
    )


def _already_replied(latest: str, body: str, *, has_note: bool) -> bool:
    """Whether the newest comment already says what *body* says, so a retry need not post it again.

    It must match exactly (whitespace aside), or a changed ``--note`` would be dropped. With no note asked
    for, any earlier ``Fixed in <sha>.`` reply for the same SHA, with or without a note, already covers it.
    """
    newest = " ".join(latest.split())
    wanted = " ".join(body.split())
    return newest == wanted or (not has_note and newest.startswith(wanted))


def match_commit(sha: str, pr: PullRequest) -> str | Refusal:
    """The full SHA of the PR commit *sha* names (a full SHA or a unique prefix), or why there is none."""
    wanted = sha.strip().lower()
    if len(wanted) < MIN_SHA_LENGTH or not _HEX.fullmatch(wanted):
        return Refusal(f"{sha!r} is not a commit SHA (need at least {MIN_SHA_LENGTH} hex characters)")
    matches = sorted({oid for oid in pr.commits if oid.lower().startswith(wanted)})
    if not matches:
        return Refusal(
            f"{sha} is not a commit on the PR's head branch {pr.head_ref} (head {pr.head_oid[:MIN_SHA_LENGTH]}); "
            "push the fix before resolving the thread"
        )
    if len(matches) > 1:
        return Refusal(f"{sha} is ambiguous on {pr.head_ref}: it matches {len(matches)} commits; give more of it")
    return matches[0]

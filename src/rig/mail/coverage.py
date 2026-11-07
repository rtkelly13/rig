"""Which senders in a saved inbox sample no rule claims yet.

The input is what the Gmail connector's ``search_threads`` returns, saved to a file per
page. Matching here is local and approximate: ``from``, ``to`` and ``subject`` are checked
the way Gmail broadly does (substring, case-insensitive); a rule that leans on ``query``
or ``exclude`` cannot be evaluated offline, so its claims are flagged as approximate.
"""

from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Annotated

from pydantic import Field, StrictStr, TypeAdapter

from rig.mail.rules import Match, Rule, RuleSet
from rig.typed_json import Lenient, decode_json


@dataclass(frozen=True, slots=True)
class _Message:
    sender: Lenient[StrictStr] = None
    subject: Lenient[StrictStr] = None
    to: Annotated[Lenient[tuple[StrictStr, ...]], Field(alias="toRecipients")] = None


@dataclass(frozen=True, slots=True)
class _Thread:
    messages: Lenient[tuple[_Message, ...]] = None


@dataclass(frozen=True, slots=True)
class _Page:
    threads: Lenient[tuple[_Thread, ...]] = None


@dataclass(frozen=True, slots=True)
class SampleMessage:
    """The newest message of one sampled thread."""

    sender: str
    subject: str
    to: tuple[str, ...]


@dataclass(frozen=True, slots=True)
class SenderRow:
    """One sender's share of the sample, how much of it rules claim, and which rules."""

    sender: str
    threads: int
    claimed: int
    rules: tuple[str, ...]
    approximate: bool
    unclaimed_subject: str | None

    @property
    def fully_claimed(self) -> bool:
        """Every sampled thread from this sender is claimed by some rule."""
        return self.claimed == self.threads


def read_sample(paths: list[Path]) -> list[SampleMessage]:
    """The newest message of every thread across the saved ``search_threads`` pages."""
    sample: list[SampleMessage] = []
    for path in paths:
        page = decode_json(_Page, path.read_text(encoding="utf-8"))
        for thread in page.threads or ():
            if not thread.messages:
                continue
            last = thread.messages[-1]
            sample.append(SampleMessage(last.sender or "", last.subject or "", last.to or ()))
    return sample


def _contains(haystack: str, needles: tuple[str, ...]) -> bool:
    lowered = haystack.lower()
    return any(needle.strip('"').lower() in lowered for needle in needles)


def claims(match: Match, message: SampleMessage) -> bool:
    """Whether ``match`` would select ``message``, judged on from, to and subject alone."""
    if match.senders and not _contains(message.sender, match.senders):
        return False
    if match.to and not any(_contains(addr, match.to) for addr in message.to):
        return False
    if match.subject and not _contains(message.subject, match.subject):
        return False
    return bool(match.senders or match.to or match.subject)


def _first_rule(ruleset: RuleSet, message: SampleMessage) -> Rule | None:
    return next((rule for rule in ruleset.rules if claims(rule.match, message)), None)


def coverage(ruleset: RuleSet, sample: list[SampleMessage]) -> list[SenderRow]:
    """Senders by thread count, busiest first; each message goes to the first rule claiming it."""
    counts = Counter(message.sender for message in sample)
    rows: list[SenderRow] = []
    for sender, count in counts.most_common():
        mine = [message for message in sample if message.sender == sender]
        owners = [_first_rule(ruleset, message) for message in mine]
        hits = [rule for rule in owners if rule is not None]
        misses = [message for message, rule in zip(mine, owners, strict=True) if rule is None]
        rows.append(
            SenderRow(
                sender=sender,
                threads=count,
                claimed=len(hits),
                rules=tuple(sorted({rule.name for rule in hits})),
                approximate=any(rule.match.query or rule.match.exclude for rule in hits),
                unclaimed_subject=misses[0].subject if misses else None,
            )
        )
    return rows


def coverage_json(rows: list[SenderRow]) -> str:
    """The coverage rows as indented JSON."""
    return TypeAdapter(list[SenderRow]).dump_json(rows, indent=2).decode()

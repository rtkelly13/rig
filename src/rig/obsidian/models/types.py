"""Result types for the vault link-integrity commands."""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class BrokenLink:
    """A wikilink pointing to a non-existent note."""

    source: str
    target: str
    line: int
    context: str


@dataclass(frozen=True, slots=True)
class UnlinkedMention:
    """A note title appearing in text without a wikilink."""

    source: str
    mention: str
    target: str


@dataclass(frozen=True, slots=True)
class LinkReport:
    """Combined output of ``run_list_links``."""

    broken: tuple[BrokenLink, ...]
    unlinked: tuple[UnlinkedMention, ...]


@dataclass(frozen=True, slots=True)
class FixLinksResult:
    """Output of ``run_fix_links``."""

    broken_found: int
    fixed: int

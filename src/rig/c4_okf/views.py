"""What the deriver and the prose beside it share: where the code came from, and Mermaid text helpers.

:mod:`rig.c4_okf.narrate` writes pages from a deriver it is handed, so it sees one through
:class:`Deriver` rather than importing :mod:`rig.c4_okf.derive`, which imports it.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

from rig.c4_okf.callgraph import CallGraph
from rig.c4_okf.dotnet_scan import Component


@dataclass(frozen=True, slots=True)
class Origin:
    """Where the code came from: the commit pins every derived view to one state of the repository."""

    commit: str
    date: str
    web: str | None

    def blob(self, repo: Path, path: Path) -> str | None:
        rel = path.relative_to(repo).as_posix()
        return f"{self.web}/blob/{self.commit}/{rel}" if self.web else None

    def tree(self) -> str | None:
        return f"{self.web}/tree/{self.commit}" if self.web else None


class Deriver(Protocol):
    """What a guide or change page reads from the deriver."""

    @property
    def repo(self) -> Path: ...

    @property
    def where(self) -> Origin: ...

    @property
    def graph(self) -> CallGraph | None: ...

    @property
    def home(self) -> dict[str, Component]: ...

    def parts(self) -> list[Component]: ...

    def code_id(self, part: Component) -> str: ...

    def label(self, part: Component) -> str: ...


def quote(text: str) -> str:
    """Text safe inside a Mermaid ``["label"]``."""
    return text.replace('"', "'")


def diagram(lines: list[str]) -> str:
    return "```mermaid\n" + "\n".join(lines) + "\n```\n"


def node_id(text: str) -> str:
    """A Mermaid id: letters, digits and underscores, the same for the same member on every run."""
    return "n_" + re.sub(r"[^A-Za-z0-9]", "_", text)


def plural(count: int, noun: str) -> str:
    return f"{count} {noun}{'' if count == 1 else 's'}"

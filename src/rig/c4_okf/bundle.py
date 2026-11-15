"""Load an OKF bundle of C4 concepts: one markdown file per concept, a Mermaid diagram on top, notes beneath.

OKF v0.2 rules this follows: a concept is any ``.md`` file except the reserved ``index.md`` and ``log.md``; its
id is its path inside the bundle without ``.md``; ``type`` is the only required frontmatter key; links between
concepts are plain markdown links, either relative to the file or absolute from the bundle root (``/a/b.md``).

The C4 layer on top is only a ``type`` convention (``C4 Context`` ... ``C4 Code``) plus Mermaid's own ``$link``
on an element, which names the concept that element zooms into.
"""

from __future__ import annotations

import posixpath
import re
from dataclasses import dataclass, field
from datetime import date
from pathlib import Path
from typing import Annotated

import yaml
from pydantic import BeforeValidator, StrictStr, ValidationError

from rig.c4_okf.mermaid_c4 import Diagram, parse_diagram
from rig.typed_json import Lenient
from rig.typed_yaml import decode_yaml

RESERVED = frozenset({"index.md", "log.md"})

#: C4 levels by OKF ``type``. Dynamic and deployment views sit beside the hierarchy, not in it.
LEVELS: dict[str, int] = {"C4 Context": 1, "C4 Container": 2, "C4 Component": 3, "C4 Code": 4}
SUPPLEMENTARY = frozenset({"C4 Landscape", "C4 Dynamic", "C4 Deployment"})
DIAGRAM_KEYWORDS: dict[str, str] = {
    "C4 Context": "C4Context",
    "C4 Container": "C4Container",
    "C4 Component": "C4Component",
    "C4 Dynamic": "C4Dynamic",
    "C4 Deployment": "C4Deployment",
    "C4 Landscape": "C4Context",
}

_FENCE = re.compile(r"^```(?:mermaid|pele)[ \t]*\n(?P<code>.*?)^```[ \t]*$", re.MULTILINE | re.DOTALL)
_MD_LINK = re.compile(r"(?<!!)\[(?P<text>[^\]]*)\]\((?P<href>[^)\s]+)(?:\s+\"[^\"]*\")?\)")
_HEADING = re.compile(r"^##[ \t]+(?P<text>.+?)[ \t]*$", re.MULTILINE)
_EXTERNAL = re.compile(r"^(?:[a-z][a-z0-9+.-]*:|//)", re.IGNORECASE)


def _iso(value: object) -> object:
    """YAML reads an unquoted timestamp as a date; keep it as the ISO text the author wrote."""
    return value.isoformat() if isinstance(value, date) else value


@dataclass(frozen=True, slots=True)
class Actor:
    """OKF ``generated`` / ``verified``: who did it (``human:<id>``, ``<producer>/<version>``) and when."""

    by: Lenient[StrictStr] = None
    at: Annotated[Lenient[StrictStr], BeforeValidator(_iso)] = None


@dataclass(frozen=True, slots=True)
class _Frontmatter:
    type: Lenient[StrictStr] = None
    title: Lenient[StrictStr] = None
    description: Lenient[StrictStr] = None
    resource: Lenient[StrictStr] = None
    tags: Lenient[tuple[StrictStr, ...]] = None
    generated: Lenient[Actor] = None
    verified: Lenient[Actor] = None


@dataclass(frozen=True, slots=True)
class BodyLink:
    """A markdown link in the notes. ``target`` is a concept id when it resolved inside the bundle."""

    text: str
    href: str
    target: str | None


@dataclass(frozen=True, slots=True)
class Concept:
    id: str
    path: Path
    type: str | None
    title: str
    description: str | None
    resource: str | None
    tags: tuple[str, ...]
    generated: Actor | None
    verified: Actor | None
    diagram: Diagram | None
    notes: str
    body_links: tuple[BodyLink, ...]
    frontmatter_error: str | None = None

    @property
    def level(self) -> int | None:
        return LEVELS.get(self.type or "")

    @property
    def is_c4(self) -> bool:
        return self.type in LEVELS or self.type in SUPPLEMENTARY

    def element_notes(self) -> dict[str, str]:
        """``## Heading`` sections in the notes, keyed by the alias or label of the element they describe."""
        if self.diagram is None:
            return {}
        sections: dict[str, str] = {}
        headings = list(_HEADING.finditer(self.notes))
        for index, heading in enumerate(headings):
            end = headings[index + 1].start() if index + 1 < len(headings) else len(self.notes)
            text = heading.group("text").strip().strip("`")
            body = self.notes[heading.end() : end].strip()
            for element in self.diagram.elements:
                if text.casefold() in (element.alias.casefold(), element.label.casefold()):
                    sections[element.alias] = body
        return sections


@dataclass(frozen=True, slots=True)
class Bundle:
    root: Path
    concepts: dict[str, Concept] = field(default_factory=dict[str, Concept])

    def get(self, concept_id: str) -> Concept | None:
        return self.concepts.get(concept_id)


def resolve_href(from_id: str, href: str) -> str | None:
    """OKF link to concept id: ``/x.md`` is bundle-absolute, anything else is relative to the linking file."""
    if not href or _EXTERNAL.match(href) or href.startswith("#"):
        return None
    path = href.split("#", 1)[0]
    joined = path.lstrip("/") if path.startswith("/") else posixpath.join(posixpath.dirname(from_id), path)
    normalised = posixpath.normpath(joined)
    if normalised.startswith(".."):
        return None
    return normalised.removesuffix(".md")


@dataclass(frozen=True, slots=True)
class _Split:
    frontmatter: str | None
    body: str


def split_frontmatter(text: str) -> _Split:
    if not text.startswith("---"):
        return _Split(None, text)
    lines = text.splitlines(keepends=True)
    for index in range(1, len(lines)):
        if lines[index].rstrip() == "---":
            return _Split("".join(lines[1:index]), "".join(lines[index + 1 :]))
    return _Split(None, text)


def _decode_frontmatter(raw: str | None) -> _Frontmatter | str:
    if raw is None:
        return "no YAML frontmatter"
    try:
        return decode_yaml(_Frontmatter, raw)
    except yaml.YAMLError as error:
        return f"frontmatter is not valid YAML: {error}"
    except ValidationError:
        return "frontmatter must be a YAML mapping"


def load_concept(root: Path, path: Path) -> Concept:
    concept_id = path.relative_to(root).with_suffix("").as_posix()
    split = split_frontmatter(path.read_text(encoding="utf-8"))
    decoded = _decode_frontmatter(split.frontmatter)
    meta = decoded if isinstance(decoded, _Frontmatter) else _Frontmatter()
    error = decoded if isinstance(decoded, str) else None

    fence = _FENCE.search(split.body)
    diagram = parse_diagram(fence.group("code")) if fence else None
    notes = (split.body[: fence.start()] + split.body[fence.end() :]).strip() if fence else split.body.strip()
    body_links = tuple(
        BodyLink(m.group("text"), m.group("href"), resolve_href(concept_id, m.group("href")))
        for m in _MD_LINK.finditer(notes)
    )
    return Concept(
        id=concept_id,
        path=path,
        type=meta.type,
        title=meta.title or path.stem.replace("-", " ").title(),
        description=meta.description,
        resource=meta.resource,
        tags=meta.tags or (),
        generated=meta.generated,
        verified=meta.verified,
        diagram=diagram,
        notes=notes,
        body_links=body_links,
        frontmatter_error=error,
    )


def load_bundle(root: Path) -> Bundle:
    """Every concept under ``root``, keyed by concept id. Hidden directories are skipped."""
    root = root.resolve()
    concepts: dict[str, Concept] = {}
    for path in sorted(root.rglob("*.md")):
        relative = path.relative_to(root)
        if path.name in RESERVED or any(part.startswith(".") for part in relative.parts):
            continue
        concept = load_concept(root, path)
        concepts[concept.id] = concept
    return Bundle(root=root, concepts=concepts)

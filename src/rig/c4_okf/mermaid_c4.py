"""Read the structure out of Mermaid C4 source: elements, boundaries, relationships and their ``$link`` targets.

This is deliberately a line-level reader, not a Mermaid parser. Pele parses and renders the diagram in the
browser and in the mermaid-toolkit lint; all the bundle checks need from Python is which aliases a diagram
declares, how they nest, what they link to, and which relationships it draws.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from enum import StrEnum


class ElementKind(StrEnum):
    PERSON = "person"
    SYSTEM = "system"
    CONTAINER = "container"
    COMPONENT = "component"
    BOUNDARY = "boundary"
    NODE = "node"
    CLASS = "class"


@dataclass(frozen=True, slots=True)
class Element:
    """One declared shape. ``line`` is its 0-based line in the diagram source; ``parent`` its enclosing boundary."""

    alias: str
    label: str
    kind: ElementKind
    macro: str
    external: bool
    link: str | None
    parent: str | None
    line: int


@dataclass(frozen=True, slots=True)
class Relationship:
    source: str
    target: str
    label: str
    line: int


@dataclass(frozen=True, slots=True)
class Diagram:
    """The structure of one Mermaid block. ``keyword`` is its first word: ``C4Context``, ``classDiagram``, ..."""

    keyword: str
    source: str
    elements: tuple[Element, ...]
    relationships: tuple[Relationship, ...]

    def element(self, alias: str) -> Element | None:
        return next((e for e in self.elements if e.alias == alias), None)

    @property
    def aliases(self) -> frozenset[str]:
        return frozenset(e.alias for e in self.elements)


C4_KEYWORDS = frozenset({"C4Context", "C4Container", "C4Component", "C4Dynamic", "C4Deployment"})

_BOUNDARIES = frozenset({"Boundary", "Enterprise_Boundary", "System_Boundary", "Container_Boundary"})
_NODES = frozenset({"Deployment_Node", "Node", "Node_L", "Node_R"})
_RELATIONSHIPS = frozenset(
    {
        "Rel",
        "BiRel",
        "Rel_U",
        "Rel_Up",
        "Rel_D",
        "Rel_Down",
        "Rel_L",
        "Rel_Left",
        "Rel_R",
        "Rel_Right",
        "Rel_Back",
        "RelIndex",
    }
)
_SHAPE_PREFIXES = (
    ("Person", ElementKind.PERSON),
    ("System", ElementKind.SYSTEM),
    ("Container", ElementKind.CONTAINER),
    ("Component", ElementKind.COMPONENT),
)

_MACRO = re.compile(r"^\s*(?P<name>[A-Za-z_]+)\s*\((?P<args>.*)\)\s*(?P<open>\{)?\s*$")
_CLICK = re.compile(r'^\s*click\s+(?P<alias>[\w-]+)\s+(?:href\s+)?"(?P<href>[^"]+)"')
_CLASS = re.compile(r"^\s*class\s+(?P<alias>[\w-]+)")


def _split_args(raw: str) -> list[str]:
    """Split macro arguments on commas outside double quotes, stripping quotes from each."""
    parts: list[str] = []
    current: list[str] = []
    quoted = False
    for char in raw:
        if char == '"':
            quoted = not quoted
            current.append(char)
        elif char == "," and not quoted:
            parts.append("".join(current))
            current = []
        else:
            current.append(char)
    parts.append("".join(current))
    return [p.strip() for p in parts if p.strip()]


def _unquote(value: str) -> str:
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] == '"':
        return value[1:-1]
    return value


@dataclass(frozen=True, slots=True)
class _Args:
    positional: tuple[str, ...]
    link: str | None


def _parse_args(raw: str) -> _Args:
    positional: list[str] = []
    link: str | None = None
    for part in _split_args(raw):
        if part.startswith("$"):
            key, _, value = part.partition("=")
            if key.strip() == "$link":
                link = _unquote(value)
            continue
        positional.append(_unquote(part))
    return _Args(tuple(positional), link)


def _shape_kind(name: str) -> ElementKind | None:
    if name in _BOUNDARIES:
        return ElementKind.BOUNDARY
    if name in _NODES:
        return ElementKind.NODE
    for prefix, kind in _SHAPE_PREFIXES:
        if name.startswith(prefix):
            return kind
    return None


def parse_diagram(source: str) -> Diagram:
    """Read a Mermaid block. C4 macros give full structure; other diagrams only ``click`` links and classes."""
    lines = source.splitlines()
    keyword = next((ln.split()[0] for ln in lines if ln.strip() and not ln.strip().startswith("%%")), "")
    parts = _parse_c4(lines) if keyword in C4_KEYWORDS else _C4Parts(_parse_clicks(lines), ())
    return Diagram(keyword=keyword, source=source, elements=parts.elements, relationships=parts.relationships)


@dataclass(frozen=True, slots=True)
class _C4Parts:
    elements: tuple[Element, ...]
    relationships: tuple[Relationship, ...]


def _parse_c4(lines: list[str]) -> _C4Parts:
    elements: list[Element] = []
    relationships: list[Relationship] = []
    stack: list[str] = []
    for index, line in enumerate(lines):
        stripped = line.strip()
        if stripped == "}":
            if stack:
                _ = stack.pop()
            continue
        match = _MACRO.match(line)
        if match is None:
            continue
        name = match.group("name")
        args = _parse_args(match.group("args"))
        if name in _RELATIONSHIPS and len(args.positional) >= 2:
            label = args.positional[2] if len(args.positional) > 2 else ""
            relationships.append(Relationship(args.positional[0], args.positional[1], label, index))
            continue
        kind = _shape_kind(name)
        if kind is None or not args.positional:
            continue
        alias = args.positional[0]
        label = args.positional[1] if len(args.positional) > 1 else alias
        elements.append(
            Element(
                alias=alias,
                label=label,
                kind=kind,
                macro=name,
                external=name.endswith("_Ext"),
                link=args.link,
                parent=stack[-1] if stack else None,
                line=index,
            )
        )
        if match.group("open"):
            stack.append(alias)
    return _C4Parts(tuple(elements), tuple(relationships))


def _parse_clicks(lines: list[str]) -> tuple[Element, ...]:
    """Class and flowchart diagrams (the C4 code level): classes, plus ``click X href "..."`` as zoom links."""
    links: dict[str, str] = {}
    classes: dict[str, int] = {}
    for index, line in enumerate(lines):
        if (click := _CLICK.match(line)) is not None:
            links[click.group("alias")] = click.group("href")
        elif (cls := _CLASS.match(line)) is not None:
            _ = classes.setdefault(cls.group("alias"), index)
    aliases = dict.fromkeys([*classes, *links])
    return tuple(
        Element(
            alias=alias,
            label=alias,
            kind=ElementKind.CLASS,
            macro="class",
            external=False,
            link=links.get(alias),
            parent=None,
            line=classes.get(alias, -1),
        )
        for alias in aliases
    )

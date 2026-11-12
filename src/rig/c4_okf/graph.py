"""How a bundle's documents compose: zoom edges, the merged element model, bundle checks and expanded views.

Composition happens three ways, all derived from plain OKF links and Mermaid aliases:

* **Zoom** — an element's ``$link`` names the concept that refines it. Several diagrams may zoom into the same
  concept, so the hierarchy is a DAG, not C4's tree.
* **Identity** — an alias used in several diagrams is one element seen in several views. The merged model lists
  every view each element appears in, and flags aliases whose labels disagree.
* **Expansion** — a parent view with one zoomed element replaced by the boundary of its child view, so two
  documents render as one diagram.
"""

from __future__ import annotations

import re
from collections import defaultdict
from dataclasses import dataclass
from enum import StrEnum

from rig.c4_okf.bundle import DIAGRAM_KEYWORDS, Bundle, Concept, resolve_href
from rig.c4_okf.mermaid_c4 import C4_KEYWORDS, Element, ElementKind


class EdgeKind(StrEnum):
    ZOOM = "zoom"
    ZOOM_OUT = "zoom-out"
    MENTION = "mention"


def _direction(bundle: Bundle, source: str, target: str) -> EdgeKind:
    """A ``$link`` to a shallower level is a way back up, not a refinement."""
    from_level, to_level = bundle.concepts[source].level, bundle.concepts[target].level
    if from_level is not None and to_level is not None and to_level < from_level:
        return EdgeKind.ZOOM_OUT
    return EdgeKind.ZOOM


@dataclass(frozen=True, slots=True)
class Edge:
    """``source`` links to ``target``; ``alias`` is the diagram element carrying a zoom link."""

    source: str
    target: str
    kind: EdgeKind
    alias: str | None
    href: str


@dataclass(frozen=True, slots=True)
class BrokenLink:
    source: str
    href: str
    alias: str | None


@dataclass(frozen=True, slots=True)
class Links:
    edges: tuple[Edge, ...]
    broken: tuple[BrokenLink, ...]

    def zooms_from(self, concept_id: str) -> tuple[Edge, ...]:
        return tuple(e for e in self.edges if e.source == concept_id and e.kind is EdgeKind.ZOOM)

    def into(self, concept_id: str) -> tuple[Edge, ...]:
        return tuple(e for e in self.edges if e.target == concept_id)


def collect_links(bundle: Bundle) -> Links:
    edges: list[Edge] = []
    broken: list[BrokenLink] = []
    for concept in bundle.concepts.values():
        if concept.diagram is not None:
            for element in concept.diagram.elements:
                if not element.link:
                    continue
                target = resolve_href(concept.id, element.link)
                if target is None and not element.link.startswith(("http:", "https:", "//")):
                    broken.append(BrokenLink(concept.id, element.link, element.alias))
                elif target is not None and target in bundle.concepts:
                    kind = _direction(bundle, concept.id, target)
                    edges.append(Edge(concept.id, target, kind, element.alias, element.link))
                elif target is not None:
                    broken.append(BrokenLink(concept.id, element.link, element.alias))
        for link in concept.body_links:
            if link.target is None:
                continue
            if link.target in bundle.concepts:
                edges.append(Edge(concept.id, link.target, EdgeKind.MENTION, None, link.href))
            elif link.href.endswith(".md") or ".md#" in link.href:
                broken.append(BrokenLink(concept.id, link.href, None))
    return Links(tuple(edges), tuple(broken))


def roots(bundle: Bundle, links: Links) -> tuple[str, ...]:
    """Entry points: context-level concepts, then any diagram nothing zooms into."""
    zoomed = {e.target for e in links.edges if e.kind is EdgeKind.ZOOM}
    context = [c.id for c in bundle.concepts.values() if c.level == 1]
    loose = [c.id for c in bundle.concepts.values() if c.is_c4 and c.level != 1 and c.id not in zoomed]
    return tuple(sorted(context) + sorted(loose))


# --- checks -------------------------------------------------------------------------------------------------


class Severity(StrEnum):
    ERROR = "error"
    WARNING = "warning"
    INFO = "info"


@dataclass(frozen=True, slots=True)
class Finding:
    code: str
    severity: Severity
    concept: str
    message: str


def _okf_findings(concept: Concept) -> list[Finding]:
    if concept.frontmatter_error:
        return [Finding("okf-frontmatter", Severity.ERROR, concept.id, concept.frontmatter_error)]
    if not concept.type:
        return [Finding("okf-type", Severity.ERROR, concept.id, "OKF requires a `type` in the frontmatter")]
    return []


def _diagram_findings(concept: Concept) -> list[Finding]:
    if not concept.is_c4:
        return []
    if concept.diagram is None:
        return [Finding("diagram-missing", Severity.WARNING, concept.id, f"`{concept.type}` has no mermaid block")]
    expected = DIAGRAM_KEYWORDS.get(concept.type or "")
    keyword = concept.diagram.keyword
    if expected is not None and keyword != expected:
        message = f"`type: {concept.type}` but the diagram is `{keyword}`; expected `{expected}`"
        return [Finding("diagram-kind", Severity.WARNING, concept.id, message)]
    if concept.level == 4 and keyword in C4_KEYWORDS:
        message = "code level is usually a class or ER diagram, not a C4 macro diagram"
        return [Finding("diagram-kind", Severity.INFO, concept.id, message)]
    return []


def _zoom_findings(bundle: Bundle, links: Links) -> list[Finding]:
    findings = [
        Finding(
            "zoom-broken" if b.alias else "link-broken",
            Severity.ERROR if b.alias else Severity.WARNING,
            b.source,
            f"`{b.alias}` zooms to `{b.href}`, which is not in the bundle"
            if b.alias
            else f"note links to `{b.href}`, which is not in the bundle",
        )
        for b in links.broken
    ]
    for edge in links.edges:
        if edge.kind is not EdgeKind.ZOOM:
            continue
        source, target = bundle.concepts[edge.source], bundle.concepts[edge.target]
        if source.level is None or target.level is None:
            continue
        # Code views link sideways to each other, where a call leaves one namespace for another: not a zoom.
        if source.type == target.type == "C4 Code":
            continue
        if target.level != source.level + 1:
            message = f"`{edge.alias}` zooms from {source.type} to {target.type}; a zoom normally goes one level deeper"
            findings.append(Finding("zoom-level", Severity.WARNING, edge.source, message))
    return findings


def _reachability_findings(bundle: Bundle, links: Links) -> list[Finding]:
    findings: list[Finding] = []
    zoom_sources: defaultdict[str, set[str]] = defaultdict(set)
    any_inbound: set[str] = set()
    for edge in links.edges:
        any_inbound.add(edge.target)
        if edge.kind is EdgeKind.ZOOM:
            zoom_sources[edge.target].add(edge.source)
    for concept in bundle.concepts.values():
        sources = zoom_sources.get(concept.id, set())
        if concept.level is not None and concept.level > 1 and not sources:
            message = f"no diagram zooms into this {concept.type}; it is only reachable by search"
            findings.append(Finding("orphan", Severity.WARNING, concept.id, message))
        elif not concept.is_c4 and concept.id not in any_inbound:
            findings.append(Finding("orphan", Severity.INFO, concept.id, "no concept links to this note"))
        if len(sources) > 1:
            message = f"shared by {len(sources)} views: {', '.join(sorted(sources))}"
            findings.append(Finding("fan-in", Severity.INFO, concept.id, message))
    return findings


def _refinement_findings(bundle: Bundle, links: Links) -> list[Finding]:
    """A relationship drawn to a zoomed element in the parent should still be visible in the child view."""
    findings: list[Finding] = []
    for edge in links.edges:
        if edge.kind is not EdgeKind.ZOOM or edge.alias is None:
            continue
        parent, child = bundle.concepts[edge.source].diagram, bundle.concepts[edge.target].diagram
        if parent is None or child is None or child.keyword not in C4_KEYWORDS:
            continue
        for rel in parent.relationships:
            if edge.alias not in (rel.source, rel.target):
                continue
            other = rel.target if rel.source == edge.alias else rel.source
            if other not in child.aliases:
                message = (
                    f"`{edge.source}` draws {rel.source} → {rel.target} ({rel.label or 'unlabelled'}), "
                    f"but `{other}` does not appear in this view"
                )
                findings.append(Finding("rel-unrefined", Severity.WARNING, edge.target, message))
    return findings


def _identity_findings(model: Model) -> list[Finding]:
    findings: list[Finding] = []
    for element in model.elements:
        if len(element.labels) > 1:
            labels = ", ".join(f"“{label}”" for label in element.labels)
            message = f"alias `{element.alias}` has different labels across views: {labels}"
            findings.append(Finding("alias-conflict", Severity.WARNING, element.views[0], message))
    return findings


def check_bundle(bundle: Bundle) -> tuple[Finding, ...]:
    links = collect_links(bundle)
    findings: list[Finding] = []
    for concept in bundle.concepts.values():
        findings += _okf_findings(concept) + _diagram_findings(concept)
        if concept.is_c4 and concept.verified is None:
            findings.append(Finding("unverified", Severity.INFO, concept.id, "no `verified` — nobody has checked it"))
    findings += _zoom_findings(bundle, links)
    findings += _reachability_findings(bundle, links)
    findings += _refinement_findings(bundle, links)
    findings += _identity_findings(build_model(bundle, links))
    order = {Severity.ERROR: 0, Severity.WARNING: 1, Severity.INFO: 2}
    return tuple(sorted(findings, key=lambda f: (order[f.severity], f.concept, f.code)))


# --- merged model -------------------------------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class ModelElement:
    """One alias across every view that declares it."""

    alias: str
    labels: tuple[str, ...]
    kinds: tuple[ElementKind, ...]
    external: bool
    views: tuple[str, ...]
    zooms_to: tuple[str, ...]


@dataclass(frozen=True, slots=True)
class ModelRelationship:
    source: str
    target: str
    labels: tuple[str, ...]
    views: tuple[str, ...]


@dataclass(frozen=True, slots=True)
class Model:
    elements: tuple[ModelElement, ...]
    relationships: tuple[ModelRelationship, ...]


@dataclass(slots=True)
class _ElementAcc:
    labels: dict[str, None]
    kinds: dict[ElementKind, None]
    external: bool
    views: dict[str, None]
    zooms_to: dict[str, None]


@dataclass(slots=True)
class _RelAcc:
    labels: dict[str, None]
    views: dict[str, None]


@dataclass(frozen=True, slots=True)
class _ZoomKey:
    concept: str
    alias: str


@dataclass(frozen=True, slots=True)
class _RelKey:
    source: str
    target: str


def build_model(bundle: Bundle, links: Links | None = None) -> Model:
    links = links or collect_links(bundle)
    zooms = {_ZoomKey(e.source, e.alias or ""): e.target for e in links.edges if e.kind is EdgeKind.ZOOM}
    elements: dict[str, _ElementAcc] = {}
    relationships: dict[_RelKey, _RelAcc] = {}
    for concept in bundle.concepts.values():
        if concept.diagram is None:
            continue
        for element in concept.diagram.elements:
            acc = elements.setdefault(element.alias, _ElementAcc({}, {}, element.external, {}, {}))
            acc.labels[element.label] = None
            acc.kinds[element.kind] = None
            acc.views[concept.id] = None
            if (target := zooms.get(_ZoomKey(concept.id, element.alias))) is not None:
                acc.zooms_to[target] = None
        for rel in concept.diagram.relationships:
            racc = relationships.setdefault(_RelKey(rel.source, rel.target), _RelAcc({}, {}))
            if rel.label:
                racc.labels[rel.label] = None
            racc.views[concept.id] = None
    return Model(
        elements=tuple(
            ModelElement(alias, tuple(a.labels), tuple(a.kinds), a.external, tuple(a.views), tuple(a.zooms_to))
            for alias, a in sorted(elements.items())
        ),
        relationships=tuple(
            ModelRelationship(key.source, key.target, tuple(r.labels), tuple(r.views))
            for key, r in relationships.items()
        ),
    )


# --- tree ---------------------------------------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class TreeNode:
    """A zoom path through the DAG. ``repeat`` marks a concept already shown higher up this walk."""

    id: str
    title: str
    type: str | None
    via: str | None
    repeat: bool
    children: tuple[TreeNode, ...]


def zoom_tree(bundle: Bundle, links: Links | None = None) -> tuple[TreeNode, ...]:
    links = links or collect_links(bundle)
    seen: set[str] = set()

    def walk(concept_id: str, via: str | None, path: frozenset[str]) -> TreeNode:
        concept = bundle.concepts[concept_id]
        repeat = concept_id in seen or concept_id in path
        seen.add(concept_id)
        children = (
            () if repeat else tuple(walk(e.target, e.alias, path | {concept_id}) for e in links.zooms_from(concept_id))
        )
        return TreeNode(concept_id, concept.title, concept.type, via, repeat, children)

    return tuple(walk(root, None, frozenset()) for root in roots(bundle, links))


# --- expansion ----------------------------------------------------------------------------------------------


class ExpandError(ValueError):
    pass


def _block(lines: list[str], start: int) -> range:
    """Indices of the line at ``start`` and, when it opens a ``{`` block, everything up to its matching ``}``."""
    if not lines[start].rstrip().endswith("{"):
        return range(start, start + 1)
    depth = 0
    for index in range(start, len(lines)):
        stripped = lines[index].strip()
        depth += stripped.endswith("{") - (stripped == "}")
        if depth == 0:
            return range(start, index + 1)
    return range(start, len(lines))


def _inside(lines: list[str], start: int) -> range:
    """Indices of the lines strictly inside the ``{ ... }`` block opened at ``start``."""
    return _block(lines, start)[1:-1]


def expand(bundle: Bundle, concept_id: str, alias: str) -> str:
    """The parent diagram with ``alias`` drawn as a boundary holding the elements of the view it zooms into.

    Parent relationships to ``alias`` stay (they now point at the boundary) unless the child view refines them:
    when the child draws a relationship between the same outside element and something inside, that replaces it.
    """
    parent = bundle.get(concept_id)
    if parent is None or parent.diagram is None:
        raise ExpandError(f"`{concept_id}` is not a diagram in this bundle")
    element = parent.diagram.element(alias)
    if element is None or not element.link:
        raise ExpandError(f"`{alias}` is not a zoomable element of `{concept_id}`")
    target = resolve_href(concept_id, element.link)
    child = bundle.get(target) if target else None
    if child is None or child.diagram is None or child.diagram.keyword not in C4_KEYWORDS:
        raise ExpandError(f"`{alias}` zooms to `{element.link}`, which is not a C4 diagram")

    child_lines = child.diagram.source.splitlines()
    own = child.diagram.element(alias)
    if own is not None and own.kind is ElementKind.BOUNDARY:
        copied = list(_inside(child_lines, own.line))
        inside = {e.alias for e in child.diagram.elements if _within(child.diagram.elements, e.parent, alias)}
    else:
        top = [e for e in child.diagram.elements if e.parent is None and e.alias not in parent.diagram.aliases]
        copied = [index for e in top for index in _block(child_lines, e.line)]
        inside = {e.alias for e in child.diagram.elements if e.alias not in parent.diagram.aliases}
    body = [child_lines[index] for index in copied]

    refined = {r.source for r in child.diagram.relationships if r.target in inside} | {
        r.target for r in child.diagram.relationships if r.source in inside
    }
    known = parent.diagram.aliases | inside
    lines = parent.diagram.source.splitlines()
    rel_at = {r.line: r for r in parent.diagram.relationships}
    out: list[str] = []
    for index, line in enumerate(lines):
        if index == element.line:
            indent = line[: len(line) - len(line.lstrip())]
            out.append(f'{indent}System_Boundary({alias}, "{element.label}") {{')
            out += [f"{indent}  {_rebase(ln.strip(), child.id)}" for ln in body]
            out.append(f"{indent}}}")
            continue
        rel = rel_at.get(index)
        if rel is not None and alias in (rel.source, rel.target):
            other = rel.target if rel.source == alias else rel.source
            if other in refined:
                continue
        out.append(line)
    # A relationship the child wrote inside a boundary already came across with the body; emit it once.
    in_body = set(copied)
    for rel in child.diagram.relationships:
        if rel.line in in_body:
            continue
        if rel.source in known and rel.target in known and (rel.source in inside or rel.target in inside):
            out.append(f"  {_rebase(child_lines[rel.line].strip(), child.id)}")
    return "\n".join(out) + "\n"


_LINK_ARG = re.compile(r'\$link="(?P<href>[^"]*)"')


def _rebase(line: str, from_id: str) -> str:
    """Rewrite ``$link`` targets written relative to ``from_id`` as bundle-absolute OKF links."""

    def absolute(match: re.Match[str]) -> str:
        target = resolve_href(from_id, match.group("href"))
        return f'$link="/{target}.md"' if target is not None else match.group(0)

    return _LINK_ARG.sub(absolute, line)


def _within(elements: tuple[Element, ...], parent: str | None, boundary: str) -> bool:
    by_alias = {e.alias: e for e in elements}
    while parent is not None:
        if parent == boundary:
            return True
        parent = by_alias[parent].parent if parent in by_alias else None
    return False

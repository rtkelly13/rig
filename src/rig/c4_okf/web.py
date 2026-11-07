"""HTTP routes for the C4 viewer, mounted into the rig web control plane by one call from its request handler.

``/c4/`` serves the viewer page and its static files from this package; ``/api/c4/*`` serves the bundle as JSON.
The bundle is re-read on every request: it is a handful of small files, and edits show up on the next click.
Set ``RIG_C4_BUNDLE`` to point the viewer at another bundle.
"""

from __future__ import annotations

import mimetypes
import os
import urllib.parse
from dataclasses import dataclass
from io import BufferedIOBase
from pathlib import Path
from typing import Protocol

from markdown_it import MarkdownIt
from markdown_it.renderer import RendererHTML
from pydantic import StrictStr, TypeAdapter

from rig.c4_okf.bundle import Actor, Bundle, Concept, load_bundle
from rig.c4_okf.cli import DEFAULT_BUNDLE
from rig.c4_okf.graph import (
    EdgeKind,
    ExpandError,
    Finding,
    Links,
    Severity,
    TreeNode,
    check_bundle,
    collect_links,
    expand,
    zoom_tree,
)
from rig.typed_json import decode

STATIC = Path(__file__).parent / "static"


class Responder(Protocol):
    """The slice of ``BaseHTTPRequestHandler`` the routes write through, so any server can mount them."""

    @property
    def wfile(self) -> BufferedIOBase: ...

    def send_response(self, code: int, message: str | None = None) -> None: ...

    def send_header(self, keyword: str, value: str) -> None: ...

    def end_headers(self) -> None: ...


_MARKDOWN = MarkdownIt("commonmark", {"html": False}).enable("table")
_HTML = RendererHTML()


def render_markdown(text: str) -> str:
    """Notes to HTML. Raw HTML in a note is escaped, not passed through: notes may be agent-written."""
    return _HTML.render(_MARKDOWN.parse(text), _MARKDOWN.options, {})


@dataclass(frozen=True, slots=True)
class ConceptSummary:
    id: str
    title: str
    type: str | None
    level: int | None
    description: str | None


@dataclass(frozen=True, slots=True)
class FindingCounts:
    error: int
    warning: int
    info: int


@dataclass(frozen=True, slots=True)
class BundleView:
    root: str
    name: str
    bundles: tuple[str, ...]
    concepts: tuple[ConceptSummary, ...]
    tree: tuple[TreeNode, ...]
    findings: tuple[Finding, ...]
    counts: FindingCounts


@dataclass(frozen=True, slots=True)
class ZoomView:
    alias: str
    target: str
    title: str
    kind: EdgeKind
    expandable: bool


@dataclass(frozen=True, slots=True)
class InboundView:
    source: str
    title: str
    alias: str | None
    kind: EdgeKind


@dataclass(frozen=True, slots=True)
class ConceptView:
    id: str
    title: str
    type: str | None
    level: int | None
    description: str | None
    resource: str | None
    tags: tuple[str, ...]
    generated: Actor | None
    verified: Actor | None
    diagram: str | None
    keyword: str | None
    notes_html: str
    element_notes: dict[str, str]
    zooms: tuple[ZoomView, ...]
    inbound: tuple[InboundView, ...]
    findings: tuple[Finding, ...]


@dataclass(frozen=True, slots=True)
class ExpandView:
    id: str
    alias: str
    diagram: str


@dataclass(frozen=True, slots=True)
class ErrorView:
    error: str


@dataclass(frozen=True, slots=True)
class _Query:
    id: tuple[StrictStr, ...] = ()
    alias: tuple[StrictStr, ...] = ()
    bundle: tuple[StrictStr, ...] = ()


_BUNDLE = TypeAdapter(BundleView)
_CONCEPT = TypeAdapter(ConceptView)
_EXPAND = TypeAdapter(ExpandView)
_ERROR = TypeAdapter(ErrorView)


def bundles() -> dict[str, Path]:
    """Every bundle the viewer can switch between: ``RIG_C4_BUNDLE`` first if set, then the default bundle, then
    the other samples beside it. Only these names are servable, so a query can never point the server elsewhere.

    The samples live in the source checkout's ``experiments/``, which a wheel does not ship: when they are absent
    only ``RIG_C4_BUNDLE`` is offered."""
    found: dict[str, Path] = {}
    override = os.environ.get("RIG_C4_BUNDLE")
    if override:
        path = Path(override).expanduser()
        found[path.name] = path
    if DEFAULT_BUNDLE.is_dir():
        found.setdefault(DEFAULT_BUNDLE.name, DEFAULT_BUNDLE)
    if DEFAULT_BUNDLE.parent.is_dir():
        for path in sorted(DEFAULT_BUNDLE.parent.iterdir()):
            if path.is_dir() and any(path.rglob("*.md")):
                found.setdefault(path.name, path)
    return found


def bundle_root(name: str | None = None) -> Path | None:
    known = bundles()
    if name:
        return known.get(name)
    return next(iter(known.values()), None)


def bundle_view(bundle: Bundle) -> BundleView:
    links = collect_links(bundle)
    findings = check_bundle(bundle)
    return BundleView(
        root=bundle.root.name,
        name=bundle.root.name,
        bundles=tuple(bundles()),
        concepts=tuple(ConceptSummary(c.id, c.title, c.type, c.level, c.description) for c in bundle.concepts.values()),
        tree=zoom_tree(bundle, links),
        findings=findings,
        counts=FindingCounts(*(sum(1 for f in findings if f.severity is s) for s in Severity)),
    )


def _expandable(bundle: Bundle, target: str) -> bool:
    child = bundle.get(target)
    return child is not None and child.diagram is not None and child.diagram.keyword.startswith("C4")


def concept_view(bundle: Bundle, concept: Concept, links: Links) -> ConceptView:
    zooms = tuple(
        ZoomView(e.alias or "", e.target, bundle.concepts[e.target].title, e.kind, _expandable(bundle, e.target))
        for e in links.edges
        if e.source == concept.id and e.kind is not EdgeKind.MENTION
    )
    inbound = tuple(
        InboundView(e.source, bundle.concepts[e.source].title, e.alias, e.kind) for e in links.into(concept.id)
    )
    return ConceptView(
        id=concept.id,
        title=concept.title,
        type=concept.type,
        level=concept.level,
        description=concept.description,
        resource=concept.resource,
        tags=concept.tags,
        generated=concept.generated,
        verified=concept.verified,
        diagram=concept.diagram.source if concept.diagram else None,
        keyword=concept.diagram.keyword if concept.diagram else None,
        notes_html=render_markdown(concept.notes),
        element_notes={alias: render_markdown(text) for alias, text in concept.element_notes().items()},
        zooms=zooms,
        inbound=inbound,
        findings=tuple(f for f in check_bundle(bundle) if f.concept == concept.id),
    )


def _send(handler: Responder, status: int, content_type: str, body: bytes) -> None:
    handler.send_response(status)
    handler.send_header("Content-Type", content_type)
    handler.send_header("Content-Length", str(len(body)))
    handler.send_header("Cache-Control", "no-cache")
    handler.end_headers()
    _ = handler.wfile.write(body)


def _json(handler: Responder, body: bytes, status: int = 200) -> bool:
    _send(handler, status, "application/json; charset=utf-8", body)
    return True


def _error(handler: Responder, message: str, status: int) -> bool:
    return _json(handler, _ERROR.dump_json(ErrorView(message)), status)


def _static(handler: Responder, name: str) -> bool:
    target = (STATIC / name).resolve()
    if not target.is_relative_to(STATIC.resolve()) or not target.is_file():
        return _error(handler, f"not found: {name}", 404)
    content_type = mimetypes.guess_type(target.name)[0] or "application/octet-stream"
    if content_type.startswith("text/") or target.suffix == ".js":
        content_type = f"{'text/javascript' if target.suffix == '.js' else content_type}; charset=utf-8"
    _send(handler, 200, content_type, target.read_bytes())
    return True


def serve_c4(handler: Responder, url: urllib.parse.ParseResult) -> bool:
    """Answer the request if it belongs to the C4 viewer; ``False`` leaves it to the caller."""
    path = url.path
    if path in ("/c4", "/c4/"):
        return _static(handler, "index.html")
    if path.startswith("/c4/static/"):
        return _static(handler, urllib.parse.unquote(path.removeprefix("/c4/static/")))
    if not path.startswith("/api/c4/"):
        return False

    query = decode(_Query, urllib.parse.parse_qs(url.query))
    root = bundle_root(query.bundle[0] if query.bundle else None)
    if root is None:
        missing = f"no bundle `{query.bundle[0]}`" if query.bundle else "no C4 bundle: set RIG_C4_BUNDLE"
        return _error(handler, missing, 404)
    bundle = load_bundle(root)
    concept_id = query.id[0] if query.id else ""
    if path == "/api/c4/bundle":
        return _json(handler, _BUNDLE.dump_json(bundle_view(bundle)))
    if path == "/api/c4/concept":
        concept = bundle.get(concept_id)
        if concept is None:
            return _error(handler, f"no concept `{concept_id}`", 404)
        return _json(handler, _CONCEPT.dump_json(concept_view(bundle, concept, collect_links(bundle))))
    if path == "/api/c4/expand":
        alias = query.alias[0] if query.alias else ""
        try:
            return _json(handler, _EXPAND.dump_json(ExpandView(concept_id, alias, expand(bundle, concept_id, alias))))
        except ExpandError as error:
            return _error(handler, str(error), 400)
    return _error(handler, f"unknown endpoint {path}", 404)

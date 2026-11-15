"""The C4-as-OKF experiment: reading diagrams, resolving zoom links, checks, composition and the web routes."""

from __future__ import annotations

import io
import json
import urllib.parse
from pathlib import Path

import pytest
from typer.testing import CliRunner

from rig.c4_okf.bundle import load_bundle, resolve_href
from rig.c4_okf.cli import DEFAULT_BUNDLE, app
from rig.c4_okf.graph import EdgeKind, Severity, TreeNode, build_model, check_bundle, collect_links, expand, zoom_tree
from rig.c4_okf.mermaid_c4 import ElementKind, parse_diagram
from rig.c4_okf.web import render_markdown, serve_c4

pytestmark = pytest.mark.ci

CONTEXT = """---
type: C4 Context
title: Shop
generated: { by: test, at: 2026-11-04 }
---

```mermaid
C4Context
Person(user, "User")
System(shop, "Shop", "Sells things", $link="shop/containers.md")
System_Ext(pay, "Payments")
Rel(user, shop, "Buys from")
Rel(shop, pay, "Charges")
```

## shop

The thing we build.
"""

CONTAINERS = """---
type: C4 Container
title: Shop containers
---

```mermaid
C4Container
Person(user, "User")
System_Ext(pay, "Payments")
System_Boundary(shop, "Shop") {
  Container(api, "API", "Python", $link="api.md")
  ContainerDb(db, "DB", "Postgres")
}
Rel(user, api, "Calls")
Rel(api, db, "Reads")
Rel(api, pay, "Charges")
```

See [the context](/context.md) and [the API](api.md).
"""

API = """---
type: C4 Component
title: API components
verified: { by: ryan, at: 2026-11-04 }
---

```mermaid
C4Component
Person(user, "User")
ContainerDb(db, "DB", "Postgres")
System_Ext(pay, "Payments")
Container_Boundary(api, "API") {
  Component(routes, "Routes", "FastAPI")
}
```
"""


def _write(root: Path, files: dict[str, str]) -> Path:
    for name, text in files.items():
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        _ = path.write_text(text, encoding="utf-8")
    return root


@pytest.fixture
def shop(tmp_path: Path) -> Path:
    return _write(
        tmp_path,
        {"context.md": CONTEXT, "shop/containers.md": CONTAINERS, "shop/api.md": API, "index.md": "# not a concept"},
    )


def test_parse_reads_elements_boundaries_links_and_relationships() -> None:
    diagram = parse_diagram(CONTAINERS.split("```mermaid\n")[1].split("```", maxsplit=1)[0])
    assert diagram.keyword == "C4Container"
    api = diagram.element("api")
    assert api is not None
    assert (api.kind, api.parent, api.link) == (ElementKind.CONTAINER, "shop", "api.md")
    pay = diagram.element("pay")
    assert pay is not None
    assert pay.external
    assert {(r.source, r.target) for r in diagram.relationships} == {("user", "api"), ("api", "db"), ("api", "pay")}


def test_parse_reads_click_links_from_class_diagrams() -> None:
    diagram = parse_diagram('classDiagram\nclass Server\nclick Server href "components.md"\n')
    server = diagram.element("Server")
    assert server is not None
    assert server.link == "components.md"


@pytest.mark.parametrize(
    ("from_id", "href", "expected"),
    [
        ("shop/containers", "api.md", "shop/api"),
        ("shop/containers", "/context.md", "context"),
        ("shop/containers", "../context.md#top", "context"),
        ("context", "../escape.md", None),
        ("context", "https://example.com/x.md", None),
        ("context", "#anchor", None),
    ],
)
def test_resolve_href(from_id: str, href: str, expected: str | None) -> None:
    assert resolve_href(from_id, href) == expected


def test_load_bundle_skips_reserved_files_and_reads_provenance(shop: Path) -> None:
    bundle = load_bundle(shop)
    assert set(bundle.concepts) == {"context", "shop/containers", "shop/api"}
    context = bundle.concepts["context"]
    assert context.level == 1
    assert context.generated is not None
    assert context.generated.at == "2026-11-04"
    assert "shop" in context.element_notes()


def test_links_classify_zoom_and_mention(shop: Path) -> None:
    links = collect_links(load_bundle(shop))
    kinds = {(e.source, e.target, e.kind) for e in links.edges}
    assert ("context", "shop/containers", EdgeKind.ZOOM) in kinds
    assert ("shop/containers", "shop/api", EdgeKind.ZOOM) in kinds
    assert ("shop/containers", "context", EdgeKind.MENTION) in kinds
    assert not links.broken


def test_upward_link_is_a_zoom_out_not_a_zoom(shop: Path) -> None:
    api = API.replace(
        'Component(routes, "Routes", "FastAPI")', 'Component(routes, "Routes", "x", $link="containers.md")'
    )
    _ = (shop / "shop" / "api.md").write_text(api, encoding="utf-8")
    bundle = load_bundle(shop)
    edge = next(e for e in collect_links(bundle).edges if e.source == "shop/api")
    assert edge.kind is EdgeKind.ZOOM_OUT
    assert not any(f.code in {"zoom-level", "fan-in"} for f in check_bundle(bundle))


def test_clean_bundle_has_no_errors_or_warnings(shop: Path) -> None:
    findings = check_bundle(load_bundle(shop))
    assert [f for f in findings if f.severity is not Severity.INFO] == []
    assert {f.concept for f in findings if f.code == "unverified"} == {"context", "shop/containers"}


def test_checks_catch_broken_zoom_orphans_skips_and_unrefined_relationships(shop: Path) -> None:
    _ = (shop / "context.md").write_text(CONTEXT.replace("shop/containers.md", "shop/missing.md"), encoding="utf-8")
    _ = (shop / "lonely.md").write_text("---\ntitle: no type\n---\nhi\n", encoding="utf-8")
    codes = {(f.code, f.severity, f.concept) for f in check_bundle(load_bundle(shop))}
    assert ("zoom-broken", Severity.ERROR, "context") in codes
    assert ("orphan", Severity.WARNING, "shop/containers") in codes
    assert ("okf-type", Severity.ERROR, "lonely") in codes


def test_refinement_flags_a_parent_relationship_the_child_drops(shop: Path) -> None:
    _ = (shop / "shop" / "containers.md").write_text(
        CONTAINERS.replace('System_Ext(pay, "Payments")\n', ""), encoding="utf-8"
    )
    findings = check_bundle(load_bundle(shop))
    assert any(f.code == "rel-unrefined" and f.concept == "shop/containers" for f in findings)


def test_model_merges_one_alias_across_views_and_flags_label_conflicts(shop: Path) -> None:
    model = build_model(load_bundle(shop))
    user = next(e for e in model.elements if e.alias == "user")
    assert set(user.views) == {"context", "shop/containers", "shop/api"}
    _ = (shop / "shop" / "containers.md").write_text(
        CONTAINERS.replace('Person(user, "User")', 'Person(user, "Customer")'), encoding="utf-8"
    )
    assert any(f.code == "alias-conflict" for f in check_bundle(load_bundle(shop)))


def test_zoom_tree_walks_the_dag_and_marks_repeats(shop: Path) -> None:
    # A second container view that also zooms into the API: the API is reached twice, shown once.
    second = CONTAINERS.replace("title: Shop containers", "title: Back office").replace("Rel(api, pay", "Rel(api, db")
    _ = (shop / "shop" / "back-office.md").write_text(second, encoding="utf-8")
    context = CONTEXT.replace('System_Ext(pay, "Payments")', 'System(ops, "Ops", $link="shop/back-office.md")')
    _ = (shop / "context.md").write_text(context, encoding="utf-8")

    def flat(nodes: tuple[TreeNode, ...]) -> list[TreeNode]:
        return [n for node in nodes for n in (node, *flat(node.children))]

    api = [n for n in flat(zoom_tree(load_bundle(shop))) if n.id == "shop/api"]
    assert [n.repeat for n in api] == [False, True]


def test_expand_inlines_the_child_and_replaces_refined_relationships(shop: Path) -> None:
    composed = expand(load_bundle(shop), "context", "shop")
    assert 'System_Boundary(shop, "Shop") {' in composed
    assert '$link="/shop/api.md"' in composed  # child-relative link rebased to bundle-absolute
    assert 'Rel(user, api, "Calls")' in composed
    assert 'Rel(user, shop, "Buys from")' not in composed
    assert parse_diagram(composed).element("api") is not None


def test_expand_draws_a_relationship_inside_the_boundary_once(shop: Path) -> None:
    # The child writes Rel(api, db) inside its boundary: it comes across with the body and must not be re-appended.
    inner = CONTAINERS.replace(
        '  ContainerDb(db, "DB", "Postgres")\n}', '  ContainerDb(db, "DB", "Postgres")\n  Rel(api, db, "Reads")\n}'
    )
    _ = (shop / "shop/containers.md").write_text(
        inner.replace('Rel(api, db, "Reads")\nRel(api, pay', "Rel(api, pay"), encoding="utf-8"
    )
    composed = expand(load_bundle(shop), "context", "shop")
    assert composed.count('Rel(api, db, "Reads")') == 1
    assert composed.count('Rel(api, pay, "Charges")') == 1


def test_notes_escape_raw_html() -> None:
    assert "<script>" not in render_markdown("hi <script>alert(1)</script>")


def test_sample_bundle_has_no_errors() -> None:
    findings = check_bundle(load_bundle(DEFAULT_BUNDLE))
    assert not [f for f in findings if f.severity is Severity.ERROR]


class _FakeHandler:
    """Just enough of a request handler for the routes to answer into a buffer."""

    def __init__(self) -> None:
        self.status: int = 0
        self.headers: dict[str, str] = {}
        self.wfile: io.BytesIO = io.BytesIO()

    def send_response(self, code: int, message: str | None = None) -> None:
        _ = message
        self.status = code

    def send_header(self, keyword: str, value: str) -> None:
        self.headers[keyword] = value

    def end_headers(self) -> None:
        pass


@pytest.mark.parametrize(
    ("url", "status"),
    [
        ("/c4/", 200),
        ("/c4/static/c4.js", 200),
        ("/c4/static/../web.py", 404),
        ("/api/c4/bundle", 200),
        ("/api/c4/bundle?bundle=rig", 200),
        ("/api/c4/bundle?bundle=..%2Fetc", 404),
        ("/api/c4/concept?id=shop/containers", 200),
        ("/api/c4/concept?id=nope", 404),
        ("/api/c4/expand?id=context&alias=shop", 200),
        ("/api/c4/expand?id=context&alias=pay", 400),
    ],
)
def test_routes(shop: Path, monkeypatch: pytest.MonkeyPatch, url: str, status: int) -> None:
    monkeypatch.setenv("RIG_C4_BUNDLE", str(shop))
    handler = _FakeHandler()
    assert serve_c4(handler, urllib.parse.urlparse(url))
    assert handler.status == status
    assert handler.headers["Content-Length"] == str(len(handler.wfile.getvalue()))


def test_routes_serve_the_override_without_the_checkout_samples(
    shop: Path, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # A wheel install has no experiments/ directory: only RIG_C4_BUNDLE is servable, and nothing raises.
    monkeypatch.setattr("rig.c4_okf.web.DEFAULT_BUNDLE", tmp_path / "absent" / "experiments" / "rig")
    monkeypatch.setenv("RIG_C4_BUNDLE", str(shop))
    handler = _FakeHandler()
    assert serve_c4(handler, urllib.parse.urlparse("/api/c4/bundle"))
    assert handler.status == 200
    assert f'"bundles":["{shop.name}"]' in handler.wfile.getvalue().decode()

    monkeypatch.delenv("RIG_C4_BUNDLE")
    handler = _FakeHandler()
    assert serve_c4(handler, urllib.parse.urlparse("/api/c4/bundle"))
    assert handler.status == 404


def test_routes_leave_other_paths_alone() -> None:
    handler = _FakeHandler()
    assert not serve_c4(handler, urllib.parse.urlparse("/api/estate"))
    assert handler.status == 0


_CLI = CliRunner()


def test_cli_check_prints_a_table_and_fails_only_on_errors_unless_strict(shop: Path) -> None:
    table = _CLI.invoke(app, ["check", str(shop)])
    assert table.exit_code == 0, table.output
    assert "3 concepts" in table.output
    assert "unverified" in table.output
    quiet = _CLI.invoke(app, ["check", str(shop), "--quiet", "--json"])
    assert json.loads(quiet.output) == []
    _ = (shop / "context.md").write_text(CONTEXT.replace('System_Ext(pay, "Payments")\n', ""), encoding="utf-8")
    _ = (shop / "shop" / "orphan.md").write_text(API.replace("API components", "Orphan"), encoding="utf-8")
    assert _CLI.invoke(app, ["check", str(shop)]).exit_code == 0
    strict = _CLI.invoke(app, ["check", str(shop), "--strict", "--json"])
    assert strict.exit_code == 1
    assert "orphan" in {f["code"] for f in json.loads(strict.output)}


def test_cli_check_exits_one_on_an_error(shop: Path) -> None:
    _ = (shop / "lonely.md").write_text("---\ntitle: no type\n---\nhi\n", encoding="utf-8")
    result = _CLI.invoke(app, ["check", str(shop)])
    assert result.exit_code == 1
    assert "okf-type" in result.output


def test_cli_tree_prints_the_zoom_hierarchy_as_text_and_json(shop: Path) -> None:
    text = _CLI.invoke(app, ["tree", str(shop)])
    assert text.exit_code == 0, text.output
    assert "API components" in text.output
    nodes = json.loads(_CLI.invoke(app, ["tree", str(shop), "--json"]).output)
    assert [n["id"] for n in nodes] == ["context"]


def test_cli_model_prints_every_alias(shop: Path) -> None:
    result = _CLI.invoke(app, ["model", str(shop)])
    assert result.exit_code == 0, result.output
    assert "user" in {e["alias"] for e in json.loads(result.output)["elements"]}


def test_cli_expand_prints_mermaid_or_fails_on_an_unknown_alias(shop: Path) -> None:
    composed = _CLI.invoke(app, ["expand", "context", "shop", "--root", str(shop)])
    assert composed.exit_code == 0, composed.output
    assert 'System_Boundary(shop, "Shop") {' in composed.output
    missing = _CLI.invoke(app, ["expand", "context", "nobody", "--root", str(shop)])
    assert missing.exit_code == 1

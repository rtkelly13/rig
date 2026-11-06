"""``rig c4`` — check, walk and compose a C4 model kept as an OKF bundle."""

from __future__ import annotations

from pathlib import Path
from typing import Annotated

import typer
from pydantic import TypeAdapter
from rich.console import Console
from rich.tree import Tree

from rig.c4_okf.bundle import load_bundle
from rig.c4_okf.graph import (
    ExpandError,
    Finding,
    Model,
    Severity,
    TreeNode,
    build_model,
    check_bundle,
    expand,
    zoom_tree,
)

console = Console()

#: The sample bundle that documents rig itself.
DEFAULT_BUNDLE = Path(__file__).resolve().parents[3] / "experiments" / "c4-okf" / "rig"

app = typer.Typer(
    name="c4",
    help="C4 diagrams as an Open Knowledge Format bundle: zoom links, composition and checks. Experimental.",
    no_args_is_help=True,
    add_completion=False,
)

_FINDINGS: TypeAdapter[tuple[Finding, ...]] = TypeAdapter(tuple[Finding, ...])
_MODEL: TypeAdapter[Model] = TypeAdapter(Model)
_TREE: TypeAdapter[tuple[TreeNode, ...]] = TypeAdapter(tuple[TreeNode, ...])

BundleArg = Annotated[
    Path,
    typer.Argument(help="Bundle root: a directory of OKF markdown concepts.", exists=True, file_okay=False),
]
JsonOpt = Annotated[bool, typer.Option("--json", help="Print JSON instead of a table.")]

_STYLE = {Severity.ERROR: "bold red", Severity.WARNING: "yellow", Severity.INFO: "dim"}


@app.command("check")
def check_cmd(
    root: BundleArg = DEFAULT_BUNDLE,
    as_json: JsonOpt = False,
    strict: Annotated[bool, typer.Option("--strict", help="Fail on warnings as well as errors.")] = False,
    quiet: Annotated[bool, typer.Option("--quiet", "-q", help="Hide info findings.")] = False,
) -> None:
    """Check OKF conformance, zoom links, level descent, orphans, fan-in and cross-view consistency.

    Codes: okf-frontmatter, okf-type (error) · zoom-broken (error) · link-broken, zoom-level, diagram-missing,
    diagram-kind, orphan, rel-unrefined, alias-conflict (warning) · fan-in, unverified, orphan note (info).
    Exits 1 on any error, or on any warning with --strict.
    """
    bundle = load_bundle(root)
    findings = check_bundle(bundle)
    shown = tuple(f for f in findings if not (quiet and f.severity is Severity.INFO))
    if as_json:
        typer.echo(_FINDINGS.dump_json(shown, indent=2).decode("utf-8"))
    else:
        for f in shown:
            console.print(f"[{_STYLE[f.severity]}]{f.severity:<7}[/] {f.code:<15} [cyan]{f.concept}[/] {f.message}")
        counts = {s: sum(1 for f in findings if f.severity is s) for s in Severity}
        console.print(
            f"\n{len(bundle.concepts)} concepts · {counts[Severity.ERROR]} errors · "
            f"{counts[Severity.WARNING]} warnings · {counts[Severity.INFO]} info"
        )
    failing = {Severity.ERROR, Severity.WARNING} if strict else {Severity.ERROR}
    if any(f.severity in failing for f in findings):
        raise typer.Exit(1)


def _add(branch: Tree, node: TreeNode) -> None:
    via = f"[dim]{node.via} →[/] " if node.via else ""
    repeat = " [dim](shown above)[/]" if node.repeat else ""
    child = branch.add(f"{via}[bold]{node.title}[/] [dim]{node.type or '?'} · {node.id}[/]{repeat}")
    for grandchild in node.children:
        _add(child, grandchild)


@app.command("tree")
def tree_cmd(root: BundleArg = DEFAULT_BUNDLE, as_json: JsonOpt = False) -> None:
    """Print the zoom hierarchy from each root view. A concept reached twice is shown once, then marked."""
    nodes = zoom_tree(load_bundle(root))
    if as_json:
        typer.echo(_TREE.dump_json(nodes, indent=2).decode("utf-8"))
        return
    tree = Tree(f"[bold]{root}[/]")
    for node in nodes:
        _add(tree, node)
    console.print(tree)


@app.command("model")
def model_cmd(root: BundleArg = DEFAULT_BUNDLE) -> None:
    """Print the merged model as JSON: every alias, its labels, the views it appears in, and what it zooms to."""
    typer.echo(_MODEL.dump_json(build_model(load_bundle(root)), indent=2).decode("utf-8"))


@app.command("expand")
def expand_cmd(
    concept: Annotated[str, typer.Argument(help="Concept id of the parent view, e.g. system-context.")],
    alias: Annotated[str, typer.Argument(help="The element to open up in place.")],
    root: Annotated[Path, typer.Option("--root", help="Bundle root.", exists=True, file_okay=False)] = DEFAULT_BUNDLE,
) -> None:
    """Print Mermaid for a parent view with one element replaced by the inside of the view it zooms into."""
    try:
        typer.echo(expand(load_bundle(root), concept, alias), nl=False)
    except ExpandError as error:
        console.print(f"[red]{error}[/]")
        raise typer.Exit(1) from error


derive_app = typer.Typer(name="derive", help="Derive C4 views from code into a bundle.", no_args_is_help=True)
app.add_typer(derive_app)


@derive_app.command("dotnet")
def derive_dotnet_cmd(
    repo: Annotated[Path, typer.Argument(help="A .NET repository; projects are read from its src/.", exists=True)],
    out: Annotated[Path, typer.Argument(help="Bundle root to write containers.md, components/ and code/ into.")],
    parent: Annotated[
        str | None,
        typer.Option("--parent", help="CONCEPT:ALIAS of the hand-written view the containers refine."),
    ] = None,
    callgraph: Annotated[
        Path | None,
        typer.Option("--callgraph", help="Directory of <Project>.callgraph.txt edge files, scored as a 2nd pathway."),
    ] = None,
    guide: Annotated[
        list[str] | None,
        typer.Option("--guide", help="A repo markdown page to copy into guide/, code names linked. Repeatable."),
    ] = None,
    changes: Annotated[
        int, typer.Option("--changes", help="Walk through the last N merged changes to src/ (needs --callgraph).")
    ] = 0,
    check: Annotated[bool, typer.Option("--check", help="Write nothing; exit 1 if any derived file is stale.")] = False,
) -> None:
    """Derive containers (projects), components (namespaces) and code (types) from a .NET repository.

    Deterministic: the same commit derives the same bytes, so --check is a staleness gate for CI.
    """
    from rig.c4_okf.derive import Parent, derive, stale, write

    link = None
    if parent:
        concept, _, alias = parent.partition(":")
        link = Parent(concept, alias)
    derived = derive(repo, out, parent=link, callgraph=callgraph, guides=tuple(guide or ()), changes=changes)
    changed = stale(out, derived.files) if check else write(out, derived.files)
    for name in changed:
        verb = "stale" if check else "wrote" if name in derived.files else "removed"
        console.print(f"{verb} {name}{' (orphaned)' if check and name not in derived.files else ''}")
    for alias, s in sorted(derived.pathways.items()):
        console.print(
            f"[dim]{alias}: {s.both} relationships on both pathways · {s.only_refs} references only"
            f" · {s.only_calls} calls only[/]"
        )
    console.print(f"{len(derived.files)} derived concepts · {len(changed)} {'stale' if check else 'changed'}")
    if check and changed:
        raise typer.Exit(1)

"""Derive C4 levels 2-4 of a .NET repository as OKF concepts, from its code, deterministically.

Levels 1 (context) stays hand-written: who uses a system and what it talks to is not in its code. The derived
views hang beneath it. The context's element ``$link``\\s to ``containers.md``, and ``--parent`` copies that
element's neighbours into the container view so the refinement check holds by construction.

Every derived concept says so: ``generated.by`` names this deriver, ``generated.at`` is the commit date (not
today, so a re-run over the same commit is byte-identical), ``resource`` is the commit on GitHub, and the tag
``derived`` marks it as overwritten on every run. ``--check`` re-derives and fails when a file on disk differs,
which is the staleness gate a hand-drawn diagram never has.
"""

from __future__ import annotations

import re
from collections import Counter
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import final

from rig.c4_okf.bundle import load_bundle
from rig.c4_okf.callgraph import CallGraph, MemberKey
from rig.c4_okf.dotnet_scan import (
    Component,
    Edge,
    Project,
    SourceFile,
    TypeInfo,
    call_edges,
    components,
    discover,
    reference_edges,
    sources,
    type_index,
)
from rig.proc import git_out

PRODUCER = "rig-c4-derive/0.1"
_COMPILER = "csc"


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


@dataclass(frozen=True, slots=True)
class Parent:
    """The hand-written view the derived container view refines: its concept id and the zoomed element."""

    concept: str
    alias: str


def origin(repo: Path) -> Origin:
    remote = git_out("remote", "get-url", "origin", cwd=repo)
    match = re.match(r"^(?:git@github\.com:|https://github\.com/)(?P<slug>[^/]+/[^/]+?)(?:\.git)?$", remote)
    return Origin(
        commit=git_out("rev-parse", "--short=12", "HEAD", cwd=repo) or "unknown",
        date=git_out("log", "-1", "--format=%cs", cwd=repo) or "unknown",
        web=f"https://github.com/{match.group('slug')}" if match else None,
    )


def slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "_", text.lower()).strip("_") or "root"


def _q(text: str) -> str:
    return text.replace('"', "'")


def _frontmatter(kind: str, title: str, description: str, where: Origin, resource: str | None = None) -> str:
    lines = [
        "---",
        f"type: {kind}",
        f"title: {title}",
        f"description: {description}",
        f"resource: {resource or where.tree() or 'unknown'}",
        f"generated: {{ by: {PRODUCER}, at: {where.date} }}",
        "tags: [derived]",
        "---",
        "",
    ]
    return "\n".join(lines)


def _diagram(lines: list[str]) -> str:
    return "```mermaid\n" + "\n".join(lines) + "\n```\n"


@dataclass(frozen=True, slots=True)
class _ProjectScan:
    project: Project
    files: tuple[SourceFile, ...]
    parts: tuple[Component, ...]


@dataclass(frozen=True, slots=True)
class PathwayScore:
    """How many relationships each pathway found: on both, on type references only, on the call graph only."""

    both: int
    only_refs: int
    only_calls: int


@dataclass(frozen=True, slots=True)
class Derived:
    """A derived bundle: file contents by bundle path, plus the pathway scores for the summary."""

    files: dict[str, str]
    pathways: dict[str, PathwayScore]


@dataclass(frozen=True, slots=True)
class _CodeKey:
    """A code view is one namespace over one set of files."""

    namespace: str
    paths: tuple[Path, ...]


@dataclass(frozen=True, slots=True)
class _ComponentsView:
    text: str
    score: PathwayScore


@dataclass(frozen=True, slots=True)
class _Scored:
    """Relationships split by which pathways found them."""

    both: list[Edge]
    only_refs: list[Edge]
    only_calls: list[Edge]


@dataclass(frozen=True, slots=True)
class _Neighbours:
    """Parent-view lines touching the zoomed alias, and that alias's label in the parent."""

    lines: list[str]
    label: str | None


@final
class _Deriver:
    def __init__(self, repo: Path, where: Origin, callgraph: Path | None) -> None:
        self.repo = repo
        self.where = where
        self.callgraph = callgraph
        self.scans = {p.path: _ProjectScan(p, f, components(p, f)) for p in discover(repo) for f in (sources(p),)}
        self.common = _common_namespace([s.project.root_namespace for s in self.scans.values()])
        self.alias = {p: slug(self.short(s.project.name)) for p, s in self.scans.items()}
        self.code_ids = self._code_ids()
        self.graph = CallGraph.load(callgraph, (s.project.name for s in self.scans.values())) if callgraph else None
        # The code view each fully qualified type is drawn in, so a call that leaves a namespace can link onward.
        self.home = {_qualified(c.namespace, t): c for s in self.scans.values() for c in s.parts for t in c.types}

    def short(self, name: str) -> str:
        """A name without the prefix every project shares; the shared root itself keeps its last segment."""
        prefix = self.common + "."
        if name.startswith(prefix):
            return name.removeprefix(prefix)
        return name.rsplit(".", 1)[-1] if name == self.common else name

    def _code_ids(self) -> dict[_CodeKey, str]:
        """One code view per distinct (namespace, files). Projects that compile the same files in share it."""
        seen: dict[_CodeKey, str] = {}
        taken: Counter[str] = Counter()
        for path, scan in self.scans.items():
            for part in scan.parts:
                key = _CodeKey(part.namespace, tuple(f.path for f in part.files))
                if key not in seen:
                    base = slug(self.short(part.namespace))
                    taken[base] += 1
                    seen[key] = f"code/{base}" if taken[base] == 1 else f"code/{base}--{self.alias[path]}"
        return seen

    def code_id(self, part: Component) -> str:
        return self.code_ids[_CodeKey(part.namespace, tuple(f.path for f in part.files))]

    def part_alias(self, part: Component) -> str:
        """One alias per code view: a namespace two projects compile in is one element in both views."""
        return "ns_" + slug(self.code_id(part).removeprefix("code/"))

    def label(self, part: Component) -> str:
        """The namespace without the prefix every project shares, so shared parts read the same everywhere."""
        return self.short(part.namespace) or "global"

    def neighbours(self, scan: _ProjectScan) -> list[_ProjectScan]:
        """Projects this one references, and projects that reference it: everything the container view joins
        it to, so the component view refines every relationship its parent draws."""
        out = {r.target for r in scan.project.refs}
        into = {p for p, s in self.scans.items() if any(r.target == scan.project.path for r in s.project.refs)}
        return [s for p, s in self.scans.items() if p in out | into and p != scan.project.path]

    def container_line(self, scan: _ProjectScan, *, link: bool, external: bool = False) -> str:
        p = scan.project
        tech = f"{'analyzer' if p.is_analyzer else 'library'} · {', '.join(p.frameworks)}"
        macro = "Container_Ext" if external else "Container"
        target = f', $link="/components/{self.alias[p.path]}.md"' if link and scan.parts else ""
        return f'{macro}({self.alias[p.path]}, "{p.name}", "{tech}", "{_n(len(scan.files), "source file")}"{target})'

    def container_rels(self, scan: _ProjectScan) -> list[str]:
        rels: list[str] = []
        me = self.alias[scan.project.path]
        if scan.project.is_analyzer:
            rels.append(f'Rel({_COMPILER}, {me}, "loads into the build", "Roslyn")')
        for ref in scan.project.refs:
            other = self.alias.get(ref.target)
            if other is None:
                continue
            rels.append(
                f'Rel({other}, {me}, "checks at build", "analyzer reference")'
                if ref.analyzer
                else f'Rel({me}, {other}, "references", "ProjectReference")'
            )
        return rels

    def containers(self, parent: Parent | None, root: Path) -> str:
        found = _parent_neighbours(root, parent)
        copied, label = found.lines, found.label
        name = label or self.repo.name
        lines = ["C4Container", f"title {name}: projects"]
        derived_rels = [r for s in self.scans.values() for r in self.container_rels(s)]
        copied_rels = [line for line in copied if line.startswith(("Rel", "BiRel"))]
        lines += [line for line in copied if line not in copied_rels]
        # A parent relationship the derived ones refine (the compiler loading each analyzer) would say it twice.
        refined = {alias for r in derived_rels for alias in _endpoints(r)}
        copied_rels = [r for r in copied_rels if not refined & set(_endpoints(r))]
        declared = any(line.split("(", 1)[-1].startswith(f"{_COMPILER},") for line in copied)
        if not declared and any(s.project.is_analyzer for s in self.scans.values()):
            lines.append(f'System_Ext({_COMPILER}, "C# compiler", "Roslyn, in the consumer\'s build")')
        boundary = parent.alias if parent else f"repo_{slug(name)}"
        lines.append(f'System_Boundary({boundary}, "{name}") {{')
        lines += [f"  {self.container_line(s, link=True)}" for s in self.scans.values()]
        lines.append("}")
        lines += copied_rels
        lines += derived_rels
        notes = [_container_note(self.repo, self.alias[p], s) for p, s in self.scans.items()]
        head = _frontmatter(
            "C4 Container",
            f"{name} — projects",
            "Every project under src/ and what it references, read from the .csproj files.",
            self.where,
        )
        intro = (
            f"Derived by `rig c4 derive dotnet` from commit `{self.where.commit}`; edits here are overwritten. "
            "The people and systems outside the boundary are copied from the parent view.\n"
        )
        return head + _diagram(lines) + "\n" + intro + "\n" + "\n".join(notes)

    def components_view(self, scan: _ProjectScan) -> _ComponentsView:
        project = scan.project
        neighbours = self.neighbours(scan)
        owned = {self.part_alias(c): c.files for c in scan.parts}
        owners: dict[str, tuple[str, ...]] = {k: tuple(t for f in v for t in f.declares) for k, v in owned.items()}
        # A type the project declares itself is its own, even when a neighbour compiles the same file in.
        mine = {t for types in owners.values() for t in types}
        for n in neighbours:
            owners[self.alias[n.project.path]] = tuple(t for f in n.files for t in f.declares if t not in mine)
        refs = reference_edges(owned, type_index(owners))
        calls = self._calls(scan, neighbours)
        me = self.alias[project.path]
        lines = ["C4Component", f"title {project.name}: namespaces"]
        if project.is_analyzer:
            lines.append(f'System_Ext({_COMPILER}, "C# compiler", "Roslyn")')
        lines += [self.container_line(n, link=False, external=True) for n in neighbours]
        lines.append(f'Container_Boundary({me}, "{project.name}") {{')
        lines += [f"  {_component_line(self.part_alias(c), self.label(c), c, self.code_id(c))}" for c in scan.parts]
        lines.append("}")
        lines += [f'Rel({_COMPILER}, {self.part_alias(c)}, "runs")' for c in scan.parts if _is_entry(c)]
        lines += _edge_rels(refs, calls)
        scored = _score(refs, calls)
        notes = [_component_note(self.repo, self.part_alias(c), c) for c in scan.parts]
        notes += [
            f"## {self.alias[n.project.path]}\n\nSee [its namespaces](/components/{self.alias[n.project.path]}.md).\n"
            for n in neighbours
            if n.parts
        ]
        notes.append(_derivation_note(scored, calls_known=self.callgraph is not None))
        head = _frontmatter(
            "C4 Component",
            f"{project.name} — namespaces",
            "Namespaces as components; relationships from type references, checked against the call graph.",
            self.where,
        )
        score = PathwayScore(len(scored.both), len(scored.only_refs), len(scored.only_calls))
        return _ComponentsView(head + _diagram(lines) + "\n" + "\n".join(notes), score)

    def _calls(self, scan: _ProjectScan, neighbours: list[_ProjectScan]) -> Counter[Edge] | None:
        if self.callgraph is None:
            return None
        path = self.callgraph / f"{scan.project.name}.callgraph.txt"
        if not path.is_file():
            return None
        owner_of = {f"{c.namespace}.{t}": self.part_alias(c) for c in scan.parts for t in c.types}
        for n in neighbours:
            owner_of |= {f"{c.namespace}.{t}": self.alias[n.project.path] for c in n.parts for t in c.types}
        return call_edges(path, owner_of)

    def code_view(self, part: Component) -> str:
        if self.graph is not None and self.graph.types:  # an edges-only file keeps the class view
            return self.flow_view(part, self.graph)
        types = set(part.types)
        declared = sorted(((t, f) for f in part.files for t in f.types), key=lambda pair: pair[0].name)
        lines = ["classDiagram", "direction LR"]
        for t, _f in declared:
            body = ([f"  <<{t.kind}>>"] if t.kind != "class" else []) + [f"  +{m}" for m in t.members]
            lines += [f"class {t.name} {{", *body, "}"] if body else [f"class {t.name}"]
        edges = sorted({(d, n) for f in part.files for d in f.declares for n in f.names & types if n not in f.declares})
        lines += [f"{a} --> {b}" for a, b in edges]
        for f in part.files:
            url = self.where.blob(self.repo, f.path)
            lines += [f'click {t} href "{url}"' for t in f.declares if url]
        notes = [_type_note(self.repo, t, f, self.where.blob(self.repo, f.path)) for t, f in declared]
        head = _frontmatter(
            "C4 Code",
            f"{part.namespace or 'global'} — types",
            "Types in the namespace and which of them name each other. Each type links to its source.",
            self.where,
        )
        return head + _diagram(lines) + "\n" + "\n".join(notes)

    def flow_view(self, part: Component, graph: CallGraph) -> str:
        """Members are the nodes and calls the edges: one subgraph per type, and calls that leave the namespace
        end on a node that links to the namespace they land in."""
        mine = sorted({_qualified(part.namespace, t) for t in part.types})
        local = set(mine)

        def drawable(key: MemberKey) -> bool:
            return key.type in self.home and graph.callable(key)

        calls = sorted((a, b) for a, b in graph.calls if drawable(a) and drawable(b) and local & {a.type, b.type})
        impls = sorted((a, b) for a, b in graph.implements if drawable(a) and drawable(b) and local & {a.type, b.type})
        linked = {k for pair in [*calls, *impls] for k in pair}
        lines = ["flowchart LR"]
        clicks: list[str] = []
        for type_id in mine:
            decl = graph.types.get(type_id)
            kind = decl.kind if decl else "type"
            name = type_id.rsplit(".", 1)[-1]
            members = [k for k in graph.of_type(type_id) if graph.callable(k)]
            if not members:
                lines.append(f'  {_node_id("type " + type_id)}["{_q(name)} · {kind}"]')
            else:
                lines += [f'  subgraph {_node_id("type " + type_id)}["{_q(name)} · {kind}"]', "    direction TB"]
                lines += [f'    {_node_id(k.type + "." + k.name)}["{_q(graph.label(k))}"]' for k in members]
                lines.append("  end")
            for k in members:
                url = self._member_url(graph, k)
                if url:
                    clicks.append(f'  click {_node_id(k.type + "." + k.name)} href "{url}"')
        away: dict[str, list[MemberKey]] = {}
        for k in sorted(linked):
            if k.type not in local:
                away.setdefault(self.code_id(self.home[k.type]), []).append(k)
        for code_id, keys in sorted(away.items()):
            lines.append(f'  subgraph {_node_id("away " + code_id)}["↗ {_q(self.label(self.home[keys[0].type]))}"]')
            lines += [
                f'    {_node_id(k.type + "." + k.name)}["{_q(k.type.rsplit(".", 1)[-1])}.{_q(graph.label(k))}"]'
                for k in keys
            ]
            lines.append("  end")
            clicks += [f'  click {_node_id(k.type + "." + k.name)} href "/{code_id}.md"' for k in keys]
        lines += [f"  {_node_id(a.type + '.' + a.name)} --> {_node_id(b.type + '.' + b.name)}" for a, b in calls]
        lines += [f"  {_node_id(a.type + '.' + a.name)} -.-> {_node_id(b.type + '.' + b.name)}" for a, b in impls]
        lines += clicks
        notes = [self._flow_note(graph, t, local) for t in mine]
        inbound = len({a for a, b in calls if a.type not in local})
        outbound = len({b for a, b in calls if b.type not in local})
        intro = (
            "Each box is a type and each node one of its members, overloads together. A solid arrow is a call; a"
            " dotted one is where a call through an interface or base class lands. Boxes marked ↗ are other"
            " namespaces and link to their own view; a member here links to its source.\n\n"
            f"{_n(len(calls), 'call')} touch this namespace: {_n(inbound, 'member')} outside call in, and it calls"
            f" {_n(outbound, 'member')} outside.\n"
        )
        head = _frontmatter(
            "C4 Code",
            f"{part.namespace or 'global'} — members and calls",
            "Types in the namespace, their members, and which member calls which, as the compiler binds them.",
            self.where,
        )
        return head + _diagram(lines) + "\n" + intro + "\n" + "\n".join(notes)

    def _member_url(self, graph: CallGraph, key: MemberKey) -> str | None:
        decl = graph.types.get(key.type)
        first = min(graph.members.get(key, ()), key=lambda d: d.line, default=None)
        if decl is None or first is None:
            return None
        url = self.where.blob(self.repo, self.repo / decl.path)
        return f"{url}#L{first.line}" if url else None

    def _flow_note(self, graph: CallGraph, type_id: str, local: set[str]) -> str:
        decl = graph.types.get(type_id)
        name = type_id.rsplit(".", 1)[-1]
        part = self.home.get(type_id)
        info = next((t for f in part.files for t in f.types if t.name == name), None) if part else None
        head = f"**{decl.access} {decl.kind}**" if decl else "**type**"
        lines = [f"## {name}", "", head + (f" · {info.summary}" if info and info.summary else "")]
        if decl:
            url = self.where.blob(self.repo, self.repo / decl.path)
            file = PurePosixPath(decl.path)
            where = f"[{file.name}]({url}#L{decl.line})" if url else f"`{file.name}`"
            lines += ["", f"{where} in {file.parent.as_posix()}/"]
        rows = []
        for key in graph.of_type(type_id):
            decls = graph.members[key]
            kinds = ", ".join(sorted({d.kind for d in decls}))
            access = ", ".join(sorted({d.access for d in decls}))
            count = f" ×{len(decls)}" if len(decls) > 1 else ""
            out = sorted({b for a, b in graph.calls if a == key and b.type in self.home})
            into = sorted({a for a, b in graph.calls if b == key and a.type in self.home})
            rows.append(
                f"| `{graph.label(key)}`{count} | {kinds} | {access} | {_names(out, local, graph)}"
                f" | {_names(into, local, graph)} |"
            )
        if rows:
            lines += ["", "| Member | Kind | Access | Calls | Called by |", "|---|---|---|---|---|", *rows]
        return "\n".join(lines) + "\n"


def _rel(repo: Path, path: Path) -> str:
    return path.relative_to(repo).as_posix() if path.is_relative_to(repo) else path.as_posix()


def _is_entry(part: Component) -> bool:
    return any(f.is_generator for f in part.files)


def _common_namespace(names: list[str]) -> str:
    split = [n.split(".") for n in names if n]
    if not split:
        return ""
    shared: list[str] = []
    for depth in range(min(len(parts) for parts in split)):
        segment = split[0][depth]
        if any(parts[depth] != segment for parts in split):
            break
        shared.append(segment)
    return ".".join(shared)


def _qualified(namespace: str, name: str) -> str:
    return f"{namespace}.{name}" if namespace else name


def _node_id(text: str) -> str:
    """A Mermaid id: letters, digits and underscores, the same for the same member on every run."""
    return "n_" + re.sub(r"[^A-Za-z0-9]", "_", text)


def _names(keys: list[MemberKey], local: set[str], graph: CallGraph) -> str:
    """Members as a reader writes them: ``Type.Run()``, with the namespace only when it is another one."""
    shown = [f"`{k.type.rsplit('.', 1)[-1] if k.type in local else k.type}.{graph.label(k)}`" for k in keys]
    return ", ".join(shown[:12]) + (f" and {len(shown) - 12} more" if len(shown) > 12 else "")


def _component_line(alias: str, label: str, part: Component, code_id: str) -> str:
    shown = ", ".join(part.types[:6]) + (" …" if len(part.types) > 6 else "")
    count = f"{len(part.types)} type{'' if len(part.types) == 1 else 's'}"
    return f'Component({alias}, "{_q(label)}", "{count}", "{_q(shown)}", $link="/{code_id}.md")'


def _edge_rels(refs: Counter[Edge], calls: Counter[Edge] | None) -> list[str]:
    rels: list[str] = []
    for edge, count in sorted(refs.items()):
        called = calls.get(edge, 0) if calls is not None else 0
        tech = _n(count, "type") + (f", {_n(called, 'call')}" if called else "")
        rels.append(f'Rel({edge.source}, {edge.target}, "uses", "{tech}")')
    rels += [
        f'Rel({edge.source}, {edge.target}, "calls", "{_n(n, "call")}, no type named")'
        for edge, n in sorted((calls or Counter[Edge]()).items())
        if edge not in refs
    ]
    return rels


def _score(refs: Counter[Edge], calls: Counter[Edge] | None) -> _Scored:
    seen_calls = set(calls or ())
    return _Scored(sorted(set(refs) & seen_calls), sorted(set(refs) - seen_calls), sorted(seen_calls - set(refs)))


def _derivation_note(scored: _Scored, *, calls_known: bool) -> str:
    refs, calls = scored.only_refs, scored.only_calls

    def show(edges: list[Edge]) -> str:
        return ", ".join(f"`{e.source} → {e.target}`" for e in edges) or "none"

    if not calls_known:
        return "## Derivation\n\nRelationships come from type references only; no call graph was given.\n"
    return (
        "## Derivation\n\n"
        "Two pathways, scored against each other:\n\n"
        f"- **Both** type references and the call graph: {len(scored.both)} relationships.\n"
        f"- **Type references only** (mentions, signatures, or calls the graph could not resolve): {show(refs)}.\n"
        f"- **Call graph only** (a call with no type named, e.g. a static helper): {show(calls)}.\n"
    )


def _container_note(repo: Path, alias: str, scan: _ProjectScan) -> str:
    p = scan.project
    linked = sum(1 for f in scan.files if not f.path.is_relative_to(p.directory))
    shared = f" {linked} of them {'is' if linked == 1 else 'are'} compiled in from another project." if linked else ""
    role = "Ships as a Roslyn analyzer." if p.is_analyzer else "Ships as a library."
    total = sum(f.lines for f in scan.files)
    frameworks = ", ".join(p.frameworks) or "unknown framework"
    return (
        f"## {alias}\n\n{role} Targets {frameworks}. {_n(len(scan.files), 'source file')}, {_n(total, 'line')}."
        f"{shared}\n\nProject file: {_where(repo, p.path)}\n"
    )


def _endpoints(rel: str) -> tuple[str, ...]:
    """``Rel(a, b, "label")`` connects ``a`` and ``b``."""
    return tuple(part.strip() for part in rel.split("(", 1)[-1].split(",")[:2])


def _n(count: int, noun: str) -> str:
    return f"{count} {noun}{'' if count == 1 else 's'}"


def _where(repo: Path, path: Path) -> str:
    """A file as ``Name.cs`` in ``dir/``: the name is what a reader scans for, the directory is context."""
    parent = PurePosixPath(_rel(repo, path)).parent.as_posix()
    return f"`{path.name}` in {parent}/" if parent != "." else f"`{path.name}`"


def _type_note(repo: Path, info: TypeInfo, f: SourceFile, url: str | None) -> str:
    lines = [f"## {info.name}", "", f"**{info.kind}**" + (f" · {info.summary}" if info.summary else "")]
    source = f"[{f.path.name}]({url})" if url else f"`{f.path.name}`"
    parent = PurePosixPath(_rel(repo, f.path)).parent.as_posix()
    lines += ["", f"{source} in {parent}/ · {_n(f.lines, 'line')}"]
    if info.members:
        lines += ["", "Public: " + ", ".join(f"`{m}`" for m in info.members)]
    return "\n".join(lines) + "\n"


def _component_note(repo: Path, alias: str, part: Component) -> str:
    def declares(f: SourceFile) -> str:
        # ``Gen.cs`` declaring ``Gen`` says nothing new; list only what the filename does not.
        other = [t for t in f.declares if t != f.path.stem]
        return ", ".join(other[:3]) + (" …" if len(other) > 3 else "")

    rows = [f"| `{f.path.name}` | {declares(f)} | {f.lines} |" for f in part.files[:12]]
    more = f"\n\nAnd {_n(len(part.files) - 12, 'more file')}." if len(part.files) > 12 else ""
    dirs = sorted({PurePosixPath(_rel(repo, f.path)).parent.as_posix() for f in part.files})
    where = f"in {dirs[0]}/" if len(dirs) == 1 else "in " + ", ".join(f"{d}/" for d in dirs)
    total = sum(f.lines for f in part.files)
    table = "\n".join(["| File | Also declares | Lines |", "|---|---|---|", *rows])
    head = f"`{part.namespace}`: {_n(len(part.types), 'type')} in {_n(len(part.files), 'file')}, {_n(total, 'line')}"
    return f"## {alias}\n\n{head}, {where}\n\n{table}{more}\n"


def _parent_neighbours(root: Path, parent: Parent | None) -> _Neighbours:
    """The parent view's elements that touch the zoomed alias, copied verbatim so the child refines the parent,
    and the zoomed element's label, which the boundary keeps so the alias reads the same in both views."""
    if parent is None or not root.is_dir():
        return _Neighbours([], None)
    concept = load_bundle(root).concepts.get(parent.concept)
    diagram = concept.diagram if concept else None
    if diagram is None:
        return _Neighbours([], None)
    touching = [r for r in diagram.relationships if parent.alias in (r.source, r.target)]
    near = {r.source for r in touching} | {r.target for r in touching}
    source = diagram.source.splitlines()
    # The boundary takes the parent's alias, so the parent's relationships still land on it.
    label = next((e.label for e in diagram.elements if e.alias == parent.alias), None)
    copied = [source[e.line].strip() for e in diagram.elements if e.alias in near - {parent.alias}]
    return _Neighbours(copied + [source[r.line].strip() for r in touching], label)


def derive(
    repo: Path,
    root: Path,
    *,
    parent: Parent | None,
    callgraph: Path | None,
    guides: tuple[str, ...] = (),
    changes: int = 0,
) -> Derived:
    from rig.c4_okf.narrate import change_pages, guide_pages

    deriver = _Deriver(repo.resolve(), origin(repo), callgraph)
    files = {"containers.md": deriver.containers(parent, root)}
    pathways: dict[str, PathwayScore] = {}
    for scan in deriver.scans.values():
        if not scan.parts:
            continue
        alias = deriver.alias[scan.project.path]
        view = deriver.components_view(scan)
        files[f"components/{alias}.md"], pathways[alias] = view.text, view.score
        for part in scan.parts:
            files.setdefault(f"{deriver.code_id(part)}.md", deriver.code_view(part))
    files |= guide_pages(deriver, list(guides), PRODUCER)
    files |= change_pages(deriver, changes, PRODUCER)
    return Derived(files, pathways)


def orphans(root: Path, files: dict[str, str]) -> list[str]:
    """Concepts this deriver wrote on an earlier run that a fresh derivation no longer produces.

    A removed or renamed project or namespace leaves its old views behind, and the viewer loads every concept, so
    they would linger. Ownership is the ``generated.by`` stamp: a hand-written concept is never an orphan.
    """
    if not root.is_dir():
        return []
    producer = PRODUCER.split("/", 1)[0] + "/"
    owned = (
        f"{c.id}.md"
        for c in load_bundle(root).concepts.values()
        if c.generated is not None and (c.generated.by or "").startswith(producer)
    )
    return sorted(name for name in owned if name not in files)


def stale(root: Path, files: dict[str, str]) -> list[str]:
    """Derived paths whose file on disk is missing or differs from a fresh derivation, plus orphans."""
    changed = [
        name
        for name, text in files.items()
        if not (root / name).is_file() or (root / name).read_text(encoding="utf-8") != text
    ]
    return sorted([*changed, *orphans(root, files)])


def write(root: Path, files: dict[str, str]) -> list[str]:
    """Write every stale derived file and delete orphans; returns the paths written or removed."""
    changed = stale(root, files)
    for name in changed:
        path = root / name
        if name not in files:
            path.unlink()
            continue
        path.parent.mkdir(parents=True, exist_ok=True)
        _ = path.write_text(files[name], encoding="utf-8")
    return changed

"""Prose beside the derived views: a repository's own guide pages, and walkthroughs of its recent changes.

Both are generated like the views, so ``--check`` catches them going stale.

**Guide pages** are copied from the repository (``--guide docs/guide/04-walkthrough.md``). Every inline code
span that names a type, ``Type.Member`` or a namespace is linked to the code view that draws it. A link to
another copied page stays in the bundle, and any other relative link goes to GitHub at the derived commit.

**Changesets** (``--changes N``) are the last ``N`` first-parent commits that touched ``src/``. A PR merged with
squash is one of these. Each change gets a call graph of the members it touched and the members one call away,
and a suggested reading order: the touched members, callers before callees. Lines map to members through the
call graph, which describes ``HEAD``. That mapping is exact only for a file unchanged since the commit. For any
other file the page names the types and says why it stops there.
"""

from __future__ import annotations

import json
import re
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path, PurePosixPath

from rig.c4_okf.callgraph import CallGraph, MemberKey
from rig.c4_okf.views import Deriver
from rig.c4_okf.views import diagram as _diagram
from rig.c4_okf.views import node_id as _node_id
from rig.c4_okf.views import plural as _n
from rig.c4_okf.views import quote as _q
from rig.proc import git, git_out

#: The most nodes a change diagram draws; neighbours beyond it are listed, not drawn.
MAX_NODES = 90


def yaml_str(text: str) -> str:
    """A frontmatter value: bare when YAML reads it back unchanged, JSON-quoted (valid YAML) otherwise."""
    if re.search(r"[:#\[\]{},&*!|>'\"%@`]", text) or text != text.strip():
        return json.dumps(text, ensure_ascii=False)
    return text


def _head(kind: str, title: str, description: str, resource: str, date: str, producer: str) -> str:
    return "\n".join(
        [
            "---",
            f"type: {kind}",
            f"title: {yaml_str(title)}",
            f"description: {yaml_str(description)}",
            f"resource: {resource}",
            f"generated: {{ by: {producer}, at: {date} }}",
            "tags: [derived]",
            "---",
            "",
        ]
    )


# --- names → views -------------------------------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class _Target:
    """Where a code span links: a code view, and the heading in it (empty for the view itself)."""

    view: str
    anchor: str


@dataclass(slots=True)
class _Names:
    """Resolves a name as prose writes it (``StatsRunner``, ``CliEntryPoint.Run``, ``Crd.Reader.Hosting``)."""

    types: dict[str, list[str]] = field(default_factory=dict)  # short name → qualified ids
    namespaces: dict[str, str] = field(default_factory=dict)  # namespace → code view id

    @classmethod
    def of(cls, d: Deriver) -> _Names:
        names = cls()
        for qualified in sorted(d.home):
            names.types.setdefault(qualified.rsplit(".", 1)[-1], []).append(qualified)
        for part in d.parts():
            _ = names.namespaces.setdefault(part.namespace, d.code_id(part))
        return names

    def target(self, d: Deriver, text: str) -> _Target | None:
        """``(code view id, anchor)`` for a code span, or None when it names nothing drawn, or names it twice."""
        bare = re.sub(r"<[^<>]*>|\(.*\)$", "", text.strip())
        if not re.fullmatch(r"[A-Za-z_][\w.]*", bare):
            return None
        if bare in self.namespaces:
            return _Target(self.namespaces[bare], "")
        parts = bare.split(".")
        # Longest qualified prefix first, so `Crd.Reader.Layout.CrdLayout` beats a type called `Crd`.
        for i in range(len(parts), 0, -1):
            head = ".".join(parts[:i])
            if head in d.home:
                return _Target(d.code_id(d.home[head]), _anchor(parts[i - 1]))
        # Then a short name, read the way prose writes it: `Type`, `Type.Member`, or `Namespace.Type`.
        candidates = [parts[0]] if len(parts) == 1 else [parts[0], parts[1]] if len(parts) == 2 else []
        for short in candidates:
            found = self.types.get(short, [])
            if len(found) == 1:
                return _Target(d.code_id(d.home[found[0]]), _anchor(short))
        return None


def _anchor(name: str) -> str:
    """The id a renderer gives ``## Name``: GitHub-style, lower case, punctuation dropped."""
    return re.sub(r"[^\w\- ]", "", name.lower()).replace(" ", "-")


# --- guide pages ---------------------------------------------------------------------------------------------

_SPAN = re.compile(r"(?<!`)`([^`\n]+)`(?!`)")
_LINK = re.compile(r"(!?\[[^\]]*\])\(([^()\s]+)\)")


def guide_pages(d: Deriver, paths: list[str], producer: str) -> dict[str, str]:
    """Bundle path → page, for each repository markdown file named in ``paths``."""
    names = _Names.of(d)
    copied = {PurePosixPath(p).as_posix(): f"guide/{PurePosixPath(p).name}" for p in paths}
    pages: dict[str, str] = {}
    for source, target in sorted(copied.items()):
        text = (d.repo / source).read_text(encoding="utf-8")
        title = next((line[2:].strip() for line in text.splitlines() if line.startswith("# ")), source)
        blob = f"{d.where.web}/blob/{d.where.commit}/{source}" if d.where.web else source
        body = _link_guide(d, names, text, source, copied)
        note = (
            f"> Copied from [{source}]({blob}) at `{d.where.commit[:7]}`. Code names link to the C4 code"
            " views. Edit the original, not this page: the next derive overwrites it.\n"
        )
        body = re.sub(r"^(# .*\n)", lambda m, note=note: m.group(1) + "\n" + note, body, count=1, flags=re.M)
        description = f"{title}, from the repository's own docs, with code names linked to the C4 views."
        pages[target] = _head("Guide", title, description, blob, d.where.date, producer) + body
    return pages


def _link_guide(d: Deriver, names: _Names, text: str, source: str, copied: dict[str, str]) -> str:
    out: list[str] = []
    fenced = False
    for line in text.splitlines():
        if re.match(r"^\s*(```|~~~)", line):
            fenced = not fenced
            out.append(line)
            continue
        if fenced:
            out.append(line)
            continue
        line = _LINK.sub(lambda m: m.group(1) + f"({_relink(d, m.group(2), source, copied)})", line)
        out.append(_link_spans(d, names, line))
    return "\n".join(out) + "\n"


def _relink(d: Deriver, href: str, source: str, copied: dict[str, str]) -> str:
    if re.match(r"^[a-z][a-z0-9+.-]*:|^#|^/", href, re.I):
        return href
    path, _, hash_ = href.partition("#")
    resolved = PurePosixPath(_normal(PurePosixPath(source).parent / path))
    anchor = f"#{hash_}" if hash_ else ""
    if resolved.as_posix() in copied:
        return PurePosixPath(copied[resolved.as_posix()]).name + anchor
    kind = "tree" if not resolved.suffix else "blob"
    return f"{d.where.web}/{kind}/{d.where.commit}/{resolved.as_posix()}{anchor}" if d.where.web else href


def _normal(path: PurePosixPath) -> str:
    parts: list[str] = []
    for part in path.parts:
        if part == "..":
            if parts:
                _ = parts.pop()
        elif part != ".":
            parts.append(part)
    return "/".join(parts)


def _link_spans(d: Deriver, names: _Names, line: str) -> str:
    """Links code spans outside existing links: ``[`x`](y)`` is left alone."""
    pieces = re.split(r"(\[[^\]]*\]\([^)]*\))", line)
    for i in range(0, len(pieces), 2):

        def link(m: re.Match[str]) -> str:
            found = names.target(d, m.group(1))
            if found is None:
                return m.group(0)
            view, anchor = found.view, found.anchor
            return f"[`{m.group(1)}`](../{view}.md{'#' + anchor if anchor else ''})"

        pieces[i] = _SPAN.sub(link, pieces[i])
    return "".join(pieces)


# --- changesets ----------------------------------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class _File:
    path: str
    added: int
    removed: int
    lines: tuple[int, ...]  # new-side lines touched (a pure deletion marks the line after it)
    exact: bool  # unchanged since, so HEAD's call graph describes it
    exists: bool


@dataclass(frozen=True, slots=True)
class _Commit:
    sha: str
    date: str
    subject: str
    body: str
    files: tuple[_File, ...]

    @property
    def number(self) -> str | None:
        m = re.search(r"\(#(\d+)\)\s*$", self.subject)
        return m.group(1) if m else None

    @property
    def page(self) -> str:
        return f"changes/pr-{self.number}" if self.number else f"changes/{self.sha[:7]}"


def _commits(repo: Path, count: int) -> list[_Commit]:
    log = git(
        "log", "--first-parent", f"-n{count}", "--format=%H%x1f%cs%x1f%s%x1f%b%x1e", "HEAD", "--", "src", cwd=repo
    )
    commits: list[_Commit] = []
    for record in log.out.split("\x1e") if log.ok else []:
        if not record.strip():
            continue
        sha, date, subject, body = record.strip("\n").split("\x1f")
        if not git_out("rev-parse", "--verify", "--quiet", f"{sha}^", cwd=repo):
            continue  # a root commit has nothing to diff against
        commits.append(_Commit(sha, date, subject, _without_trailers(body), tuple(_files(repo, sha))))
    return commits


def _without_trailers(body: str) -> str:
    """The message without ``Co-authored-by:``-style trailers, which say who, not what."""
    lines = body.strip().splitlines()
    while lines and (re.match(r"^[A-Z][\w-]+: .+", lines[-1]) or not lines[-1].strip()):
        _ = lines.pop()
    return "\n".join(lines).strip()


@dataclass(frozen=True, slots=True)
class _Stat:
    added: int
    removed: int


def _files(repo: Path, sha: str) -> list[_File]:
    stats: dict[str, _Stat] = {}
    for line in git_out("diff", "--numstat", "-M", f"{sha}^", sha, "--", "src", cwd=repo).splitlines():
        added, removed, path = line.split("\t", 2)
        path = re.sub(r"\{[^{}]* => ([^{}]*)\}", r"\1", path).split(" => ")[-1].replace("//", "/")
        stats[path] = _Stat(int(added) if added.isdigit() else 0, int(removed) if removed.isdigit() else 0)
    touched: dict[str, list[int]] = defaultdict(list)
    current = ""
    diff = git("diff", "-U0", "-M", "--no-color", f"{sha}^", sha, "--", "src", cwd=repo).out
    for line in diff.splitlines():
        if line.startswith("+++ "):
            current = line[6:] if line.startswith("+++ b/") else ""
        elif line.startswith("@@") and current:
            m = re.match(r"@@ -\S+ \+(\d+)(?:,(\d+))? @@", line)
            if m:
                start, size = int(m.group(1)), int(m.group(2) or "1")
                touched[current] += list(range(start, start + size)) if size else [start + 1]
    files: list[_File] = []
    for path in sorted(stats):
        stat = stats[path]
        at_commit = git_out("rev-parse", f"{sha}:{path}", cwd=repo)
        at_head = git_out("rev-parse", f"HEAD:{path}", cwd=repo)
        files.append(
            _File(
                path, stat.added, stat.removed, tuple(touched.get(path, ())), at_head == at_commit != "", at_head != ""
            )
        )
    return files


@dataclass(frozen=True, slots=True)
class _Span:
    """Where a declaration starts: a member (*key*), or the type itself when *key* is None."""

    line: int
    key: MemberKey | None
    type_id: str


def _member_spans(graph: CallGraph) -> dict[str, list[_Span]]:
    """Per file, the start line of every type and member declared in it, in order."""
    spans: dict[str, list[_Span]] = defaultdict(list)
    for type_id, decl in graph.types.items():
        spans[decl.path].append(_Span(decl.line, None, type_id))
        for key in graph.of_type(type_id):
            spans[decl.path] += [_Span(m.line, key, type_id) for m in graph.members[key]]
    for items in spans.values():
        items.sort(key=lambda s: (s.line, s.key is not None, s.type_id, s.key or MemberKey("", "")))
    return spans


@dataclass(slots=True)
class _Touched:
    members: set[MemberKey] = field(default_factory=set)
    types: set[str] = field(default_factory=set)  # touched outside any member, or only known by file
    loose: list[_File] = field(default_factory=list)  # files the graph cannot place


def _touched(graph: CallGraph, commit: _Commit) -> _Touched:
    spans = _member_spans(graph)
    by_path: dict[str, list[str]] = defaultdict(list)
    for type_id, decl in graph.types.items():
        by_path[decl.path].append(type_id)
    out = _Touched()
    for f in commit.files:
        if not f.path.endswith(".cs"):
            continue
        if not f.exact:
            if by_path.get(f.path):
                out.types.update(by_path[f.path])
            else:
                out.loose.append(f)
            continue
        items = spans.get(f.path, [])
        for line in f.lines:
            before = [s for s in items if s.line <= line]
            if before:
                span = before[-1]
                if span.key is not None and graph.callable(span.key):
                    out.members.add(span.key)
                else:
                    out.types.add(span.type_id)
            elif items:
                out.types.add(items[0].type_id)
    return out


def _reading_order(graph: CallGraph, members: set[MemberKey]) -> list[MemberKey]:
    """Touched members, each after the touched members that call it; ties (and cycles) by name."""
    calls = {c for c in graph.calls if c.source in members and c.target in members and c.source != c.target}
    order: list[MemberKey] = []
    left = set(members)
    while left:
        ready = sorted(m for m in left if not any(c.source in left and c.target == m for c in calls))
        pick = ready[0] if ready else sorted(left)[0]
        order.append(pick)
        left.discard(pick)
    return order


def change_pages(d: Deriver, count: int, producer: str) -> dict[str, str]:
    graph = d.graph
    if graph is None or count <= 0:
        return {}
    commits = _commits(d.repo, count)
    pages = {f"{c.page}.md": _change_page(d, graph, c, producer) for c in commits}
    rows = [
        f"| [{_md(c.subject)}]({PurePosixPath(c.page).name}.md) | {c.date} | {len(c.files)} | "
        f"+{sum(f.added for f in c.files)} −{sum(f.removed for f in c.files)} |"
        for c in commits
    ]
    listing = (
        "# Recent changes\n\n"
        f"The last {len(commits)} changes merged to the default branch that touched `src/`, newest first. Each"
        " page draws what the change touched in the call graph and suggests an order to read it in.\n\n"
        "| Change | Merged | Files | Lines |\n|---|---|---|---|\n" + "\n".join(rows) + "\n"
    )
    tree = d.where.tree() or "unknown"
    head = _head(
        "Changes",
        "Recent changes",
        "Walkthroughs of recent changes, drawn on the C4 code views.",
        tree,
        d.where.date,
        producer,
    )
    pages["changes/changes.md"] = head + listing
    return pages


def _md(text: str) -> str:
    return text.replace("|", "\\|").replace("[", "\\[").replace("]", "\\]")


def _change_page(d: Deriver, graph: CallGraph, commit: _Commit, producer: str) -> str:
    touched = _touched(graph, commit)
    changed = touched.members
    near: set[MemberKey] = set()
    for call in sorted(graph.calls | graph.implements):
        a, b = call.source, call.target
        if a in changed and b not in changed and b.type in d.home and graph.callable(b):
            near.add(b)
        if b in changed and a not in changed and a.type in d.home and graph.callable(a):
            near.add(a)
    room = max(0, MAX_NODES - len(changed))
    drawn_near = set(sorted(near, key=lambda k: (k.type not in {c.type for c in changed}, k))[:room])
    shown = changed | drawn_near
    lines = ["flowchart LR"]
    clicks: list[str] = []
    by_type: dict[str, list[MemberKey]] = defaultdict(list)
    for k in sorted(shown):
        by_type[k.type].append(k)
    for type_id in sorted(set(by_type) | touched.types):
        name = type_id.rsplit(".", 1)[-1]
        part = d.home.get(type_id)
        where = f" ({d.label(part)})" if part else ""
        tid = _node_id("type " + type_id)
        if by_type.get(type_id):
            lines += [f'  subgraph {tid}["{_q(name)}{_q(where)}"]', "    direction TB"]
            lines += [f'    {_node_id(k.type + "." + k.name)}["{_q(graph.label(k))}"]' for k in by_type[type_id]]
            lines.append("  end")
        else:
            lines.append(f'  {tid}["{_q(name)}{_q(where)}"]')
        if type_id in touched.types:
            lines.append(f"  class {tid} changed")
        if part:
            target = f"/{d.code_id(part)}.md#{_anchor(name)}"
            for k in by_type.get(type_id, []):
                clicks.append(f'  click {_node_id(k.type + "." + k.name)} href "{target}"')
            if not by_type.get(type_id):
                clicks.append(f'  click {tid} href "{target}"')
    for call in sorted(graph.calls):
        a, b = call.source, call.target
        if a in shown and b in shown and a != b and (a in changed or b in changed):
            lines.append(f"  {_node_id(a.type + '.' + a.name)} --> {_node_id(b.type + '.' + b.name)}")
    for call in sorted(graph.implements):
        a, b = call.source, call.target
        if a in shown and b in shown and (a in changed or b in changed):
            lines.append(f"  {_node_id(a.type + '.' + a.name)} -.-> {_node_id(b.type + '.' + b.name)}")
    lines += [f"  class {_node_id(k.type + '.' + k.name)} changed" for k in sorted(changed)]
    lines.append("  classDef changed stroke-width:3px")
    lines += clicks
    if len(lines) == 2:  # nothing in the graph: a change to files the derive does not draw
        lines.insert(1, '  none["No drawn code changed"]')

    web = d.where.web
    link = f"{web}/commit/{commit.sha}" if web else commit.sha[:7]
    pr = f" · [pull request #{commit.number}]({web}/pull/{commit.number})" if web and commit.number else ""
    out = [
        f"# {commit.subject}",
        "",
        f"Merged {commit.date} as [{commit.sha[:7]}]({link}){pr}. Touched {_n(len(commit.files), 'file')},"
        f" +{sum(f.added for f in commit.files)} −{sum(f.removed for f in commit.files)} lines.",
        "",
        "Members the change edited have a heavy border; the rest are one call away, so they are what to"
        " re-read for a knock-on effect. Click a node for its type in the code view."
        + (f" {len(near) - len(drawn_near)} more neighbours are listed below, not drawn." if near - drawn_near else ""),
        "",
    ]
    if commit.body:
        out += [
            "## What the change says about itself",
            "",
            *[f"> {line}" if line else ">" for line in commit.body.splitlines()],
            "",
        ]
    order = _reading_order(graph, changed)
    if order:
        out += ["## Read it in this order", "", "Callers come before what they call, so each step has its context.", ""]
        for i, k in enumerate(order, 1):
            part = d.home.get(k.type)
            name = k.type.rsplit(".", 1)[-1]
            view = f"../{d.code_id(part)}.md#{_anchor(name)}" if part else None
            label = f"`{name}.{graph.label(k)}`"
            callers = sorted(
                {c.source for c in graph.calls if c.target == k and c.source not in changed and c.source.type in d.home}
            )
            first = min(graph.members[k], key=lambda m: m.line)
            decl = graph.types.get(k.type)
            src = f"{web}/blob/{commit.sha}/{decl.path}#L{first.line}" if web and decl else None
            extra = (
                f" · called from {len(callers)} untouched {'member' if len(callers) == 1 else 'members'}"
                if callers
                else ""
            )
            out.append(
                f"{i}. {f'[{label}]({view})' if view else label}" + (f" · [source]({src})" if src else "") + extra
            )
        out.append("")
    if touched.types:
        out += ["## Types touched outside a single member", ""]
        out += [
            f"- `{t.rsplit('.', 1)[-1]}` in {d.label(d.home[t]) if t in d.home else t.rsplit('.', 1)[0]}"
            for t in sorted(touched.types)
        ]
        out.append("")
    out += ["## Files", "", "| File | Lines | Mapped |", "|---|---|---|"]
    for f in commit.files:
        why = (
            ("to members" if f.exact else "changed since, so to types" if f.exists else "no longer at this path")
            if f.path.endswith(".cs")
            else "not code"
        )
        url = f"{web}/blob/{commit.sha}/{f.path}" if web else None
        name = f"[{f.path}]({url})" if url else f"`{f.path}`"
        out.append(f"| {name} | +{f.added} −{f.removed} | {why} |")
    out.append("")
    if near - drawn_near:
        rest = sorted(near - drawn_near)
        out += [
            "## Neighbours not drawn",
            "",
            ", ".join(f"`{k.type.rsplit('.', 1)[-1]}.{graph.label(k)}`" for k in rest),
            "",
        ]
    title = f"Change: {commit.subject}"
    head = _head("C4 Code", title, f"What {commit.sha[:7]} touched, in the call graph.", link, commit.date, producer)
    return head + _diagram(lines) + "\n" + "\n".join(out)

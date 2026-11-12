"""Read the C4 structure of a .NET repository straight from its code, without running .NET.

Two levels are recoverable from files alone:

- **Containers** are projects. ``ProjectReference`` gives the relationships, and an analyzer reference
  (``OutputItemType="Analyzer"``) or a Roslyn-component project is a build-time relationship.
- **Components** are namespaces inside a project. Two pathways give their relationships, and they are kept
  apart on purpose so each can be scored against the other:

  1. *Type references*: every type a file declares is indexed by namespace, and a file that names a type from
     another namespace depends on it. Cheap, sees everything a reader sees, but cannot tell a call from a
     mention in a signature.
  2. *Call graph*: the ``E:caller | callee`` edge files a Roslyn script writes (Parquet.SourceGenerator's
     ``scripts/CallGraph.cs`` is the model). Precise where it resolves, blind where it does not (delegates,
     virtual dispatch, framework calls).
"""

from __future__ import annotations

import re
from collections import Counter
from dataclasses import dataclass
from pathlib import Path, PurePosixPath, PureWindowsPath
from typing import Annotated
from xml.etree import ElementTree

from defusedxml import ElementTree as SafeTree
from pydantic import Field

from rig.typed_json import decode

#: Namespaces a project borrows to back-fill the BCL (``IsExternalInit``); not part of its design.
_POLYFILL = ("System.", "Microsoft.")
_SKIP_DIRS = frozenset({"bin", "obj", "temp", ".worktrees", "node_modules"})
_DECL = re.compile(r"\b(?P<kind>class|struct|interface|enum|record(?:\s+struct|\s+class)?)\s+(?P<name>[A-Z]\w*)")
# A ``///`` block, any attribute lines, then the declaration it documents (matched on the raw text).
_DOC = re.compile(
    r"(?P<doc>(?:^[ \t]*///[^\n]*\n)+)(?:^[ \t]*\[[^\n]*\n)*^[^\n]*?"
    r"\b(?:class|struct|interface|enum|record(?:\s+struct|\s+class)?)\s+(?P<name>[A-Z]\w*)",
    re.MULTILINE,
)
_SUMMARY = re.compile(r"<summary>(?P<text>[\s\S]*?)</summary>")
_CREF = re.compile(
    r"<(?:see|seealso|paramref|typeparamref)\s+(?:cref|name|langword)=\"(?:[A-Z]:)?(?P<ref>[^\"]+)\"\s*/>"
)
_TAG = re.compile(r"</?[a-z][^>]*>")
_MODIFIERS = r"(?:(?:static|override|virtual|abstract|sealed|async|readonly|required|partial|new|unsafe|extern)\s+)*"
# ``public <type> Name(``, ``{``, ``=>`` or ``;``. The type group is checked so type declarations are not members.
_MEMBER = re.compile(
    rf"^[ \t]*public\s+{_MODIFIERS}(?P<type>[\w.?<>\[\],() ]+?)\s+(?P<name>[A-Z]\w*)\s*(?P<shape>\(|<|\{{|=>|;|=)",
    re.MULTILINE,
)
_TYPE_WORDS = frozenset({"class", "struct", "interface", "enum", "record", "delegate", "event", "const", "operator"})
_SUMMARY_CAP = 180
_MEMBER_CAP = 8
_NAMESPACE = re.compile(r"^\s*namespace\s+(?P<name>[\w.]+)", re.MULTILINE)
_IDENT = re.compile(r"\b[A-Z]\w*\b")
# ``namespace A.B;`` and ``using A.B;`` name namespaces, not types: a segment that matches a type is no reference.
_DIRECTIVE = re.compile(r"^\s*(?:global\s+)?(?:namespace|using)\s+(?!var\b)[\w.\s=]+;?\s*$", re.MULTILINE)
_GENERATOR = re.compile(r":\s*(?:[\w.]+\.)?IIncrementalGenerator\b")
# Comments and string literals: raw strings first (they may contain quotes), then verbatim, then plain.
_NOISE = re.compile(r'"""[\s\S]*?"""|@"(?:[^"]|"")*"|"(?:\\.|[^"\\\n])*"|\'(?:\\.|[^\'\\\n])\'|//[^\n]*|/\*[\s\S]*?\*/')


@dataclass(frozen=True, slots=True)
class ProjectRef:
    target: Path
    analyzer: bool


@dataclass(frozen=True, slots=True)
class Project:
    """One ``.csproj``. ``root_namespace`` defaults to the project name, as MSBuild does."""

    name: str
    path: Path
    root_namespace: str
    frameworks: tuple[str, ...]
    is_analyzer: bool
    refs: tuple[ProjectRef, ...]
    compile_links: tuple[Path, ...]

    @property
    def directory(self) -> Path:
        return self.path.parent


@dataclass(frozen=True, slots=True)
class TypeInfo:
    """One declared type: its kind (``class``, ``record``, ...), the first sentence of its ``<summary>``, and its
    public members (``Name()`` for a method, ``Name`` otherwise). Members are only read when the file declares
    this one type, so they are never attributed to the wrong one."""

    name: str
    kind: str
    summary: str
    members: tuple[str, ...]


@dataclass(frozen=True, slots=True)
class SourceFile:
    path: Path
    namespace: str
    declares: tuple[str, ...]
    names: frozenset[str]
    is_generator: bool
    lines: int = 0
    types: tuple[TypeInfo, ...] = ()


@dataclass(frozen=True, slots=True)
class Component:
    """A namespace's share of one project. ``key`` is the namespace relative to the project root namespace."""

    key: str
    namespace: str
    files: tuple[SourceFile, ...]

    @property
    def types(self) -> tuple[str, ...]:
        return tuple(sorted({t for f in self.files for t in f.declares}))


def _msbuild_path(base: Path, raw: str) -> Path:
    """An MSBuild path uses backslashes on every platform."""
    return (base / PurePosixPath(*PureWindowsPath(raw).parts)).resolve()


def _msbuild_glob(base: Path, raw: str) -> list[Path]:
    """``..\\Other\\Models\\*.cs`` or ``Shared\\**\\*.cs``: the fixed directory part, then a glob below it."""
    if "*" not in raw:
        return [_msbuild_path(base, raw)]
    parts = PureWindowsPath(raw).parts
    fixed = next(i for i, part in enumerate(parts) if "*" in part)
    directory = _msbuild_path(base, str(PureWindowsPath(*parts[:fixed]))) if fixed else base.resolve()
    return sorted(p.resolve() for p in directory.glob("/".join(parts[fixed:])) if p.is_file())


@dataclass(frozen=True, slots=True)
class _ItemAttrs:
    """The attributes of an MSBuild item (``<ProjectReference>``, ``<Compile>``) that the scan reads."""

    include: Annotated[str, Field(alias="Include")] = ""
    output_item_type: Annotated[str, Field(alias="OutputItemType")] = ""


def _items(root: ElementTree.Element, tag: str) -> list[_ItemAttrs]:
    """Every ``<tag>`` item with an ``Include``."""
    return [a for item in root.iter(tag) if (a := decode(_ItemAttrs, item.attrib)).include]


@dataclass(frozen=True, slots=True, order=True)
class Edge:
    """A relationship from one owner (a namespace alias or a neighbouring project) to another."""

    source: str
    target: str


def _text(root: ElementTree.Element, tag: str) -> str | None:
    found = root.find(f".//{tag}")
    return found.text.strip() if found is not None and found.text else None


def read_project(path: Path) -> Project:
    root = SafeTree.parse(path).getroot()
    if root is None:
        raise ValueError(f"{path}: empty project file")
    frameworks = _text(root, "TargetFrameworks") or _text(root, "TargetFramework") or ""
    refs = tuple(
        ProjectRef(_msbuild_path(path.parent, item.include), item.output_item_type == "Analyzer")
        for item in _items(root, "ProjectReference")
    )
    links = tuple(found for item in _items(root, "Compile") for found in _msbuild_glob(path.parent, item.include))
    extended = (_text(root, "EnforceExtendedAnalyzerRules") or "").lower() == "true"
    roslyn = (_text(root, "IsRoslynComponent") or "").lower() == "true"
    return Project(
        name=path.stem,
        path=path.resolve(),
        root_namespace=_text(root, "RootNamespace") or path.stem,
        frameworks=tuple(f for f in frameworks.split(";") if f),
        is_analyzer=extended or roslyn,
        refs=refs,
        compile_links=links,
    )


def discover(repo: Path, roots: tuple[str, ...] = ("src",)) -> tuple[Project, ...]:
    """Every project under ``roots``, plus whatever they reference, transitively. Sorted by name."""
    pending = [p for r in roots for p in sorted((repo / r).rglob("*.csproj")) if not _skipped(p, repo)]
    found: dict[Path, Project] = {}
    while pending:
        path = pending.pop().resolve()
        if path in found or not path.is_file():
            continue
        project = read_project(path)
        found[path] = project
        pending.extend(ref.target for ref in project.refs)
    return tuple(sorted(found.values(), key=lambda p: p.name))


def _skipped(path: Path, repo: Path) -> bool:
    return any(part in _SKIP_DIRS for part in path.relative_to(repo).parts)


def _own_sources(project: Project) -> list[Path]:
    return [p for p in project.directory.rglob("*.cs") if not _skipped(p, project.directory)]


def _summary(doc: str) -> str:
    """The first sentence of a ``<summary>``, with ``<see cref="T:A.B"/>`` read as ``B`` and other tags dropped."""
    text = "\n".join(line.strip().removeprefix("///") for line in doc.splitlines())
    found = _SUMMARY.search(text)
    if not found:
        return ""
    plain = _CREF.sub(lambda m: m.group("ref").rsplit(".", 1)[-1], found.group("text"))
    plain = " ".join(_TAG.sub("", plain).split())
    sentence = re.split(r"(?<=[.!?])\s", plain, maxsplit=1)[0]
    return sentence if len(sentence) <= _SUMMARY_CAP else sentence[: _SUMMARY_CAP - 1].rstrip() + "…"


def _members(code: str) -> tuple[str, ...]:
    seen: dict[str, None] = {}
    for m in _MEMBER.finditer(code):
        if set(m.group("type").split()) & _TYPE_WORDS:
            continue
        seen.setdefault(m.group("name") + ("()" if m.group("shape") in "(<" else ""), None)
    return tuple(seen)[:_MEMBER_CAP]


def read_source(path: Path) -> SourceFile:
    raw = path.read_text(encoding="utf-8", errors="replace")
    code = _NOISE.sub(" ", raw)
    namespace = _NAMESPACE.search(code)
    kinds: dict[str, str] = {}
    for m in _DECL.finditer(code):
        _ = kinds.setdefault(m.group("name"), m.group("kind").split()[0])
    docs = {m.group("name"): _summary(m.group("doc")) for m in _DOC.finditer(raw)}
    members = _members(code) if len(kinds) == 1 else ()
    return SourceFile(
        path=path,
        namespace=namespace.group("name") if namespace else "",
        declares=tuple(sorted(kinds)),
        names=frozenset(_IDENT.findall(_DIRECTIVE.sub(" ", code))),
        is_generator=bool(_GENERATOR.search(code)),
        lines=len(raw.splitlines()),
        types=tuple(TypeInfo(n, kinds[n], docs.get(n, ""), members) for n in sorted(kinds)),
    )


def sources(project: Project) -> tuple[SourceFile, ...]:
    """The project's own ``.cs`` files plus any it compiles in by ``<Compile Include=... Link=...>``."""
    paths = {*(_own_sources(project)), *(p for p in project.compile_links if p.is_file())}
    return tuple(read_source(p) for p in sorted(paths))


def component_key(project: Project, namespace: str) -> str:
    """``Parquet.SourceGenerator.Emitter.Compound`` in a project rooted at ``Parquet.SourceGenerator`` is
    ``Emitter.Compound``; the root namespace itself is ``""``."""
    root = project.root_namespace
    if namespace == root:
        return ""
    return namespace.removeprefix(root + ".") if namespace.startswith(root + ".") else namespace


def components(project: Project, files: tuple[SourceFile, ...]) -> tuple[Component, ...]:
    by_ns: dict[str, list[SourceFile]] = {}
    for f in files:
        if f.declares and not f.namespace.startswith(_POLYFILL):
            by_ns.setdefault(f.namespace, []).append(f)
    return tuple(Component(component_key(project, ns), ns, tuple(group)) for ns, group in sorted(by_ns.items()))


TypeIndex = dict[str, str]
"""Type name to the owner key that declares it. A name declared in two owners is dropped: it is ambiguous."""


def type_index(owners: dict[str, tuple[str, ...]]) -> TypeIndex:
    seen: dict[str, set[str]] = {}
    for owner, types in owners.items():
        for name in types:
            seen.setdefault(name, set()).add(owner)
    return {name: next(iter(o)) for name, o in seen.items() if len(o) == 1}


def reference_edges(owned: dict[str, tuple[SourceFile, ...]], index: TypeIndex) -> Counter[Edge]:
    """Pathway 1. ``(owner, other)`` counted once per distinct type of ``other`` that ``owner`` names."""
    edges: Counter[Edge] = Counter()
    for owner, files in owned.items():
        named = {n for f in files for n in f.names}
        for name in sorted(named):
            target = index.get(name)
            if target is not None and target != owner:
                edges[Edge(owner, target)] += 1
    return edges


def _type_of(member: str) -> str:
    """``A.B.Type.Method/(args)`` is declared on ``A.B.Type``, and so is its constructor ``A.B.Type..ctor/()``."""
    head = member.split("/", 1)[0]
    for special in ("..ctor", "..cctor"):
        if head.endswith(special):
            return head.removesuffix(special)
    return head.rsplit(".", 1)[0]


def call_edges(path: Path, owner_of: dict[str, str]) -> Counter[Edge]:
    """Pathway 2. ``owner_of`` maps a fully qualified type to its owner; calls inside one owner are dropped."""
    edges: Counter[Edge] = Counter()
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.startswith("E:") or " | " not in line:  # T:, M: and X: records describe, they do not call
            continue
        caller, callee = line[2:].split(" | ", 1)
        source, target = owner_of.get(_type_of(caller)), owner_of.get(_type_of(callee.strip()))
        if source is not None and target is not None and source != target:
            edges[Edge(source, target)] += 1
    return edges

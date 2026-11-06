"""The compiler's view of a .NET solution, read from the files ``dotnet/callgraph.cs`` writes.

The text scan in :mod:`rig.c4_okf.dotnet_scan` sees names; this sees what they bind to. One
``<Project>.callgraph.txt`` per project, one record per line:

- ``T:<Ns>.<Type>|<kind>|<access>|<path>:<line>``: a declared type (nested types sit in their namespace);
- ``M:<Ns>.<Type>.<Member>/(<params>)|<kind>|<access>|<line>``: a declared member;
- ``E:<caller> | <callee>``: a call, a ``new``, or a method group passed as a delegate;
- ``X:<interface or base member> | <implementation>``: where a call through an abstraction lands.

Members are read by name, not by signature: overloads are one node, because a reader follows "who calls
``Run``", not which overload.
"""

from __future__ import annotations

from collections.abc import Iterable
from dataclasses import dataclass, field
from pathlib import Path

#: Kinds drawn as nodes. Fields, constants and enum values are listed in the notes, not drawn.
CALLABLE = frozenset({"method", "ctor", "property", "operator"})


@dataclass(frozen=True, slots=True, order=True)
class MemberKey:
    """A member by type and name: ``Ns.StatsRunner`` + ``Run``. A constructor is ``new``."""

    type: str
    name: str


@dataclass(frozen=True, slots=True)
class TypeDecl:
    id: str
    kind: str
    access: str
    path: str
    line: int

    @property
    def namespace(self) -> str:
        return self.id.rsplit(".", 1)[0] if "." in self.id else ""

    @property
    def name(self) -> str:
        return self.id.rsplit(".", 1)[-1]


@dataclass(frozen=True, slots=True)
class MemberDecl:
    key: MemberKey
    kind: str
    access: str
    line: int
    signature: str


@dataclass(slots=True)
class CallGraph:
    types: dict[str, TypeDecl] = field(default_factory=dict)
    members: dict[MemberKey, list[MemberDecl]] = field(default_factory=dict)
    calls: set[tuple[MemberKey, MemberKey]] = field(default_factory=set)
    implements: set[tuple[MemberKey, MemberKey]] = field(default_factory=set)

    @classmethod
    def load(cls, directory: Path, projects: Iterable[str]) -> CallGraph | None:
        """The union of the named projects' files; ``None`` when none of them is there."""
        graph = cls()
        found = False
        for name in sorted(set(projects)):
            path = directory / f"{name}.callgraph.txt"
            if path.is_file():
                found = True
                graph.read(path.read_text(encoding="utf-8").splitlines())
        return graph if found else None

    def read(self, lines: Iterable[str]) -> None:
        for line in lines:
            tag, _, rest = line.partition(":")
            if tag == "T":
                ident, kind, access, where = rest.split("|")
                path, _, at = where.rpartition(":")
                _ = self.types.setdefault(ident, TypeDecl(ident, kind, access, path, int(at)))
            elif tag == "M":
                ident, kind, access, at = rest.split("|")
                key = member_key(ident)
                decl = MemberDecl(key, kind, access, int(at), ident.split("/", 1)[1])
                same = self.members.setdefault(key, [])
                if decl not in same:
                    same.append(decl)
            elif tag in ("E", "X"):
                source, _, target = rest.partition(" | ")
                pair = (member_key(source), member_key(target))
                (self.calls if tag == "E" else self.implements).add(pair)

    def callable(self, key: MemberKey) -> bool:
        return any(d.kind in CALLABLE for d in self.members.get(key, ()))

    def of_type(self, type_id: str) -> list[MemberKey]:
        return sorted(k for k in self.members if k.type == type_id)

    def label(self, key: MemberKey) -> str:
        """``Run()`` for something called, ``Count`` for a property or field."""
        kinds = {d.kind for d in self.members.get(key, ())}
        return key.name + ("()" if kinds & {"method", "ctor", "operator"} or not kinds else "")


def member_key(ident: str) -> MemberKey:
    """``A.B.Type.Run/(int)`` is ``Run`` on ``A.B.Type``; ``A.B.Type..ctor/()`` is ``new``."""
    head = ident.partition("/")[0]
    for special, shown in ((".ctor", "new"), (".cctor", "static new")):
        if head.endswith("." + special):
            return MemberKey(head.removesuffix("." + special), shown)
    owner, _, name = head.rpartition(".")
    return MemberKey(owner, name)


def type_of(ident: str) -> str:
    return member_key(ident).type

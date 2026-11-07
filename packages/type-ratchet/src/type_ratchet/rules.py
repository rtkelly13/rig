"""The rules and the per-module counter that finds them."""

from __future__ import annotations

import ast
import io
import re
import tokenize
from collections import Counter
from collections.abc import Iterator
from dataclasses import dataclass, field
from enum import StrEnum
from typing import override

DEFAULT_LONG_MODULE_LINES = 800

_TYPE_IGNORE = re.compile(r"#\s*(type|pyright):\s*ignore")
_NOQA = re.compile(r"#\s*noqa\b", re.IGNORECASE)
_PYRIGHT_DIRECTIVE = re.compile(r"#\s*pyright:\s*(?P<body>.*)")
_RELAXED_MODES = frozenset({"basic", "standard", "off"})
_RELAXED_LEVELS = frozenset({"false", "none", "hint", "information", "warning"})


class Rule(StrEnum):
    FIXED_TUPLE = "fixed-tuple"
    CAST = "cast"
    IGNORE_COMMENT = "ignore-comment"
    NOQA = "noqa"
    GETATTR_FAMILY = "getattr-family"
    LONG_MODULE = "long-module"
    STR_KEY_GET = "str-key-get"
    STR_KEY_SUBSCRIPT = "str-key-subscript"
    ISINSTANCE_DICT = "isinstance-dict"
    JSON_LOADS = "json-loads"
    YAML_LOAD = "yaml-load"
    PYRIGHT_PRAGMA = "pyright-pragma"
    LOOSE_DICT = "loose-dict"


BOUNDARY_RULES: frozenset[Rule] = frozenset({Rule.JSON_LOADS, Rule.YAML_LOAD, Rule.ISINSTANCE_DICT, Rule.LOOSE_DICT})
"""Rules a designated boundary module may break: it is where raw parsed data is turned into typed shapes."""

_JSON_LOADERS = frozenset({"json.loads", "json.load"})
_YAML_LOADERS = frozenset(
    f"yaml.{name}"
    for name in (
        "load",
        "safe_load",
        "full_load",
        "unsafe_load",
        "load_all",
        "safe_load_all",
        "full_load_all",
        "unsafe_load_all",
    )
)
_ENVIRON = frozenset({"os.environ"})
_LOOSE_MAPS = frozenset(
    {
        "dict",
        "typing.Dict",
        "typing.Mapping",
        "typing.MutableMapping",
        "collections.abc.Mapping",
        "collections.abc.MutableMapping",
    }
)


@dataclass(slots=True)
class _Visitor(ast.NodeVisitor):
    aliases: dict[str, str]
    bindings: dict[str, frozenset[str]] = field(default_factory=dict[str, frozenset[str]])
    str_key_receivers: frozenset[str] = frozenset()
    loose_dict_aliases: frozenset[str] = frozenset()
    counts: Counter[Rule] = field(default_factory=Counter[Rule])
    _in_alias_definition: bool = False

    def _is_map_receiver(self, node: ast.expr) -> bool:
        """A receiver read by string key because it is a homogeneous map (the environment, or configured)."""
        name = self._name(node)
        if not name:
            return False
        if name in _ENVIRON or name in self.str_key_receivers:
            return True
        _, dot, attr = name.rpartition(".")
        return bool(dot) and f"*.{attr}" in self.str_key_receivers

    def _loader(self, node: ast.expr) -> bool:
        """Any reference counts, not just a call, so aliasing `json.loads` to a variable does not hide it.

        A local name imported more than once (say in two functions) is checked against every import that binds it.
        """
        head, _, rest = _dotted_name(node).partition(".")
        names = {f"{target}.{rest}" if rest else target for target in self.bindings.get(head, {head})}
        if names & _JSON_LOADERS:
            self.counts[Rule.JSON_LOADS] += 1
            return True
        if names & _YAML_LOADERS:
            self.counts[Rule.YAML_LOAD] += 1
            return True
        return False

    @override
    def visit_Name(self, node: ast.Name) -> None:
        self._loose_alias(node)
        self._loader(node)

    @override
    def visit_Attribute(self, node: ast.Attribute) -> None:
        self._loose_alias(node)
        if not self._loader(node):
            self.generic_visit(node)

    def _loose_alias(self, node: ast.Name | ast.Attribute) -> None:
        """A read of a configured loose-dict alias, however it is imported or qualified."""
        if isinstance(node.ctx, ast.Load) and self._name(node).rpartition(".")[2] in self.loose_dict_aliases:
            self.counts[Rule.LOOSE_DICT] += 1

    def _is_loose_dict(self, node: ast.AST) -> bool:
        """`dict[str, object]`, `Mapping[str, object]` or `MutableMapping[str, object]`, however it is imported."""
        if self._in_alias_definition or not isinstance(node, ast.Subscript):
            return False
        name = self._name(node.value)
        if name not in _LOOSE_MAPS and f"typing.{name}" not in _LOOSE_MAPS:
            return False
        elts = node.slice.elts if isinstance(node.slice, ast.Tuple) else []
        return len(elts) == 2 and _dotted_name(elts[0]) == "str" and _dotted_name(elts[1]) == "object"

    def _defines_loose_alias(self, target: ast.expr) -> bool:
        return isinstance(target, ast.Name) and target.id in self.loose_dict_aliases

    def _visit_definition(self, node: ast.AST, defines_alias: bool) -> None:
        """The value of a configured alias's own definition is not counted: that is where the alias is declared."""
        outer = self._in_alias_definition
        self._in_alias_definition = outer or defines_alias
        self.generic_visit(node)
        self._in_alias_definition = outer

    @override
    def visit_Assign(self, node: ast.Assign) -> None:
        self._visit_definition(node, any(self._defines_loose_alias(target) for target in node.targets))

    def _name(self, node: ast.expr) -> str:
        """Dotted name with its first segment resolved through the module's imports."""
        head, _, rest = _dotted_name(node).partition(".")
        resolved = self.aliases.get(head, head)
        return f"{resolved}.{rest}" if rest else resolved

    @override
    def visit_FunctionDef(self, node: ast.FunctionDef) -> None:
        self._function(node)

    @override
    def visit_AsyncFunctionDef(self, node: ast.AsyncFunctionDef) -> None:
        self._function(node)

    def _function(self, node: ast.FunctionDef | ast.AsyncFunctionDef) -> None:
        args = node.args
        for arg in [*args.posonlyargs, *args.args, *args.kwonlyargs, args.vararg, args.kwarg]:
            if arg is not None:
                self._annotation(arg.annotation)
        self._annotation(node.returns)
        self.generic_visit(node)

    @override
    def visit_AnnAssign(self, node: ast.AnnAssign) -> None:
        defines_alias = self._defines_loose_alias(node.target)
        if not defines_alias:
            self._annotation(node.annotation)
        self._visit_definition(node, defines_alias)

    @override
    def visit_TypeAlias(self, node: ast.TypeAlias) -> None:
        defines_alias = self._defines_loose_alias(node.name)
        self._visit_definition(node, defines_alias)
        if not defines_alias:
            self._annotation(node.value)

    @override
    def visit_ClassDef(self, node: ast.ClassDef) -> None:
        if any(self._name(base) in {"NamedTuple", "typing.NamedTuple"} for base in node.bases):
            self.counts[Rule.FIXED_TUPLE] += 1
        self.generic_visit(node)

    @override
    def visit_Call(self, node: ast.Call) -> None:
        name = self._name(node.func)
        if name in {"cast", "typing.cast"}:
            self.counts[Rule.CAST] += 1
        elif name in {"getattr", "hasattr", "setattr"}:
            self.counts[Rule.GETATTR_FAMILY] += 1
        elif name in {"namedtuple", "collections.namedtuple", "NamedTuple", "typing.NamedTuple"}:
            self.counts[Rule.FIXED_TUPLE] += 1
        elif name == "isinstance" and len(node.args) == 2 and _mentions_dict(node.args[1]):
            self.counts[Rule.ISINSTANCE_DICT] += 1
        elif (
            isinstance(node.func, ast.Attribute)
            and node.func.attr == "get"
            and _is_str_constant(node.args[:1])
            and not self._is_map_receiver(node.func.value)
        ):
            self.counts[Rule.STR_KEY_GET] += 1
        self.generic_visit(node)

    @override
    def visit_Subscript(self, node: ast.Subscript) -> None:
        """Only reads count: a write such as `out["k"] = v` builds a payload rather than consuming a record."""
        is_read = isinstance(node.ctx, ast.Load)
        is_literal = self._name(node.value) in {"Literal", "typing.Literal"}
        is_str_key = isinstance(node.slice, ast.Constant) and isinstance(node.slice.value, str)
        if is_read and is_str_key and not is_literal and not self._is_map_receiver(node.value):
            self.counts[Rule.STR_KEY_SUBSCRIPT] += 1
        if self._is_loose_dict(node):
            self.counts[Rule.LOOSE_DICT] += 1
        self.generic_visit(node)

    def _annotation(self, node: ast.expr | None, quoted: bool = False) -> None:
        """Annotation-only rules; inside a quoted annotation the loose-dict check runs too, as no visitor reaches it."""
        if node is None:
            return
        for sub in ast.walk(node):
            if isinstance(sub, ast.Constant) and isinstance(sub.value, str):
                self._annotation(_parse_quoted(sub.value), quoted=True)
            elif self._is_fixed_tuple(sub):
                self.counts[Rule.FIXED_TUPLE] += 1
            if quoted and (self._is_loose_dict(sub) or self._is_quoted_alias(sub)):
                self.counts[Rule.LOOSE_DICT] += 1

    def _is_quoted_alias(self, node: ast.AST) -> bool:
        return isinstance(node, ast.Name | ast.Attribute) and (
            self._name(node).rpartition(".")[2] in self.loose_dict_aliases
        )

    def _is_fixed_tuple(self, node: ast.AST) -> bool:
        """`tuple[A, B]` and `Tuple[A]` are shapes; `tuple[T, ...]` is a sequence and allowed."""
        if not isinstance(node, ast.Subscript):
            return False
        if self._name(node.value) not in {"tuple", "Tuple", "typing.Tuple"}:
            return False
        elts = node.slice.elts if isinstance(node.slice, ast.Tuple) else [node.slice]
        is_homogeneous = len(elts) == 2 and isinstance(elts[1], ast.Constant) and elts[1].value is Ellipsis
        return not is_homogeneous


def _parse_quoted(annotation: str) -> ast.expr | None:
    try:
        return ast.parse(annotation, mode="eval").body
    except SyntaxError:
        return None


def _import_bindings(tree: ast.Module) -> dict[str, frozenset[str]]:
    """Every fully qualified name each locally bound import name refers to, across all scopes."""
    bindings: dict[str, set[str]] = {}
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            for alias in node.names:
                bindings.setdefault(alias.asname or alias.name.partition(".")[0], set[str]()).add(
                    alias.name if alias.asname else alias.name.partition(".")[0]
                )
        elif isinstance(node, ast.ImportFrom) and node.module and not node.level:
            for alias in node.names:
                bindings.setdefault(alias.asname or alias.name, set[str]()).add(f"{node.module}.{alias.name}")
    return {name: frozenset(targets) for name, targets in bindings.items()}


def _import_aliases(tree: ast.Module) -> dict[str, str]:
    """Map each locally bound import name to the fully qualified name it refers to."""
    aliases: dict[str, str] = {}
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            for alias in node.names:
                if alias.asname:
                    aliases[alias.asname] = alias.name
        elif isinstance(node, ast.ImportFrom) and node.module and not node.level:
            for alias in node.names:
                aliases[alias.asname or alias.name] = f"{node.module}.{alias.name}"
    return aliases


def _dotted_name(node: ast.expr) -> str:
    if isinstance(node, ast.Name):
        return node.id
    if isinstance(node, ast.Attribute):
        prefix = _dotted_name(node.value)
        return f"{prefix}.{node.attr}" if prefix else ""
    return ""


def _is_str_constant(args: list[ast.expr]) -> bool:
    return bool(args) and isinstance(args[0], ast.Constant) and isinstance(args[0].value, str)


def _mentions_dict(node: ast.expr) -> bool:
    if isinstance(node, ast.Tuple):
        return any(_mentions_dict(elt) for elt in node.elts)
    return _dotted_name(node) == "dict"


def _comments(source: str) -> Iterator[tokenize.TokenInfo]:
    try:
        for tok in tokenize.generate_tokens(io.StringIO(source).readline):
            if tok.type == tokenize.COMMENT:
                yield tok
    except (tokenize.TokenError, SyntaxError):
        return


def _relaxes_checking(comment: tokenize.TokenInfo) -> bool:
    """A file-level `# pyright:` directive that switches a mode down or a rule off; `# pyright: ignore` is not one."""
    match = _PYRIGHT_DIRECTIVE.match(comment.string)
    if match is None or comment.line[: comment.start[1]].strip():
        return False
    for item in match.group("body").split(","):
        name, eq, level = (part.strip().lower() for part in item.partition("="))
        if (not eq and name in _RELAXED_MODES) or (eq and level in _RELAXED_LEVELS):
            return True
    return False


def count_source(
    source: str,
    str_key_receivers: frozenset[str] = frozenset(),
    long_module_lines: int = DEFAULT_LONG_MODULE_LINES,
    loose_dict_aliases: frozenset[str] = frozenset(),
) -> Counter[Rule]:
    """Count every rule's occurrences in one module's source."""
    tree = ast.parse(source)
    visitor = _Visitor(_import_aliases(tree), _import_bindings(tree), str_key_receivers, loose_dict_aliases)
    visitor.visit(tree)
    counts = visitor.counts
    for comment in _comments(source):
        if _TYPE_IGNORE.search(comment.string):
            counts[Rule.IGNORE_COMMENT] += 1
        elif _relaxes_checking(comment):
            counts[Rule.PYRIGHT_PRAGMA] += 1
        if _NOQA.search(comment.string):
            counts[Rule.NOQA] += 1
    if len(source.splitlines()) > long_module_lines:
        counts[Rule.LONG_MODULE] = 1
    return counts

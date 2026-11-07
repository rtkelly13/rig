"""Read the ratchet's settings from the target repo's pyproject.toml.

`[tool.type-ratchet]` is the table; `[tool.repo-health.ratchet]` is still read when it is absent.
"""

from __future__ import annotations

import tomllib
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import TypeGuard

from type_ratchet.rules import DEFAULT_LONG_MODULE_LINES, Rule

TABLE: tuple[str, ...] = ("tool", "type-ratchet")
LEGACY_TABLE: tuple[str, ...] = ("tool", "repo-health", "ratchet")
DEFAULT_PATHS: tuple[str, ...] = ("src",)
DEFAULT_HINT = "Remove them."

_PATHS = "paths"
_DISABLE = "disable"
_LONG_MODULE_LINES = "long-module-lines"
_PRAGMA_ALLOW = "pyright-pragma-allow"
_BOUNDARY_MODULES = "boundary-modules"
_STR_KEY_RECEIVERS = "str-key-receivers"
_LOOSE_DICT_ALIASES = "loose-dict-aliases"
_LOOSE_DICT_ALLOW = "loose-dict-allow"
_HINT = "hint"
_KEYS = frozenset(
    {
        _PATHS,
        _DISABLE,
        _LONG_MODULE_LINES,
        _PRAGMA_ALLOW,
        _BOUNDARY_MODULES,
        _STR_KEY_RECEIVERS,
        _LOOSE_DICT_ALIASES,
        _LOOSE_DICT_ALLOW,
        _HINT,
    }
)


class ConfigError(ValueError):
    """The ratchet table in pyproject.toml is malformed."""


@dataclass(frozen=True, slots=True, kw_only=True)
class RatchetConfig:
    """Which files the ratchet checks, which rules it gates, and the exemptions."""

    paths: tuple[str, ...] = DEFAULT_PATHS
    """Repo-relative directories whose git-tracked `*.py` files are checked."""
    disabled_rules: frozenset[Rule] = frozenset()
    """Rules that are counted for `--report` but never fail the gate."""
    long_module_lines: int = DEFAULT_LONG_MODULE_LINES
    """A module with more lines than this breaks `long-module`."""
    pyright_pragma_allow: frozenset[str] = frozenset()
    """Repo-relative paths that may keep a file-level pyright pragma."""
    boundary_modules: frozenset[str] = frozenset()
    """Repo-relative paths allowed to parse raw JSON/YAML and narrow it with `isinstance(x, dict)`."""
    str_key_receivers: frozenset[str] = frozenset()
    """Dotted receivers that are homogeneous string-keyed maps, not records; `*.attr` matches any `x.attr`."""
    loose_dict_aliases: frozenset[str] = frozenset()
    """Type alias names that `loose-dict` counts like `dict[str, object]`, wherever they are referenced."""
    loose_dict_allow: tuple[str, ...] = ()
    """Repo-relative paths or `fnmatch` globs that may keep loose dicts: output serializers and config round-trips."""
    hint: str = DEFAULT_HINT
    """Printed after the violations, typically a pointer to the repo's own guidance."""


def _is_table(value: object) -> TypeGuard[dict[str, object]]:
    return isinstance(value, dict)


def _is_array(value: object) -> TypeGuard[list[object]]:
    return isinstance(value, list)


def _find_table(document: dict[str, object], path: Sequence[str]) -> dict[str, object] | None:
    node: object = document
    for key in path:
        if not _is_table(node):
            return None
        node = node.get(key)
    if node is None:
        return None
    if not _is_table(node):
        raise ConfigError(f"[{'.'.join(path)}] must be a table")
    return node


def _strings(table: dict[str, object], key: str) -> tuple[str, ...] | None:
    value = table.get(key)
    if value is None:
        return None
    if not _is_array(value):
        raise ConfigError(f"{key} must be a list of strings")
    items = tuple(item for item in value if isinstance(item, str))
    if len(items) != len(value):
        raise ConfigError(f"{key} must be a list of strings")
    return items


def _rules(table: dict[str, object]) -> frozenset[Rule]:
    names = _strings(table, _DISABLE) or ()
    known = {rule.value: rule for rule in Rule}
    unknown = sorted(set(names) - known.keys())
    if unknown:
        raise ConfigError(f"{_DISABLE} has unknown rules: {', '.join(unknown)}")
    return frozenset(known[name] for name in names)


def _positive_int(table: dict[str, object], key: str, default: int) -> int:
    value = table.get(key, default)
    if not isinstance(value, int) or isinstance(value, bool) or value < 1:
        raise ConfigError(f"{key} must be a positive integer")
    return value


def _string(table: dict[str, object], key: str, default: str) -> str:
    value = table.get(key, default)
    if not isinstance(value, str):
        raise ConfigError(f"{key} must be a string")
    return value


def parse_config(document: dict[str, object]) -> RatchetConfig:
    """Build the config from a parsed pyproject document; a missing table means the defaults."""
    table = _find_table(document, TABLE)
    if table is None:
        table = _find_table(document, LEGACY_TABLE)
    elif _find_table(document, LEGACY_TABLE) is not None:
        raise ConfigError(f"set only one of [{'.'.join(TABLE)}] and [{'.'.join(LEGACY_TABLE)}]")
    if table is None:
        return RatchetConfig()
    unknown = sorted(table.keys() - _KEYS)
    if unknown:
        raise ConfigError(f"unknown keys: {', '.join(unknown)}")
    paths = _strings(table, _PATHS)
    if paths is not None and not paths:
        raise ConfigError(f"{_PATHS} must not be empty")
    return RatchetConfig(
        paths=paths or DEFAULT_PATHS,
        disabled_rules=_rules(table),
        long_module_lines=_positive_int(table, _LONG_MODULE_LINES, DEFAULT_LONG_MODULE_LINES),
        pyright_pragma_allow=frozenset(_strings(table, _PRAGMA_ALLOW) or ()),
        boundary_modules=frozenset(_strings(table, _BOUNDARY_MODULES) or ()),
        str_key_receivers=frozenset(_strings(table, _STR_KEY_RECEIVERS) or ()),
        loose_dict_aliases=frozenset(_strings(table, _LOOSE_DICT_ALIASES) or ()),
        loose_dict_allow=_strings(table, _LOOSE_DICT_ALLOW) or (),
        hint=_string(table, _HINT, DEFAULT_HINT),
    )


def load_config(pyproject: Path) -> RatchetConfig:
    """Read the ratchet table from a pyproject.toml; a missing file or table means the defaults."""
    if not pyproject.is_file():
        return RatchetConfig()
    try:
        document = tomllib.loads(pyproject.read_text(encoding="utf-8"))
    except tomllib.TOMLDecodeError as exc:
        raise ConfigError(f"{pyproject}: {exc}") from exc
    return parse_config(document)

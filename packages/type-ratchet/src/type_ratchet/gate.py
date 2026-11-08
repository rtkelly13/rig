"""Measure a repo and turn the counts into violations."""

from __future__ import annotations

import subprocess
from collections.abc import Callable
from dataclasses import dataclass
from fnmatch import fnmatchcase
from pathlib import Path

from type_ratchet.config import RatchetConfig
from type_ratchet.rules import BOUNDARY_RULES, Rule, count_source

type FileCounts = dict[str, int]
type RuleCounts = dict[Rule, FileCounts]


@dataclass(slots=True, frozen=True)
class Violation:
    rule: Rule
    path: str
    count: int


def _pathspec(path: str) -> str:
    prefix = path.strip().strip("/")
    return "*.py" if prefix in {"", "."} else f"{prefix}/*.py"


def tracked_files(root: Path, paths: tuple[str, ...]) -> list[Path]:
    """Git-tracked Python files under any of `paths` that still exist on disk (a deletion may be unstaged)."""
    specs = [_pathspec(path) for path in paths]
    r = subprocess.run(["git", "ls-files", "-z", "--", *specs], capture_output=True, text=True, cwd=root, check=True)
    found = (root / line for line in sorted(set(r.stdout.split("\0"))) if line)
    return [path for path in found if path.is_file()]


def _measured_files(root: Path, config: RatchetConfig) -> list[Path]:
    """Tracked files under `paths`, plus any tracked file a rule set's `files` glob matches."""
    files = set(tracked_files(root, config.paths))
    if config.rule_sets:
        files.update(
            path
            for path in tracked_files(root, (".",))
            if any(rule_set.matches(path.relative_to(root).as_posix()) for rule_set in config.rule_sets)
        )
    return sorted(files)


def measure(root: Path, config: RatchetConfig | None = None) -> RuleCounts:
    """Per-rule, per-file counts for every tracked Python file under the configured paths and rule sets."""
    settings = config or RatchetConfig()
    result: RuleCounts = {rule: {} for rule in Rule}
    for path in _measured_files(root, settings):
        rel = path.relative_to(root).as_posix()
        source = path.read_text(encoding="utf-8")
        counts = count_source(
            source, settings.str_key_receivers, settings.long_module_lines, settings.loose_dict_aliases
        )
        for rule, n in counts.items():
            if n:
                result[rule][rel] = n
    return result


_ALLOW_LISTS: dict[Rule, Callable[[RatchetConfig], tuple[str, ...]]] = {
    Rule.LOOSE_DICT: lambda config: config.loose_dict_allow,
    Rule.FAKE_LOCATION: lambda config: config.fake_location_allow,
    Rule.PATCHING: lambda config: config.patching_allow,
}
"""Rules a configured list of paths or globs may break."""


def _exempt(rule: Rule, path: str, config: RatchetConfig) -> bool:
    if rule not in config.gated_rules(path):
        return True
    if rule is Rule.PYRIGHT_PRAGMA:
        return path in config.pyright_pragma_allow
    allowed = _ALLOW_LISTS.get(rule)
    if allowed is not None and any(fnmatchcase(path, pattern) for pattern in allowed(config)):
        return True
    return rule in BOUNDARY_RULES and path in config.boundary_modules


def violations(actual: RuleCounts, config: RatchetConfig | None = None) -> tuple[Violation, ...]:
    """Every file with an unexempted occurrence, in rule then path order."""
    exemptions = config or RatchetConfig()
    return tuple(
        Violation(rule, path, count)
        for rule in sorted(Rule)
        for path, count in sorted(actual.get(rule, {}).items())
        if count and not _exempt(rule, path, exemptions)
    )

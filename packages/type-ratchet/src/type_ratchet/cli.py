"""The `ratchet` command."""

from __future__ import annotations

import argparse
import subprocess
import sys
from collections.abc import Sequence
from pathlib import Path

from type_ratchet.config import ConfigError, RatchetConfig, load_config
from type_ratchet.gate import RuleCounts, measure, violations
from type_ratchet.rules import Rule

EXIT_VIOLATIONS = 1
EXIT_USAGE = 2


class _Args(argparse.Namespace):
    report: bool = False
    root: Path | None = None


def _out(text: str) -> None:
    _ = sys.stdout.write(f"{text}\n")


def _err(text: str) -> None:
    _ = sys.stderr.write(f"ratchet: {text}\n")


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="ratchet", description="Fail if any gated type-soup smell appears in the configured paths."
    )
    _ = parser.add_argument("--report", action="store_true", help="Print totals for every rule.")
    _ = parser.add_argument(
        "--root", type=Path, help="Repo root holding pyproject.toml (default: the enclosing git work tree)."
    )
    return parser


def _repo_root() -> Path:
    r = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True)
    return Path(r.stdout.strip())


def _totals_table(actual: RuleCounts, config: RatchetConfig) -> str:
    width = max(len(rule.value) for rule in Rule) + len(" (disabled)")
    lines = ["Type-soup gate", f"{'Rule':<{width}}  {'Now':>5}"]
    for rule in Rule:
        label = f"{rule.value} (disabled)" if rule in config.disabled_rules else rule.value
        lines.append(f"{label:<{width}}  {sum(actual[rule].values()):>5}")
    return "\n".join(lines)


def run(argv: Sequence[str] | None = None) -> int:
    """Run the gate and return the process exit code."""
    args = _parser().parse_args(argv, namespace=_Args())
    try:
        root = (args.root or _repo_root()).resolve()
    except (subprocess.CalledProcessError, FileNotFoundError):
        _err("not inside a git work tree; pass --root.")
        return EXIT_USAGE
    try:
        config = load_config(root / "pyproject.toml")
    except ConfigError as exc:
        _err(str(exc))
        return EXIT_USAGE
    try:
        actual = measure(root, config)
    except (subprocess.CalledProcessError, FileNotFoundError):
        _err(f"{root} is not inside a git work tree, or git is not installed.")
        return EXIT_USAGE

    if args.report:
        _out(_totals_table(actual, config))

    found = violations(actual, config)
    for v in found:
        _out(f"{v.path}: {v.count} {v.rule.value}")
    if found:
        _out(config.hint)
        return EXIT_VIOLATIONS
    _out("No gated type soup.")
    return 0


def main() -> None:
    sys.exit(run())

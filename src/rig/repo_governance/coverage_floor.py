"""Coverage floor ratchet: the floor never falls, and it must follow coverage up to the target.

``[tool.coverage.report] fail_under`` is the floor that ``coverage report`` enforces. This adds
the three rules that make it a ratchet rather than a number someone set once:

- coverage may not drop below the floor;
- a pull request may not lower the floor (compared with the base branch's ``pyproject.toml``);
- once coverage climbs more than ``slack`` points above the floor, the floor must be raised,
  until it reaches ``target``.

Usage::

    python -m rig.repo_governance.coverage_floor coverage.json [--base-pyproject PATH]
"""

from __future__ import annotations

import argparse
import math
import pathlib
import sys
import tomllib
from collections.abc import Sequence
from dataclasses import dataclass, field

from pydantic import StrictFloat, StrictInt

from rig.typed_json import decode, decode_json


@dataclass(frozen=True, slots=True)
class _Report:
    fail_under: StrictFloat | StrictInt = 0


@dataclass(frozen=True, slots=True)
class _CoverageTool:
    report: _Report = field(default_factory=_Report)


@dataclass(frozen=True, slots=True)
class _RatchetConfig:
    target: StrictFloat | StrictInt = 95
    slack: StrictFloat | StrictInt = 2


@dataclass(frozen=True, slots=True)
class _Rig:
    coverage_ratchet: _RatchetConfig = field(default_factory=_RatchetConfig)


@dataclass(frozen=True, slots=True)
class _Tool:
    coverage: _CoverageTool = field(default_factory=_CoverageTool)
    rig: _Rig = field(default_factory=_Rig)


@dataclass(frozen=True, slots=True)
class _Pyproject:
    tool: _Tool = field(default_factory=_Tool)


@dataclass(frozen=True, slots=True)
class _Totals:
    percent_covered: StrictFloat | StrictInt = 0.0


@dataclass(frozen=True, slots=True)
class _CoverageJson:
    totals: _Totals = field(default_factory=_Totals)


@dataclass(frozen=True, slots=True)
class Ratchet:
    """The floor and where it is heading."""

    floor: float
    target: float
    slack: float


def read_ratchet(pyproject_text: str) -> Ratchet:
    tool = decode(_Pyproject, tomllib.loads(pyproject_text)).tool
    config = tool.rig.coverage_ratchet
    return Ratchet(floor=float(tool.coverage.report.fail_under), target=float(config.target), slack=float(config.slack))


def read_measured(coverage_json_text: str) -> float:
    return float(decode_json(_CoverageJson, coverage_json_text).totals.percent_covered)


def problems(measured: float, ratchet: Ratchet, base_floor: float | None = None) -> list[str]:
    """Every rule the measurement and floor break; empty when the ratchet holds."""
    found: list[str] = []
    floor, target = ratchet.floor, ratchet.target
    if floor > target:
        found.append(f"The floor {floor:g}% is above the target {target:g}%.")
    if base_floor is not None and floor < base_floor:
        found.append(f"The floor fell from {base_floor:g}% to {floor:g}%. It only rises.")
    if measured < floor:
        found.append(f"Coverage {measured:.2f}% is below the floor {floor:g}%. Add tests, don't lower the floor.")
    elif floor < target and measured - floor > ratchet.slack:
        raised = min(target, float(math.floor(measured)))
        found.append(
            f"Coverage {measured:.2f}% is more than {ratchet.slack:g} points above the floor {floor:g}%. "
            f"Raise [tool.coverage.report] fail_under to {raised:g}."
        )
    return found


class _Args(argparse.Namespace):
    coverage_json: pathlib.Path = pathlib.Path("coverage.json")
    pyproject: pathlib.Path = pathlib.Path("pyproject.toml")
    base_pyproject: pathlib.Path | None = None


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("coverage_json", type=pathlib.Path)
    parser.add_argument("--pyproject", type=pathlib.Path, default=pathlib.Path("pyproject.toml"))
    parser.add_argument("--base-pyproject", type=pathlib.Path, help="the base branch's pyproject.toml, if any")
    args = parser.parse_args(argv, namespace=_Args())
    coverage_json, pyproject, base = args.coverage_json, args.pyproject, args.base_pyproject

    ratchet = read_ratchet(pyproject.read_text(encoding="utf-8"))
    base_floor = read_ratchet(base.read_text(encoding="utf-8")).floor if base and base.is_file() else None
    measured = read_measured(coverage_json.read_text(encoding="utf-8"))

    found = problems(measured, ratchet, base_floor)
    for problem in found:
        sys.stdout.write(f"::error::{problem}\n")
    if not found:
        sys.stdout.write(f"Coverage {measured:.2f}% holds the floor {ratchet.floor:g}% (target {ratchet.target:g}%).\n")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main())

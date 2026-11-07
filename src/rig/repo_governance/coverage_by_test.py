"""Per-test coverage: how many lines each test executes, and how many no other test does.

Reads a ``.coverage`` file recorded with ``--cov-context=test``. For each test it reports:

- **covered**: distinct source lines the test executed (setup, call and teardown together);
- **unique**: of those, lines that no other test executed. Delete the test and these lines
  are no longer run by the suite.

A test with zero unique lines runs nothing another test does not; it is a candidate to merge or
sharpen, but check it kills no mutant the others miss before deleting it (docs/test-quality.md).
A line with exactly one test is where that test's assertions matter most.

``--group`` sets what counts as one test, and uniqueness is judged against the others at the
same level: ``item`` (each parametrized case), ``function`` (cases merged) or ``file``. Lines run
only while modules were imported carry no test context; they are counted once, as
``import_only``, and credited to no test.

**Definition lines are import-only too**, whichever test ran them: module-level statements, class
bodies, and ``def`` and decorator lines. They run once per process, when a module is first
imported, so under xdist they land on whichever test imported it first on its worker. They show
a module was loaded, never what a test checked. ``--keep-definitions`` credits them as recorded.

Usage::

    uv run pytest -n auto --cov=src --cov-context=test --cov-report=
    uv run python -m rig.repo_governance.coverage_by_test .coverage --csv temp/coverage-by-test.csv

Add ``--record-sizes temp/sizes.json`` to the pytest run and ``--sizes temp/sizes.json`` here to
split every number by style (example, property, bdd) and size (small, medium, large).
"""

from __future__ import annotations

import argparse
import ast
import csv
import pathlib
import statistics
import sys
from collections import Counter
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass
from typing import Literal

from coverage import CoverageData

from rig.repo_governance.size_probe import Row, largest, read_rows

Group = Literal["item", "function", "file"]
GROUPS: tuple[Group, ...] = ("item", "function", "file")

#: A source line: (file, line number).
Line = tuple[str, int]


@dataclass(frozen=True, slots=True)
class Footprint:
    """One test's footprint."""

    test: str
    covered: int
    unique: int


@dataclass(frozen=True, slots=True)
class Report:
    """Every test's footprint, and the lines no test is credited with."""

    tests: list[Footprint]
    #: Lines executed by at least one test.
    tested_lines: int
    #: Lines executed only at import time, outside any test.
    import_only: int


def owner(context: str, group: Group) -> str:
    """The test a pytest-cov context belongs to; "" for the import-time context.

    ``tests/a.py::test_x[1]|run`` is ``tests/a.py::test_x[1]`` as an item,
    ``tests/a.py::test_x`` as a function and ``tests/a.py`` as a file.
    """
    node = context.rsplit("|", 1)[0] if "|" in context else context
    if not node or group == "item":
        return node
    if group == "file":
        return node.split("::", 1)[0]
    return node.split("[", 1)[0]


def build(lines: Mapping[Line, Iterable[str]], group: Group = "item") -> Report:
    """Turn each line's contexts into per-test counts."""
    covered: Counter[str] = Counter()
    unique: Counter[str] = Counter()
    tested = import_only = 0
    for contexts in lines.values():
        tests = {owner(c, group) for c in contexts} - {""}
        if not tests:
            import_only += 1
            continue
        tested += 1
        covered.update(tests)
        if len(tests) == 1:
            unique.update(tests)
    stats = [Footprint(test=t, covered=n, unique=unique[t]) for t, n in covered.items()]
    stats.sort(key=lambda s: (-s.unique, -s.covered, s.test))
    return Report(tests=stats, tested_lines=tested, import_only=import_only)


def definition_lines(source: str) -> frozenset[int]:
    """Lines that run when a module is imported, not when its functions are called.

    Module-level statements, class bodies, ``def`` lines, decorators and default arguments.
    Function bodies are left out, however deeply nested.
    """
    found: set[int] = set()

    def visit(nodes: Iterable[ast.stmt]) -> None:
        for node in nodes:
            if isinstance(node, ast.FunctionDef | ast.AsyncFunctionDef):
                found.add(node.lineno)
                found.update(d.lineno for d in node.decorator_list)
                defaults = [*node.args.defaults, *node.args.kw_defaults]
                found.update(d.lineno for d in defaults if d is not None)
            elif isinstance(node, ast.ClassDef):
                found.add(node.lineno)
                found.update(d.lineno for d in node.decorator_list)
                visit(node.body)
            else:
                found.update(range(node.lineno, (node.end_lineno or node.lineno) + 1))

    visit(ast.parse(source).body)
    return frozenset(found)


def read_lines(
    data: CoverageData, root: pathlib.Path | None = None, *, keep_definitions: bool = False
) -> dict[Line, list[str]]:
    """Every measured line and the contexts that executed it; paths relative to *root*.

    Unless *keep_definitions*, a definition line keeps no test context (see the module docs).
    A source file that cannot be read keeps its contexts as recorded.
    """
    found: dict[Line, list[str]] = {}
    for filename in sorted(data.measured_files()):
        path = pathlib.Path(filename)
        name = path.relative_to(root).as_posix() if root and path.is_relative_to(root) else path.as_posix()
        definitions = frozenset[int]() if keep_definitions else _definitions_of(path)
        for lineno, contexts in data.contexts_by_lineno(filename).items():
            found[name, lineno] = [""] if lineno in definitions else contexts
    return found


def _definitions_of(path: pathlib.Path) -> frozenset[int]:
    try:
        return definition_lines(path.read_text(encoding="utf-8"))
    except (OSError, SyntaxError, UnicodeDecodeError):
        return frozenset()


@dataclass(frozen=True, slots=True)
class Kind:
    """A test's style and size, from a ``--record-sizes`` file (docs/test-quality.md)."""

    style: str
    size: str


_UNKNOWN = Kind("unknown", "unknown")


def kinds(report: Report, rows: Mapping[str, Row], group: Group) -> dict[str, Kind]:
    """Each test's kind. A grouped test takes the largest size of its cases, and its style is
    "mixed" when they differ. A test with no row is "unknown": it was skipped, or not recorded."""
    cases: dict[str, list[Row]] = {}
    for nodeid, row in rows.items():
        cases.setdefault(owner(nodeid, group), []).append(row)
    found: dict[str, Kind] = {}
    for test in report.tests:
        mine = cases.get(test.test, [])
        styles = {r.style for r in mine}
        found[test.test] = Kind(
            style=styles.pop() if len(styles) == 1 else ("mixed" if styles else "unknown"),
            size=largest([r.size for r in mine]) if mine else "unknown",
        )
    return found


def by_kind(report: Report, found: Mapping[str, Kind]) -> list[str]:
    """One line per style and size: how many tests, their median footprint, and how many own nothing."""
    groups: dict[Kind, list[Footprint]] = {}
    for test in report.tests:
        groups.setdefault(found.get(test.test, _UNKNOWN), []).append(test)
    lines = [f"{'style':<9} {'size':<8} {'tests':>6} {'covered':>8} {'unique':>7} {'no unique':>10}"]
    for kind, tests in sorted(groups.items(), key=lambda item: (item[0].style, item[0].size)):
        zero = sum(t.unique == 0 for t in tests)
        lines.append(
            f"{kind.style:<9} {kind.size:<8} {len(tests):>6} {statistics.median(t.covered for t in tests):>8g} "
            f"{statistics.median(t.unique for t in tests):>7g} {zero / len(tests):>10.0%}"
        )
    return lines


def summary(report: Report, group: Group) -> list[str]:
    """A short reading of the report, for a terminal or a job summary."""
    tests = report.tests
    if not tests:
        return ["No test contexts. Record with --cov-context=test."]
    zero = [t for t in tests if t.unique == 0]
    unique_total = sum(t.unique for t in tests)
    return [
        f"{len(tests)} tests ({group}) execute {report.tested_lines} lines; "
        f"{report.import_only} more run only at import.",
        f"{unique_total} lines ({unique_total / report.tested_lines:.0%}) are executed by exactly one test.",
        f"{len(zero)} tests ({len(zero) / len(tests):.0%}) execute no line that another test does not.",
        f"Median per test: {statistics.median(t.covered for t in tests):g} covered, "
        f"{statistics.median(t.unique for t in tests):g} unique.",
    ]


def write_csv(report: Report, out: pathlib.Path, found: Mapping[str, Kind] | None = None) -> None:
    out.parent.mkdir(parents=True, exist_ok=True)
    with out.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.writer(handle)
        if found is None:
            writer.writerow(["test", "covered", "unique"])
            writer.writerows([t.test, t.covered, t.unique] for t in report.tests)
            return
        writer.writerow(["test", "covered", "unique", "style", "size"])
        for t in report.tests:
            kind = found.get(t.test, _UNKNOWN)
            writer.writerow([t.test, t.covered, t.unique, kind.style, kind.size])


class _Args(argparse.Namespace):
    data_file: pathlib.Path = pathlib.Path(".coverage")
    group: Group = "item"
    csv: pathlib.Path | None = None
    root: pathlib.Path = pathlib.Path()
    top: int = 10
    keep_definitions: bool = False
    sizes: pathlib.Path | None = None


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("data_file", type=pathlib.Path, nargs="?", default=pathlib.Path(".coverage"))
    parser.add_argument("--group", choices=GROUPS, default="item", help="what counts as one test")
    parser.add_argument("--csv", type=pathlib.Path, help="write every test's covered and unique counts here")
    parser.add_argument("--root", type=pathlib.Path, default=pathlib.Path(), help="report paths relative to this")
    parser.add_argument("--top", type=int, default=10, help="tests to list by unique lines")
    parser.add_argument(
        "--keep-definitions", action="store_true", help="credit def, class and module-level lines to tests"
    )
    parser.add_argument("--sizes", type=pathlib.Path, help="a pytest --record-sizes file: add style and size")
    args = parser.parse_args(argv, namespace=_Args())

    if not args.data_file.is_file():
        sys.stderr.write(f"{args.data_file} not found. Run pytest with --cov --cov-context=test first.\n")
        return 2
    if args.sizes and not args.sizes.is_file():
        sys.stderr.write(f"{args.sizes} not found. Run pytest with --record-sizes first.\n")
        return 2
    data = CoverageData(str(args.data_file))
    data.read()
    report = build(read_lines(data, args.root.resolve(), keep_definitions=args.keep_definitions), args.group)

    found = kinds(report, read_rows(args.sizes), args.group) if args.sizes else None

    out = sys.stdout
    for line in summary(report, args.group):
        out.write(line + "\n")
    if found is not None and report.tests:
        out.write("\nBy style and size (medians per test):\n")
        for line in by_kind(report, found):
            out.write("  " + line + "\n")
    if report.tests and args.top:
        out.write("\nMost unique lines:\n")
        for stat in report.tests[: args.top]:
            out.write(f"  {stat.unique:6d} unique {stat.covered:6d} covered  {stat.test}\n")
    if args.csv:
        write_csv(report, args.csv, found)
        out.write(f"\nEvery test: {args.csv.as_posix()}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())

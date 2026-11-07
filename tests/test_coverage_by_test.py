"""Per-test coverage: each test's covered lines, and the lines only it executes."""

from __future__ import annotations

import csv
import json
import pathlib

import pytest
from coverage import CoverageData
from hypothesis import given
from hypothesis import strategies as st

from rig.repo_governance.coverage_by_test import GROUPS, Footprint, Group, build, definition_lines, main, owner

pytestmark = pytest.mark.ci


@pytest.mark.parametrize(
    ("group", "expected"),
    [
        ("item", "tests/a.py::test_x[1-2]"),
        ("function", "tests/a.py::test_x"),
        ("file", "tests/a.py"),
    ],
)
def test_a_context_names_its_test_at_each_level(group: str, expected: str) -> None:
    for phase in ("run", "setup", "teardown"):
        assert owner(f"tests/a.py::test_x[1-2]|{phase}", group) == expected  # pyright: ignore[reportArgumentType]


def test_the_import_context_belongs_to_no_test() -> None:
    assert {owner("", g) for g in GROUPS} == {""}


def test_a_line_one_test_runs_is_unique_to_it_and_a_shared_line_is_not() -> None:
    report = build(
        {
            ("m.py", 1): ["tests/a.py::test_a|run"],
            ("m.py", 2): ["tests/a.py::test_a|run", "tests/a.py::test_b|run"],
            ("m.py", 3): ["tests/a.py::test_b|setup", "tests/a.py::test_b|run"],
            ("m.py", 4): [""],
            ("m.py", 5): ["", "tests/a.py::test_a|run"],
        }
    )

    assert report.tests == [
        Footprint("tests/a.py::test_a", covered=3, unique=2),
        Footprint("tests/a.py::test_b", covered=2, unique=1),
    ]
    assert (report.tested_lines, report.import_only) == (4, 1)


def test_cases_of_one_function_share_lines_as_items_but_own_them_as_a_function() -> None:
    lines = {("m.py", 1): ["tests/a.py::test_x[1]|run", "tests/a.py::test_x[2]|run"]}

    assert [t.unique for t in build(lines, "item").tests] == [0, 0]
    assert build(lines, "function").tests == [Footprint("tests/a.py::test_x", covered=1, unique=1)]


_tests = st.sampled_from(
    ["tests/a.py::test_p[1]", "tests/a.py::test_p[2]", "tests/a.py::test_q", "tests/b.py::test_r", ""]
)
_contexts = st.lists(st.tuples(_tests, st.sampled_from(["run", "setup", "teardown"])).map("|".join), max_size=6)
_lines = st.dictionaries(st.tuples(st.sampled_from(["m.py", "n.py"]), st.integers(1, 30)), _contexts, max_size=40)


@given(_lines, st.sampled_from(GROUPS))
def test_counts_are_bounded_and_every_line_is_accounted_for(
    lines: dict[tuple[str, int], list[str]], group: Group
) -> None:
    report = build(lines, group)

    assert report.tested_lines + report.import_only == len(lines)
    assert sum(t.unique for t in report.tests) <= report.tested_lines
    for stat in report.tests:
        assert 0 <= stat.unique <= stat.covered <= report.tested_lines


@given(_lines)
def test_coarser_groups_never_own_fewer_lines(lines: dict[tuple[str, int], list[str]]) -> None:
    owned = {g: sum(t.unique for t in build(lines, g).tests) for g in GROUPS}

    assert owned["item"] <= owned["function"] <= owned["file"]
    assert len({build(lines, g).tested_lines for g in GROUPS}) == 1


def _record(path: pathlib.Path, source: pathlib.Path, by_context: dict[str, list[int]]) -> None:
    data = CoverageData(str(path))
    for context, linenos in by_context.items():
        data.set_context(context)
        data.add_lines({str(source): linenos})
    data.write()


def test_main_reads_a_coverage_file_and_writes_every_test(
    tmp_path: pathlib.Path, capsys: pytest.CaptureFixture[str]
) -> None:
    source = tmp_path / "src" / "m.py"
    _record(
        tmp_path / ".coverage",
        source,
        {"": [1], "tests/a.py::test_a|run": [2, 3], "tests/a.py::test_b|run": [3]},
    )
    out = tmp_path / "out" / "by-test.csv"

    code = main([str(tmp_path / ".coverage"), "--root", str(tmp_path), "--csv", str(out)])

    assert code == 0
    printed = capsys.readouterr().out
    assert "2 tests (item) execute 2 lines; 1 more run only at import." in printed
    assert "1 tests (50%) execute no line that another test does not." in printed
    with out.open(encoding="utf-8", newline="") as handle:
        assert list(csv.reader(handle)) == [
            ["test", "covered", "unique"],
            ["tests/a.py::test_a", "2", "1"],
            ["tests/a.py::test_b", "1", "0"],
        ]


def test_main_without_a_coverage_file_says_how_to_record_one(
    tmp_path: pathlib.Path, capsys: pytest.CaptureFixture[str]
) -> None:
    assert main([str(tmp_path / ".coverage")]) == 2
    assert "--cov-context=test" in capsys.readouterr().err


def test_main_on_data_without_contexts_says_so(tmp_path: pathlib.Path, capsys: pytest.CaptureFixture[str]) -> None:
    _record(tmp_path / ".coverage", tmp_path / "m.py", {"": [1, 2]})

    assert main([str(tmp_path / ".coverage")]) == 0
    assert "No test contexts" in capsys.readouterr().out


DEFINITIONS = """\
import os

LIMIT = (
    3
)


@decorator
def outer(x=os.sep):
    y = x

    def inner():
        return y

    return inner


class Thing:
    size = 2

    def method(self):
        return self.size
"""


def test_definition_lines_are_what_runs_at_import_and_never_a_function_body() -> None:
    assert definition_lines(DEFINITIONS) == {1, 3, 4, 5, 8, 9, 18, 19, 21}


def test_a_definition_line_is_import_only_whichever_test_recorded_it(
    tmp_path: pathlib.Path, capsys: pytest.CaptureFixture[str]
) -> None:
    source = tmp_path / "m.py"
    source.write_text(DEFINITIONS, encoding="utf-8")
    _record(tmp_path / ".coverage", source, {"tests/a.py::test_a|run": [1, 9, 10, 16], "tests/a.py::test_b|run": [22]})
    out = tmp_path / "by-test.csv"

    main([str(tmp_path / ".coverage"), "--root", str(tmp_path), "--csv", str(out)])
    by_default = out.read_text(encoding="utf-8")
    main([str(tmp_path / ".coverage"), "--root", str(tmp_path), "--csv", str(out), "--keep-definitions"])
    kept = out.read_text(encoding="utf-8")

    assert by_default.splitlines()[1:] == ["tests/a.py::test_a,2,2", "tests/a.py::test_b,1,1"]
    assert kept.splitlines()[1:] == ["tests/a.py::test_a,4,4", "tests/a.py::test_b,1,1"]
    assert "2 more run only at import." in capsys.readouterr().out


def _sizes(path: pathlib.Path, rows: dict[str, tuple[str, str]]) -> pathlib.Path:
    path.write_text(
        json.dumps({t: {"style": st_, "size": sz, "resources": [], "duration": 0.0} for t, (st_, sz) in rows.items()}),
        encoding="utf-8",
    )
    return path


def test_sizes_add_a_style_and_size_to_every_test_and_a_line_per_kind(
    tmp_path: pathlib.Path, capsys: pytest.CaptureFixture[str]
) -> None:
    _record(
        tmp_path / ".coverage",
        tmp_path / "m.py",
        {
            "tests/a.py::test_p[1]|run": [1, 2],
            "tests/a.py::test_p[2]|run": [2],
            "tests/a.py::test_q|run": [2, 3],
            "tests/b.py::test_r|run": [4],
        },
    )
    sizes = _sizes(
        tmp_path / "sizes.json",
        {
            "tests/a.py::test_p[1]": ("example", "small"),
            "tests/a.py::test_p[2]": ("example", "medium"),
            "tests/a.py::test_q": ("property", "small"),
        },
    )
    out = tmp_path / "by-function.csv"

    code = main([str(tmp_path / ".coverage"), "--group", "function", "--sizes", str(sizes), "--csv", str(out)])

    assert code == 0
    with out.open(encoding="utf-8", newline="") as handle:
        assert sorted(csv.reader(handle)) == sorted(
            [
                ["test", "covered", "unique", "style", "size"],
                ["tests/a.py::test_p", "2", "1", "example", "medium"],
                ["tests/a.py::test_q", "2", "1", "property", "small"],
                ["tests/b.py::test_r", "1", "1", "unknown", "unknown"],
            ]
        )
    printed = capsys.readouterr().out
    assert "By style and size" in printed
    assert any(line.split()[:3] == ["example", "medium", "1"] for line in printed.splitlines())


def test_a_file_mixing_styles_is_mixed_and_takes_its_largest_size(tmp_path: pathlib.Path) -> None:
    _record(tmp_path / ".coverage", tmp_path / "m.py", {"tests/a.py::test_p|run": [1], "tests/a.py::test_q|run": [2]})
    sizes = _sizes(
        tmp_path / "sizes.json",
        {"tests/a.py::test_p": ("example", "large"), "tests/a.py::test_q": ("bdd", "small")},
    )
    out = tmp_path / "by-file.csv"

    main([str(tmp_path / ".coverage"), "--group", "file", "--sizes", str(sizes), "--csv", str(out)])

    assert out.read_text(encoding="utf-8").splitlines()[1] == "tests/a.py,2,2,mixed,large"


def test_main_with_a_missing_sizes_file_says_how_to_record_one(
    tmp_path: pathlib.Path, capsys: pytest.CaptureFixture[str]
) -> None:
    _record(tmp_path / ".coverage", tmp_path / "m.py", {"tests/a.py::test_p|run": [1]})

    assert main([str(tmp_path / ".coverage"), "--sizes", str(tmp_path / "nope.json")]) == 2
    assert "--record-sizes" in capsys.readouterr().err

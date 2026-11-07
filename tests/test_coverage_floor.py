"""The coverage floor is a ratchet: it never falls and must follow coverage up to the target."""

from __future__ import annotations

import pathlib

import pytest
from hypothesis import given
from hypothesis import strategies as st

from rig.repo_governance.coverage_floor import Ratchet, main, problems, read_measured, read_ratchet

pytestmark = pytest.mark.ci

PYPROJECT = """
[tool.coverage.report]
fail_under = 80

[tool.rig.coverage_ratchet]
target = 95
slack = 2
"""

RATCHET = Ratchet(floor=80, target=95, slack=2)


def test_reads_the_floor_and_ratchet_from_pyproject() -> None:
    assert read_ratchet(PYPROJECT) == RATCHET


def test_a_pyproject_without_a_floor_has_floor_zero_and_the_default_target() -> None:
    assert read_ratchet("[project]\nname = 'x'\n") == Ratchet(floor=0, target=95, slack=2)


def test_the_repository_floor_holds_its_own_rules() -> None:
    root = pathlib.Path(__file__).resolve().parent.parent
    ratchet = read_ratchet((root / "pyproject.toml").read_text(encoding="utf-8"))

    assert 0 < ratchet.floor <= ratchet.target == 95


def test_reads_the_total_from_coverage_json() -> None:
    assert read_measured('{"totals": {"percent_covered": 81.25}, "files": {}}') == 81.25


@pytest.mark.parametrize("measured", [80.0, 81.0, 82.0])
def test_coverage_at_or_just_above_the_floor_holds(measured: float) -> None:
    assert problems(measured, RATCHET, base_floor=80) == []


def test_coverage_below_the_floor_fails() -> None:
    assert problems(79.99, RATCHET) == ["Coverage 79.99% is below the floor 80%. Add tests, don't lower the floor."]


def test_lowering_the_floor_fails_even_when_coverage_holds_it() -> None:
    lowered = Ratchet(floor=78, target=95, slack=2)

    assert problems(79.0, lowered, base_floor=80) == ["The floor fell from 80% to 78%. It only rises."]


def test_coverage_well_above_the_floor_demands_raising_it_to_the_whole_percent() -> None:
    assert problems(84.6, RATCHET) == [
        "Coverage 84.60% is more than 2 points above the floor 80%. Raise [tool.coverage.report] fail_under to 84."
    ]


def test_the_demanded_floor_never_passes_the_target() -> None:
    assert problems(99.5, Ratchet(floor=90, target=95, slack=2))[0].endswith("fail_under to 95.")


def test_at_the_target_there_is_nothing_left_to_raise() -> None:
    assert problems(99.5, Ratchet(floor=95, target=95, slack=2)) == []


def test_a_floor_above_the_target_fails() -> None:
    assert problems(97.0, Ratchet(floor=96, target=95, slack=2)) == ["The floor 96% is above the target 95%."]


percent = st.floats(min_value=0, max_value=100, allow_nan=False)


@given(measured=percent, floor=st.integers(0, 95), slack=st.integers(1, 5))
def test_following_the_advice_always_settles_the_ratchet(measured: float, floor: int, slack: int) -> None:
    """Raising the floor to the value the message names leaves nothing to fix."""
    ratchet = Ratchet(floor=floor, target=95, slack=slack)
    found = problems(measured, ratchet, base_floor=floor)
    if measured < floor:
        assert len(found) == 1
        return

    raised = Ratchet(floor=max(floor, min(95, int(measured))), target=95, slack=slack)
    assert problems(measured, raised, base_floor=floor) == []


def test_main_reports_each_problem_as_a_workflow_error(
    tmp_path: pathlib.Path, capsys: pytest.CaptureFixture[str]
) -> None:
    (tmp_path / "pyproject.toml").write_text(PYPROJECT, encoding="utf-8")
    (tmp_path / "base.toml").write_text(PYPROJECT.replace("80", "82"), encoding="utf-8")
    (tmp_path / "coverage.json").write_text('{"totals": {"percent_covered": 79.0}}', encoding="utf-8")

    code = main(
        [
            (tmp_path / "coverage.json").as_posix(),
            "--pyproject",
            (tmp_path / "pyproject.toml").as_posix(),
            "--base-pyproject",
            (tmp_path / "base.toml").as_posix(),
        ]
    )

    assert code == 1
    assert capsys.readouterr().out.splitlines() == [
        "::error::The floor fell from 82% to 80%. It only rises.",
        "::error::Coverage 79.00% is below the floor 80%. Add tests, don't lower the floor.",
    ]


def test_main_passes_without_a_base_branch_file(tmp_path: pathlib.Path, capsys: pytest.CaptureFixture[str]) -> None:
    (tmp_path / "pyproject.toml").write_text(PYPROJECT, encoding="utf-8")
    (tmp_path / "coverage.json").write_text('{"totals": {"percent_covered": 81.0}}', encoding="utf-8")

    code = main(
        [
            (tmp_path / "coverage.json").as_posix(),
            "--pyproject",
            (tmp_path / "pyproject.toml").as_posix(),
            "--base-pyproject",
            (tmp_path / "missing.toml").as_posix(),
        ]
    )

    assert code == 0
    assert capsys.readouterr().out == "Coverage 81.00% holds the floor 80% (target 95%).\n"

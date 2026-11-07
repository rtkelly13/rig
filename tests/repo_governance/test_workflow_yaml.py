from __future__ import annotations

from dataclasses import dataclass

import pytest

from rig.repo_governance.workflow_yaml import KeyText, Triggers, workflow_keys
from rig.typed_json import decode
from rig.typed_yaml import load_yaml

pytestmark = pytest.mark.ci


@dataclass(frozen=True, slots=True)
class _Workflow:
    on: Triggers = ()
    jobs: dict[KeyText, object] | None = None


def _read(text: str) -> _Workflow:
    return decode(_Workflow, workflow_keys(load_yaml(text)))


def test_the_yaml_boolean_on_key_reads_as_on() -> None:
    assert _read("on:\n  pull_request:\n  push:\n").on == ("pull_request", "push")


def test_a_literal_on_key_wins_over_the_boolean_one() -> None:
    assert _read('"on": [push]\non: [pull_request]\n').on == ("push",)


@pytest.mark.parametrize(
    ("text", "expected"),
    [("on: push\n", ("push",)), ("on: [push, pull_request]\n", ("push", "pull_request")), ("on:\n", ()), ("", ())],
)
def test_triggers_read_from_any_form(text: str, expected: tuple[str, ...]) -> None:
    assert decode(_Workflow, workflow_keys(load_yaml(text) or {})).on == expected


def test_job_ids_read_as_text_and_other_non_string_keys_are_dropped() -> None:
    workflow = _read("1: dropped\njobs:\n  2: {}\n")

    assert workflow.jobs == {"2": {}}


def test_anything_but_a_mapping_passes_through() -> None:
    assert workflow_keys(["a"]) == ["a"]

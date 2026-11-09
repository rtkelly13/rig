"""JSONC parsing: comments, trailing commas, and member offsets."""

from __future__ import annotations

import pytest

from rig import jsonc

pytestmark = pytest.mark.ci

SAMPLE = """﻿{
    // a line comment
    "update.mode": "manual", // trailing comment
    /* block
       comment */
    "url": "https://example.com/* not a comment */",
    "nested": {
        "a": [1, 2, 3,],
        "b": "quote \\" inside",
    },
}
"""


def test_loads_reads_comments_and_trailing_commas() -> None:
    assert jsonc.loads(SAMPLE) == {
        "update.mode": "manual",
        "url": "https://example.com/* not a comment */",
        "nested": {"a": [1, 2, 3], "b": 'quote " inside'},
    }


def test_members_report_each_value_span() -> None:
    root = jsonc.root_object(SAMPLE)
    found = jsonc.members(SAMPLE, root)

    assert [m.key for m in found] == ["update.mode", "url", "nested"]
    nested = found[2]
    assert SAMPLE[nested.value_start] == "{"
    assert SAMPLE[nested.value_end - 1] == "}"
    assert [m.key for m in jsonc.members(SAMPLE, nested.value_start)] == ["a", "b"]


def test_empty_object_has_no_members() -> None:
    assert jsonc.members("{ /* nothing */ }", 0) == []


@pytest.mark.parametrize("text", ["[]", "", '{"a": 1', '{"a" 1}', "{ /* open"])
def test_rejects_what_it_cannot_read(text: str) -> None:
    with pytest.raises(jsonc.JsoncError):
        jsonc.members(text, jsonc.root_object(text))

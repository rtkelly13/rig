"""The decode helpers every JSON boundary goes through."""

from __future__ import annotations

from dataclasses import dataclass

import pytest
import yaml
from pydantic import StrictInt, StrictStr, ValidationError

from rig.typed_json import Lenient, decode, decode_json, encode_json, load_json, try_decode, try_decode_json
from rig.typed_yaml import decode_yaml, load_yaml, try_decode_yaml


@dataclass(frozen=True, slots=True)
class _Point:
    x: int
    y: int


def test_decode_builds_the_record() -> None:
    assert decode(_Point, {"x": 1, "y": 2}) == _Point(1, 2)


def test_decode_raises_on_a_mismatch() -> None:
    with pytest.raises(ValidationError):
        decode(_Point, {"x": 1})


def test_try_decode_is_none_on_a_mismatch() -> None:
    assert try_decode(list[_Point], [{"x": 1}]) is None


def test_decode_json_parses_and_validates() -> None:
    assert decode_json(list[_Point], '[{"x": 1, "y": 2}]') == [_Point(1, 2)]


@pytest.mark.parametrize("text", ["not json", '{"x": 1}', ""])
def test_decode_json_raises_validation_error_for_bad_text_or_shape(text: str) -> None:
    with pytest.raises(ValidationError):
        decode_json(_Point, text)
    assert try_decode_json(_Point, text) is None


def test_load_json_parses_and_raises_value_error() -> None:
    assert load_json('{"a": [1]}') == {"a": [1]}
    with pytest.raises(ValueError, match="Expecting value"):
        load_json("not json")


@dataclass(frozen=True, slots=True)
class _Loose:
    name: Lenient[StrictStr] = None
    size: Lenient[StrictInt] = None


def test_lenient_field_of_the_wrong_type_reads_as_absent() -> None:
    assert decode(_Loose, {"name": 3, "size": 4}) == _Loose(name=None, size=4)


def test_yaml_helpers_parse_decode_and_reject() -> None:
    assert load_yaml("a: 1") == {"a": 1}
    assert decode_yaml(_Point, "x: 1\ny: 2") == _Point(1, 2)
    assert try_decode_yaml(_Point, "x: [") is None
    assert try_decode_yaml(_Point, "x: 1") is None
    with pytest.raises(yaml.YAMLError):
        decode_yaml(_Point, "x: [")


@pytest.mark.ci
def test_encode_json_round_trips_through_decode_json() -> None:
    text = encode_json(_Point, _Point(x=3, y=4))
    assert text == '{"x":3,"y":4}'
    assert decode_json(_Point, text) == _Point(x=3, y=4)

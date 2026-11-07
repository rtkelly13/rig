from __future__ import annotations

from dataclasses import fields, is_dataclass
from typing import Any

from deepdiff import DeepDiff
from pydantic import BaseModel


def _serialize_for_deep_diff(value: Any) -> Any:
    if isinstance(value, BaseModel):
        return value.model_dump()

    if isinstance(value, dict):
        return {key: _serialize_for_deep_diff(item) for key, item in value.items()}

    if isinstance(value, list):
        return [_serialize_for_deep_diff(item) for item in value]

    if isinstance(value, tuple):
        return tuple(_serialize_for_deep_diff(item) for item in value)

    to_dict = getattr(value, "to_dict", None)
    if callable(to_dict):
        return to_dict()

    if is_dataclass(value) and not isinstance(value, type):
        return {field.name: _serialize_for_deep_diff(getattr(value, field.name)) for field in fields(value)}

    return value


def assert_deep_equal(
    left: Any,
    right: Any,
    *,
    ignore_order: bool = False,
) -> None:
    diff = DeepDiff(
        _serialize_for_deep_diff(left),
        _serialize_for_deep_diff(right),
        ignore_order=ignore_order,
    )
    assert not diff, diff.pretty()


def assert_pydantic_deep_equal(
    left: BaseModel,
    right: BaseModel,
    *,
    ignore_order: bool = False,
) -> None:
    assert_deep_equal(left, right, ignore_order=ignore_order)

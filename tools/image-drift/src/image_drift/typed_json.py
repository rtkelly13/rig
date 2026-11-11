"""Decode parsed JSON into typed shapes at the boundary, instead of casting it."""

from __future__ import annotations

import json
from collections.abc import Callable
from functools import cache
from typing import Annotated, Protocol, TypeIs

from pydantic import TypeAdapter, ValidationError, WrapValidator

type JsonValue = str | int | float | bool | None | list[JsonValue] | dict[str, JsonValue]
type JsonObject = dict[str, JsonValue]


_JSON_LOADS: Callable[[str | bytes], object] = json.loads


def load_json(text: str | bytes) -> object:
    """``json.loads`` typed as ``object``, so the result must be narrowed or decoded before use.

    Raises ``json.JSONDecodeError`` (a ``ValueError``) on malformed text, exactly as ``json.loads`` does.
    """
    return _JSON_LOADS(text)


class WrapHandler(Protocol):
    """The inner validator a pydantic ``WrapValidator`` receives, typed so its result is not ``Any``."""

    def __call__(self, value: object, /) -> object: ...


def or_none(value: object, handler: WrapHandler) -> object:
    """A field of the wrong type reads as absent; the rest of its record still counts."""
    try:
        return handler(value)
    except ValidationError:
        return None


type Lenient[T] = Annotated[T | None, WrapValidator(or_none)]


@cache
def _adapter[T](kind: type[T]) -> TypeAdapter[T]:
    return TypeAdapter(kind)


def decode[T](kind: type[T], value: object) -> T:
    """Validate ``value`` as ``kind``, raising ``pydantic.ValidationError`` (a ``ValueError``) when it does not fit."""
    return _adapter(kind).validate_python(value)


def try_decode[T](kind: type[T], value: object) -> T | None:
    """Validate ``value`` as ``kind``, or ``None`` when it does not fit."""
    try:
        return decode(kind, value)
    except ValidationError:
        return None


def decode_json[T](kind: type[T], text: str | bytes) -> T:
    """Parse and validate JSON ``text`` as ``kind``; malformed JSON raises ``ValidationError`` too."""
    return _adapter(kind).validate_json(text)


def encode_json[T](kind: type[T], value: T) -> str:
    """Serialise ``value`` as ``kind`` to JSON text; the inverse of ``decode_json``."""
    return _adapter(kind).dump_json(value).decode("utf-8")


def try_decode_json[T](kind: type[T], text: str | bytes) -> T | None:
    """Parse and validate JSON ``text`` as ``kind``, or ``None`` when it is malformed or does not fit."""
    try:
        return decode_json(kind, text)
    except ValidationError:
        return None


def is_list(value: object) -> TypeIs[list[object]]:
    """Narrow parsed JSON to a list whose items are still unchecked."""
    return isinstance(value, list)


def is_object(value: object) -> TypeIs[dict[object, object]]:
    """Narrow parsed JSON to a mapping whose keys and values are still unchecked."""
    return isinstance(value, dict)

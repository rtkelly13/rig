"""Decode TOML through the same typed boundary used for JSON and YAML."""

from __future__ import annotations

import tomllib
from collections.abc import Callable

from rig.typed_json import decode

_LOAD: Callable[[str], object] = tomllib.loads


def decode_toml[T](kind: type[T], text: str) -> T:
    """Validate a TOML document against a typed schema."""
    return decode(kind, _LOAD(text))

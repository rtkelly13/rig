"""Parse YAML into ``object`` and decode it into typed shapes, instead of passing ``Any`` around."""

from __future__ import annotations

from collections.abc import Callable

import yaml

from rig.typed_json import decode

_SAFE_LOAD: Callable[[str | bytes], object] = yaml.safe_load


def load_yaml(text: str | bytes) -> object:
    """``yaml.safe_load`` typed as ``object``. Raises ``yaml.YAMLError`` on malformed text."""
    return _SAFE_LOAD(text)


def decode_yaml[T](kind: type[T], text: str | bytes) -> T:
    """Parse and validate YAML ``text`` as ``kind``; raises ``yaml.YAMLError`` or ``pydantic.ValidationError``."""
    return decode(kind, load_yaml(text))

from __future__ import annotations

import sys

import pytest


@pytest.fixture(autouse=True)
def clear_image_drift_caches() -> None:
    """Start every test with empty ``functools.cache``/``lru_cache`` caches in ``image_drift``.

    A test should not pass or fail because another test warmed a cache first.
    """
    for name, module in list(sys.modules.items()):
        if name != "image_drift" and not name.startswith("image_drift."):
            continue
        for value in list(vars(module).values()):
            if hasattr(value, "cache_clear") and getattr(value, "__module__", None) == name:
                value.cache_clear()  # pyright: ignore[reportAttributeAccessIssue]

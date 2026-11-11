"""Typed scalar reductions over image arrays.

numpy declares array shapes, ``np.gradient`` and element reads as ``Any``. These helpers
return plain ``int`` and ``float`` so the scoring code stays fully typed.
"""

from __future__ import annotations

import math
from collections.abc import Callable, Sequence
from typing import SupportsFloat

import numpy as np

from image_drift.geometry import FloatArray


def _at(values: Sequence[int], index: int) -> int:
    return values[index]


def axis_length(array: FloatArray, axis: int) -> int:
    """Length of ``array`` along ``axis``."""
    return _at(array.shape, axis)


def rms(array: FloatArray) -> float:
    """Root mean square of every element."""
    return math.sqrt(float(np.mean(array**2)))


def _call_float(function: Callable[..., SupportsFloat], *args: object) -> float:
    return float(function(*args))


def _call_arrays(function: Callable[[FloatArray], Sequence[FloatArray]], array: FloatArray) -> list[FloatArray]:
    return list(function(array))


def gradient_magnitude(array: FloatArray) -> FloatArray:
    """Per-pixel magnitude of the 2-D ``np.gradient`` of a single-channel image."""
    rows, columns = _call_arrays(np.gradient, array)
    return np.sqrt(rows**2 + columns**2)


def correlation_pct(first: FloatArray, second: FloatArray) -> float:
    """Pearson correlation of two equally sized arrays as a percentage; 100 when either is constant."""
    cov = np.cov(first.flatten(), second.flatten())
    denom = math.sqrt(_call_float(cov.item, 0, 0) * _call_float(cov.item, 1, 1))
    return _call_float(cov.item, 0, 1) / denom * 100.0 if denom > 0 else 100.0

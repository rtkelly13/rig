"""Typed handles to Windows system DLLs.

``ctypes.windll`` only exists in typeshed on Windows, so code reaching it directly is
unanalysable when the checker runs on Linux. ``system_dll`` returns the same handle as
a ``ctypes.CDLL`` (``WinDLL``'s base, declared on every platform) and raises
``OSError`` elsewhere, so callers type-check everywhere and fail the same way off
Windows that a missing DLL would.
"""

from __future__ import annotations

import ctypes
import functools
import sys
from collections.abc import Callable, Sequence


@functools.cache
def system_dll(name: str) -> ctypes.CDLL:
    """The stdcall handle for ``name`` (e.g. ``"user32"``), loaded once per process."""
    if sys.platform == "win32":
        return ctypes.WinDLL(name)
    raise OSError(f"{name}.dll is only available on Windows")


def call_int(function: Callable[..., int], *args: object) -> int:
    """Call a foreign function whose result is an integer: a handle, a ``BOOL`` or a count.

    ctypes declares every foreign call as returning ``Any``; routing it through this
    typed parameter gives callers an ``int`` without a cast.
    """
    return function(*args)


def registry_value[Key](query: Callable[[Key, str], Sequence[object]], key: Key, name: str) -> object:
    """The data half of ``winreg.QueryValueEx(key, name)``, typed ``object`` rather than ``Any``."""
    return query(key, name)[0]

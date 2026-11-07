"""What a Windows call can raise, as tuples to catch instead of bare ``Exception``.

Importable on every OS: off Windows the pywin32 and COM classes do not exist, so the
tuples hold only what a portable stand-in for the call can raise.
"""

from __future__ import annotations

import sys
from collections.abc import Generator
from contextlib import contextmanager


class VirtualDesktopError(RuntimeError):
    """pyvda raised a bare ``Exception``: a desktop vanished mid-call, or a view could not be found."""


def _win32_errors() -> tuple[type[Exception], ...]:
    if sys.platform == "win32":
        from _ctypes import COMError

        import pywintypes

        return (OSError, pywintypes.error, pywintypes.com_error, COMError)
    return (OSError,)


#: What a pywin32, ctypes or COM call can raise: Win32 errors, pywin32's own error and COM
#: failures from both pywin32 and comtypes.
WIN32_ERRORS = _win32_errors()

#: :data:`WIN32_ERRORS` plus what a best-effort probe hits where the Windows layer is missing:
#: :func:`desktop_manager.win32.ensure_windows`'s ``RuntimeError`` and an absent pywin32 module.
WIN32_OPTIONAL_ERRORS: tuple[type[Exception], ...] = (*WIN32_ERRORS, ImportError, RuntimeError)

#: What importing pyvda, or a pyvda call wrapped in :func:`pyvda_errors`, can raise. ``RuntimeError``
#: also covers :class:`VirtualDesktopError`.
PYVDA_ERRORS: tuple[type[Exception], ...] = (*WIN32_OPTIONAL_ERRORS, ValueError, NotImplementedError)


@contextmanager
def pyvda_errors() -> Generator[None]:
    """Turn pyvda's bare ``Exception`` into :class:`VirtualDesktopError` so callers can catch
    :data:`PYVDA_ERRORS` instead of everything. Any other exception passes through untouched."""
    try:
        yield
    except Exception as err:
        if type(err) is not Exception:
            raise
        raise VirtualDesktopError(str(err)) from err

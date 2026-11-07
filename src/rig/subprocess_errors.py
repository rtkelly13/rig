"""Typed reads of a failed subprocess's captured output.

``CalledProcessError.stderr`` and ``.stdout`` are typed ``Any`` because they hold ``str``, ``bytes`` or ``None``
depending on how the process was run. Read them through these helpers instead of touching the attributes.
"""

from __future__ import annotations

import subprocess


def stderr_text(exc: subprocess.CalledProcessError) -> str:
    """The failed command's captured stderr as text; empty when it was not captured."""
    match exc:
        case subprocess.CalledProcessError(stderr=str() as text):
            return text
        case subprocess.CalledProcessError(stderr=bytes() as raw):
            return raw.decode(errors="replace")
        case _:
            return ""


def stdout_text(exc: subprocess.CalledProcessError) -> str:
    """The failed command's captured stdout as text; empty when it was not captured."""
    match exc:
        case subprocess.CalledProcessError(stdout=str() as text):
            return text
        case subprocess.CalledProcessError(stdout=bytes() as raw):
            return raw.decode(errors="replace")
        case _:
            return ""

"""The wrap-up follower's lock on Windows: a one-byte `msvcrt` region lock, dropped if the process dies."""

from __future__ import annotations

import msvcrt
from typing import IO


def try_lock(handle: IO[bytes]) -> None:
    """Take the lock without waiting; raises OSError when another process holds it."""
    _ = handle.seek(0)
    msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)


def unlock(handle: IO[bytes]) -> None:
    _ = handle.seek(0)
    msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)

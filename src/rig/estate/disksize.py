"""Measure a directory the way deleting it would.

Apparent size lies about the two biggest regenerable categories. uv hardlinks
`.venv` files from its cache and pnpm hardlinks `node_modules` from its store,
so deleting one copy frees only the files nothing else links. Measured on this
machine (2026-10-08): a `.venv` of 479 MB frees 24 MB, a `node_modules` of
3,676 MB frees 89 MB. So "freeable" counts only files with a link count of one.

The catch is cost on Windows. `os.scandir` hands back size and mtime for free
from the directory listing, but `st_nlink` is always zero there; reading it
means an `os.stat` per file, which made a `node_modules` walk 13x slower (7.7s
to 103s). So every file of 1 MiB or more is stat'ed exactly (they dominate the
bytes), and one in `SAMPLE_STRIDE` of the rest, whose freeable share is
extrapolated. The result is flagged `estimated`. On POSIX the listing already
carries `st_nlink`, so every measure there is exact.

A hardlink between two files *inside* the measured tree reads as shared even
though deleting the tree frees it. That undercounts, which is the safe
direction for a number that justifies deleting things.

Pure Python on purpose: `du` is not on the PATH a scheduled task gets on
Windows, and a size of zero there would silently skip every candidate.
"""

from __future__ import annotations

import os
import pathlib
from dataclasses import dataclass

SAMPLE_STRIDE = 32
LARGE_FILE = 1024 * 1024

_NLINK_IN_LISTING = os.name != "nt"


@dataclass(frozen=True, slots=True)
class Measure:
    """What deleting a tree would free, and whether it may be deleted at all."""

    apparent: int
    freeable: int
    #: A `.git` file or directory exists somewhere below the root; the walk stopped there.
    holds_checkout: bool = False
    estimated: bool = False


#: A tree whose walk stopped at a `.git`; its sizes are unknown, not zero.
UNMEASURED = Measure(0, 0, holds_checkout=True)


@dataclass(slots=True)
class _Tally:
    apparent: int = 0
    known_freeable: int = 0
    small_total: int = 0
    small_seen: int = 0
    sampled_bytes: int = 0
    sampled_freeable: int = 0

    def add(self, entry: os.DirEntry[str], size: int, nlink_in_listing: int) -> None:
        self.apparent += size
        if _NLINK_IN_LISTING:
            if nlink_in_listing <= 1:
                self.known_freeable += size
            return
        if size >= LARGE_FILE:
            if _nlink(entry.path) <= 1:
                self.known_freeable += size
            return
        self.small_total += size
        self.small_seen += 1
        if self.small_seen % SAMPLE_STRIDE == 1:
            self.sampled_bytes += size
            if _nlink(entry.path) <= 1:
                self.sampled_freeable += size

    def result(self) -> Measure:
        if not self.small_total:
            return Measure(self.apparent, self.known_freeable)
        share = self.sampled_freeable / self.sampled_bytes if self.sampled_bytes else 1.0
        freeable = self.known_freeable + int(self.small_total * share)
        return Measure(self.apparent, min(freeable, self.apparent), estimated=True)


def measure(path: pathlib.Path) -> Measure:
    """Walk *path* once, returning apparent and freeable bytes.

    The walk ends at the first `.git` and returns `UNMEASURED`: a caller that
    refuses checkouts needs no size for one, and walking a nested checkout with
    its own `node_modules` can take minutes.
    """
    tally = _Tally()
    stack = [os.fspath(path)]
    while stack:
        current = stack.pop()
        try:
            with os.scandir(current) as entries:
                for entry in entries:
                    if entry.name == ".git":
                        return UNMEASURED
                    try:
                        if entry.is_symlink() or entry.is_junction():
                            continue
                        if entry.is_dir(follow_symlinks=False):
                            stack.append(entry.path)
                        elif entry.is_file(follow_symlinks=False):
                            st = entry.stat(follow_symlinks=False)
                            tally.add(entry, st.st_size, st.st_nlink)
                    except OSError:
                        continue
        except OSError:
            continue
    return tally.result()


def _nlink(path: str) -> int:
    try:
        return pathlib.Path(path).stat(follow_symlinks=False).st_nlink
    except OSError:
        return 1

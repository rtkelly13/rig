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
`du` answers the general question for any tree, checkouts included, and
exactly: POSIX shells out to `du`, and Windows walks the tree counting a
hardlinked file only once every one of its links has been seen inside it,
rounded up to the cluster. It is slower than `measure`, so it serves the
interactive commands (`apps`, `downloads`, `reclaim`), not the scheduled ones.
"""

from __future__ import annotations

import os
import pathlib
import subprocess
import sys
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


@dataclass(frozen=True, slots=True)
class Footprint:
    """Apparent bytes under a tree, and when its newest file was written."""

    bytes_used: int = 0
    #: Epoch mtime of the newest file; 0.0 for a tree with no files.
    newest: float = 0.0


def footprint(path: pathlib.Path) -> Footprint:
    """Walk all of *path*, `.git` included, summing apparent size and the newest mtime.

    Unlike `measure` this never stops early and never asks for link counts, so
    on Windows it costs only the directory listings (about 10s for 300,000
    files). It answers "how big is this, and is anyone still writing to it".
    Symlinks and junctions are not followed: a workspace checkout links whole
    clones in under `projects/`, which would otherwise count them again.
    """
    total = 0
    newest = 0.0
    stack = [os.fspath(path)]
    while stack:
        current = stack.pop()
        try:
            with os.scandir(current) as entries:
                for entry in entries:
                    try:
                        if entry.is_symlink() or entry.is_junction():
                            continue
                        if entry.is_dir(follow_symlinks=False):
                            stack.append(entry.path)
                        elif entry.is_file(follow_symlinks=False):
                            st = entry.stat(follow_symlinks=False)
                            total += st.st_size
                            newest = max(newest, st.st_mtime)
                    except OSError:
                        continue
        except OSError:
            continue
    return Footprint(total, newest)


#: NTFS default cluster size; allocation is a whole number of these.
CLUSTER = 4096


@dataclass(slots=True, frozen=True)
class TreeSize:
    """Allocation of one tree, split by whether deleting it frees the bytes."""

    #: Bytes that go when the tree goes: every link to the file is inside it.
    freeable: int
    #: Bytes also linked from outside the tree: deleting it frees none of them.
    shared: int


def _allocated(size: int) -> int:
    return -(-size // CLUSTER) * CLUSTER


@dataclass(slots=True, frozen=True)
class _FileId:
    """A file's identity on the volume, shared by all of its hardlinks."""

    device: int
    inode: int


@dataclass(slots=True)
class _Linked:
    """A hardlinked file met during the walk: how many links it has and how many were inside."""

    nlink: int
    allocated: int
    seen: int = 0


def measure_windows(path: pathlib.Path) -> TreeSize:
    """Walk *path*, counting each inode once and only when all its links are inside."""
    inodes: dict[_FileId, _Linked] = {}
    plain = 0
    stack = [str(path)]
    while stack:
        current = stack.pop()
        try:
            entries = list(os.scandir(current))
        except OSError:
            continue
        for entry in entries:
            try:
                # Junctions and symlinks point elsewhere; their target is not this tree's.
                if entry.is_symlink() or entry.is_junction():
                    continue
                if entry.is_dir(follow_symlinks=False):
                    stack.append(entry.path)
                    continue
                # DirEntry.stat() on Windows carries no inode or link count; os.stat does.
                st = pathlib.Path(entry.path).stat(follow_symlinks=False)
            except OSError:
                continue
            if st.st_nlink <= 1:
                plain += _allocated(st.st_size)
                continue
            linked = inodes.setdefault(
                _FileId(device=st.st_dev, inode=st.st_ino),
                _Linked(nlink=st.st_nlink, allocated=_allocated(st.st_size)),
            )
            linked.seen += 1

    freeable = plain
    shared = 0
    for linked in inodes.values():
        if linked.seen >= linked.nlink:
            freeable += linked.allocated
        else:
            shared += linked.allocated
    return TreeSize(freeable=freeable, shared=shared)


def _du_posix(path: pathlib.Path) -> int:
    try:
        done = subprocess.run(["du", "-sk", str(path)], capture_output=True, text=True, check=False, timeout=120)
    except (OSError, subprocess.SubprocessError):
        return 0
    head = done.stdout.split()
    return int(head[0]) * 1024 if head and head[0].isdigit() else 0


def du(path: pathlib.Path) -> int:
    """Return the bytes deleting *path* would free (best effort; 0 if unreadable)."""
    if sys.platform == "win32":
        if path.is_file():
            try:
                return _allocated(path.stat().st_size)
            except OSError:
                return 0
        return measure_windows(path).freeable
    return _du_posix(path)

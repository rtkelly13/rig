"""Classify each test by what it actually does: Google's small, medium and large sizes.

A test's **size** is decided by the resources its body uses, not by where it lives or what its
author meant (docs/test-quality.md):

- **small**: one process, no subprocess, no filesystem writes, no sockets;
- **medium**: subprocesses, filesystem writes or localhost sockets;
- **large**: a socket to anywhere other than this machine, or a test marked ``integration``
  (it drives a live desktop).

Its **style** comes from the markers ``tests/conftest.py`` derives: ``property``, ``bdd``,
otherwise ``example``.

This module only classifies. The recorder is the pytest plugin in ``tests/sizes.py``, which
``--record-sizes PATH`` turns on. It feeds PEP 578 audit events from each test's *call* phase
through ``resource_of`` and writes one JSON file, ``{nodeid: Row}``, that
``coverage_by_test --sizes`` joins on. Bytecode writes (``__pycache__``) and the Hypothesis
example database are ignored, since neither is the code under test.
"""

from __future__ import annotations

import os
import pathlib
from collections.abc import Iterable, Sequence
from dataclasses import dataclass
from typing import Literal, TypeIs

from rig.typed_json import decode_json

Resource = Literal["subprocess", "filesystem", "localhost", "network"]
Size = Literal["small", "medium", "large"]
Style = Literal["example", "property", "bdd"]

SIZES: tuple[Size, ...] = ("small", "medium", "large")

_WRITE_FLAGS = os.O_WRONLY | os.O_RDWR | os.O_CREAT | os.O_APPEND | os.O_TRUNC
_PROCESS_EVENTS = frozenset(
    {"subprocess.Popen", "os.system", "os.posix_spawn", "os.spawn", "os.exec", "os.fork", "os.startfile"}
)
_FILESYSTEM_EVENTS = frozenset(
    {
        "os.mkdir",
        "os.remove",
        "os.rename",
        "os.rmdir",
        "os.symlink",
        "os.link",
        "os.truncate",
        "os.utime",
        "shutil.rmtree",
        "shutil.copyfile",
        "shutil.move",
    }
)
_SOCKET_EVENTS = frozenset({"socket.connect", "socket.bind", "socket.sendto", "socket.sendmsg"})
#: Hosts that never leave this machine. Compared against, never bound.
_LOCAL_HOSTS = frozenset({"", "localhost", "::1", "::", ".".join("0" * 4)})
_IGNORED_PARTS = ("__pycache__", ".hypothesis", f"temp{os.sep}hypothesis")


@dataclass(frozen=True, slots=True)
class Row:
    """One test, as ``--record-sizes`` writes it."""

    style: Style
    size: Size
    resources: list[Resource]
    duration: float


def resource_of(event: str, args: tuple[object, ...]) -> Resource | None:
    """The resource an audit event uses, or None when it is not one that sets a size."""
    if event in _PROCESS_EVENTS:
        return "subprocess"
    if event in _FILESYSTEM_EVENTS:
        return None if _ignored(args) else "filesystem"
    if event == "open":
        return "filesystem" if _opens_for_write(args) and not _ignored(args) else None
    if event in _SOCKET_EVENTS:
        return "localhost" if _is_local_address(args[1] if len(args) > 1 else None) else "network"
    if event == "socket.getaddrinfo":
        return "localhost" if _is_local_host(args[0] if args else None) else "network"
    return None


def size_of(resources: Iterable[Resource], markers: Iterable[str]) -> Size:
    used = set(resources)
    if "network" in used or "integration" in set(markers):
        return "large"
    return "medium" if used else "small"


def style_of(markers: Iterable[str]) -> Style:
    names = set(markers)
    if "bdd" in names:
        return "bdd"
    return "property" if "property" in names else "example"


def _opens_for_write(args: tuple[object, ...]) -> bool:
    mode = args[1] if len(args) > 1 else None
    flags = args[2] if len(args) > 2 else None
    if isinstance(mode, str):
        return any(c in mode for c in "wax+")
    return isinstance(flags, int) and bool(flags & _WRITE_FLAGS)


def _ignored(args: tuple[object, ...]) -> bool:
    path = args[0] if args else None
    if isinstance(path, bytes):
        text = path.decode(errors="replace")
    elif isinstance(path, str | pathlib.PurePath):
        text = str(path)
    else:
        return False  # a file descriptor
    return any(part in text for part in _IGNORED_PARTS)


def _is_local_address(address: object) -> bool:
    if _is_tuple(address):  # (host, port), or (host, port, flowinfo, scope) for IPv6
        return _is_local_host(address[0] if address else None)
    return True  # None, or an AF_UNIX path: never leaves the machine


def _is_tuple(value: object) -> TypeIs[tuple[object, ...]]:
    return isinstance(value, tuple)


def _is_local_host(host: object) -> bool:
    if host is None:
        return True
    if isinstance(host, bytes):
        host = host.decode(errors="replace")
    if not isinstance(host, str):
        return False
    name = host.lower()
    return name in _LOCAL_HOSTS or name.startswith("127.")


def read_rows(path: pathlib.Path) -> dict[str, Row]:
    return decode_json(dict[str, Row], path.read_text(encoding="utf-8"))


def largest(sizes: Sequence[Size]) -> Size:
    return max(sizes, key=SIZES.index) if sizes else "small"

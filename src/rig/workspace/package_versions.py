"""Version ordering for registry versions: SemVer-ish, tolerant of NuGet's four-part and legacy forms."""

from __future__ import annotations

import re
from dataclasses import dataclass


def is_stable_version(version: str) -> bool:
    """True if version has no prerelease identifier (e.g., '1.0.0' or '1.0.0+build1')."""
    return "-" not in version.partition("+")[0]


_CHUNK_RE = re.compile(r"\d+|\D+")


@dataclass(slots=True, frozen=True, order=True)
class _PreChunk:
    kind: int  # 0 for numeric, 1 for string
    num: int = 0
    text: str = ""


@dataclass(slots=True, frozen=True, order=True)
class _VersionKey:
    parts: tuple[int, ...]
    released: bool  # a release sorts after its prereleases
    pre_parts: tuple[_PreChunk, ...] = ()


def version_key(version: str) -> _VersionKey:
    core, _, pre = version.partition("-")
    core_parts = tuple(int(p) if p.isdigit() else 0 for p in core.split("."))
    if not pre:
        return _VersionKey(core_parts, True, ())
    chunks: list[_PreChunk] = []
    matches: list[str] = _CHUNK_RE.findall(pre)
    for c in matches:
        c_clean: str = c.strip(".-_+")
        if not c_clean:
            continue
        if c_clean.isdigit():
            chunks.append(_PreChunk(kind=0, num=int(c_clean)))
        else:
            chunks.append(_PreChunk(kind=1, text=c_clean.lower()))
    return _VersionKey(core_parts, False, tuple(chunks))


def is_ahead(manifest: str, registry: str) -> bool:
    return version_key(manifest) > version_key(registry)

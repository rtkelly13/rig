"""The pinned collector binary: which release, where it goes, and installing it safely.

The always-on collector is the official **core** distribution (`otelcol`), not
contrib. Measured on an M-series Mac with the config in `collector_config`:
22 MB resident after start, 3 MB after two idle minutes, 0.03 s of CPU in those
two minutes; contrib was 131 MB / 37 MB for the same work. Core carries the OTLP
receiver, the batch processor and the rotating `file` exporter, which is all the
always-on half needs. Contrib (for the ClickHouse exporter) is phase 2, on demand.

Every archive's sha256 is pinned here from the GitHub release's asset digests,
and checked **before** anything is extracted. A mismatch deletes the download
and raises; no binary from it is ever written, let alone run.
"""

from __future__ import annotations

import hashlib
import pathlib
import platform
import shutil
import sys
import tarfile
import urllib.request
from collections.abc import Callable
from dataclasses import dataclass
from http.client import HTTPResponse

from rig.otel_stack.paths import otel_home

VERSION = "0.162.0"
_RELEASES_URL = "https://github.com/open-telemetry/opentelemetry-collector-releases/releases/download"


@dataclass(frozen=True, slots=True)
class Release:
    asset: str
    sha256: str
    binary: str


RELEASES: dict[str, Release] = {
    "darwin_arm64": Release(
        "otelcol_0.162.0_darwin_arm64.tar.gz",
        "53de34c02913f54aa240861e0febf67a6839bc8ecf1bfab83a8ae7d355d30674",
        "otelcol",
    ),
    "darwin_amd64": Release(
        "otelcol_0.162.0_darwin_amd64.tar.gz",
        "7292fdb7673283b5fa4422c33703b846011426f79ac7b0505ed9c314a256870d",
        "otelcol",
    ),
    "linux_amd64": Release(
        "otelcol_0.162.0_linux_amd64.tar.gz",
        "f99929987a915d3c6b2c9b15bc4938c5cea903a37a3f49e478024a0fa0772339",
        "otelcol",
    ),
    "windows_amd64": Release(
        "otelcol_0.162.0_windows_amd64.tar.gz",
        "480381f40e182e638ac49def6c28ef6c60bcb8b342056dd9d0b9b906146b8f82",
        "otelcol.exe",
    ),
}

_OS = {"darwin": "darwin", "linux": "linux", "win32": "windows"}
_ARCH = {"arm64": "arm64", "aarch64": "arm64", "x86_64": "amd64", "amd64": "amd64"}

type Fetch = Callable[[str, pathlib.Path], None]
_URLOPEN: Callable[..., HTTPResponse] = urllib.request.urlopen


class UnsupportedPlatformError(RuntimeError):
    pass


class DigestMismatchError(RuntimeError):
    pass


def platform_key() -> str:
    key = f"{_OS.get(sys.platform, sys.platform)}_{_ARCH.get(platform.machine().lower(), platform.machine())}"
    if key not in RELEASES:
        raise UnsupportedPlatformError(f"no pinned otelcol {VERSION} build for {key}")
    return key


def release(key: str | None = None) -> Release:
    return RELEASES[key or platform_key()]


def install_dir(home: pathlib.Path | None = None) -> pathlib.Path:
    return (home or otel_home()) / "bin" / VERSION


def binary_path(key: str | None = None, home: pathlib.Path | None = None) -> pathlib.Path:
    return install_dir(home) / release(key).binary


def _marker(home: pathlib.Path | None) -> pathlib.Path:
    return install_dir(home) / ".sha256"


def verified(key: str | None = None, home: pathlib.Path | None = None) -> bool:
    """Whether the installed binary came from an archive whose digest matched the pin."""
    marker = _marker(home)
    if not binary_path(key, home).is_file() or not marker.is_file():
        return False
    return marker.read_text(encoding="utf-8").strip() == release(key).sha256


def download(url: str, dest: pathlib.Path) -> None:
    """Stream *url* to *dest*. HTTPS only: `urlopen` would also read `file://` paths."""
    if not url.startswith("https://"):
        raise ValueError(f"refusing non-HTTPS download: {url}")
    with _URLOPEN(url, timeout=120) as response, dest.open("wb") as out:
        shutil.copyfileobj(response, out)


def sha256_of(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def install(*, key: str | None = None, home: pathlib.Path | None = None, fetch: Fetch = download) -> pathlib.Path:
    """Download, verify and unpack the pinned binary. Idempotent: a verified install is left as is."""
    rel = release(key)
    target = binary_path(key, home)
    if verified(key, home):
        return target
    folder = install_dir(home)
    folder.mkdir(parents=True, exist_ok=True)
    archive = folder / f"{rel.asset}.part"
    try:
        fetch(f"{_RELEASES_URL}/v{VERSION}/{rel.asset}", archive)
        actual = sha256_of(archive)
        if actual != rel.sha256:
            raise DigestMismatchError(f"{rel.asset}: sha256 {actual}, pinned {rel.sha256}; nothing was extracted")
        _extract_binary(archive, rel.binary, target)
    finally:
        archive.unlink(missing_ok=True)
    _ = _marker(home).write_text(rel.sha256 + "\n", encoding="utf-8")
    return target


def _extract_binary(archive: pathlib.Path, name: str, target: pathlib.Path) -> None:
    """Write only the archive's top-level *name* to *target*; nothing else in the archive is touched."""
    with tarfile.open(archive, "r:gz") as tar:
        member = tar.getmember(name)
        source = tar.extractfile(member) if member.isfile() else None
        if source is None:
            raise DigestMismatchError(f"{archive.name} has no regular file {name}")
        partial = target.with_suffix(target.suffix + ".tmp")
        with source, partial.open("wb") as out:
            shutil.copyfileobj(source, out)
    partial.chmod(0o755)
    _ = partial.replace(target)

"""The on-demand binaries: ClickHouse and the contrib collector, pinned and verified like `collector`.

Neither is downloaded until `otel-stack clickhouse up` (or `install`) first asks
for it, and neither runs outside `up`..`down`. Each archive's sha256 is pinned
from the GitHub release's asset digest and checked **before** anything is
unpacked; a mismatch deletes the download and raises.

- **ClickHouse** `v26.8.23.6-lts` (2026-10-10). macOS ships a bare binary; Linux
  a tarball holding `usr/bin/clickhouse`. There is no native Windows build
  (ClickHouse runs there only under WSL or Docker), so Windows has no pin and
  `up` says so instead of half-starting.
- **otelcol-contrib** `0.162.0`, the same release as the always-on core
  collector. It carries the `clickhouse` exporter, which creates the OTel
  tables; the core build does not.
"""

from __future__ import annotations

import pathlib
import shutil
import tarfile
from dataclasses import dataclass

from rig.otel_stack.collector import (
    VERSION as COLLECTOR_VERSION,
)
from rig.otel_stack.collector import (
    DigestMismatchError,
    Fetch,
    UnsupportedPlatformError,
    download,
    platform_key,
    sha256_of,
)
from rig.otel_stack.paths import otel_home

CLICKHOUSE_VERSION = "26.8.23.6-lts"
_CLICKHOUSE_URL = f"https://github.com/ClickHouse/ClickHouse/releases/download/v{CLICKHOUSE_VERSION}"
_CONTRIB_URL = (
    f"https://github.com/open-telemetry/opentelemetry-collector-releases/releases/download/v{COLLECTOR_VERSION}"
)


@dataclass(frozen=True, slots=True)
class Pin:
    """One downloadable artifact. *member* is the binary's path inside a tarball; empty for a bare binary."""

    url: str
    sha256: str
    binary: str
    member: str = ""


CLICKHOUSE: dict[str, Pin] = {
    "darwin_arm64": Pin(
        f"{_CLICKHOUSE_URL}/clickhouse-macos-aarch64",
        "ffd86ef015a85753916d6cffeb202c531109b5f35ef485a2ec87805aff5cb66a",
        "clickhouse",
    ),
    "darwin_amd64": Pin(
        f"{_CLICKHOUSE_URL}/clickhouse-macos",
        "069fb0f152bbfd1934b9f5194e8d503459b343637b86c3ed9996902d13926f18",
        "clickhouse",
    ),
    "linux_amd64": Pin(
        f"{_CLICKHOUSE_URL}/clickhouse-common-static-26.8.23.6-amd64.tgz",
        "14e50330a5b3485e18bb0c6b81637fc1a91567a8eb4ee43062cee5dc552d593b",
        "clickhouse",
        member="clickhouse-common-static-26.8.23.6/usr/bin/clickhouse",
    ),
}

CONTRIB: dict[str, Pin] = {
    "darwin_arm64": Pin(
        f"{_CONTRIB_URL}/otelcol-contrib_0.162.0_darwin_arm64.tar.gz",
        "d5e11974d2e4adac3cc001a25137aad52f6e77ec3034ba7735cac7c219a5f97a",
        "otelcol-contrib",
        member="otelcol-contrib",
    ),
    "darwin_amd64": Pin(
        f"{_CONTRIB_URL}/otelcol-contrib_0.162.0_darwin_amd64.tar.gz",
        "91a6e7a0f5e1981986c897db4535de5ba8d7fc45d4205809aecb9ebebf68c71c",
        "otelcol-contrib",
        member="otelcol-contrib",
    ),
    "linux_amd64": Pin(
        f"{_CONTRIB_URL}/otelcol-contrib_0.162.0_linux_amd64.tar.gz",
        "fcc063749f730f8c21fe29f2d340ff174f5f1c5885bd3156fb6c985a3036fcc3",
        "otelcol-contrib",
        member="otelcol-contrib",
    ),
    "windows_amd64": Pin(
        f"{_CONTRIB_URL}/otelcol-contrib_0.162.0_windows_amd64.tar.gz",
        "b371829181e89947a1a6570b8b6c38c20d85e2d215b76bf042f89b365b9e5c3d",
        "otelcol-contrib.exe",
        member="otelcol-contrib.exe",
    ),
}


def clickhouse_pin(key: str | None = None) -> Pin:
    platform = key or platform_key()
    pin = CLICKHOUSE.get(platform)
    if pin is None:
        raise UnsupportedPlatformError(
            f"ClickHouse has no native build for {platform}; on Windows run it under WSL or Docker"
        )
    return pin


def contrib_pin(key: str | None = None) -> Pin:
    platform = key or platform_key()
    pin = CONTRIB.get(platform)
    if pin is None:
        raise UnsupportedPlatformError(f"no pinned otelcol-contrib {COLLECTOR_VERSION} build for {platform}")
    return pin


def pin_dir(pin: Pin, home: pathlib.Path | None = None) -> pathlib.Path:
    """One folder per pinned digest, so a new pin never mixes with an old binary."""
    return (home or otel_home()) / "bin" / pin.sha256[:16]


def binary(pin: Pin, home: pathlib.Path | None = None) -> pathlib.Path:
    return pin_dir(pin, home) / pin.binary


def verified(pin: Pin, home: pathlib.Path | None = None) -> bool:
    marker = pin_dir(pin, home) / ".sha256"
    if not binary(pin, home).is_file() or not marker.is_file():
        return False
    return marker.read_text(encoding="utf-8").strip() == pin.sha256


def install(pin: Pin, *, home: pathlib.Path | None = None, fetch: Fetch = download) -> pathlib.Path:
    """Download, verify, then unpack *pin*'s binary. Idempotent: a verified install is left as is."""
    target = binary(pin, home)
    if verified(pin, home):
        return target
    folder = pin_dir(pin, home)
    folder.mkdir(parents=True, exist_ok=True)
    download_to = folder / f"{pin.binary}.download"
    try:
        fetch(pin.url, download_to)
        actual = sha256_of(download_to)
        if actual != pin.sha256:
            raise DigestMismatchError(f"{pin.url}: sha256 {actual}, pinned {pin.sha256}; nothing was unpacked")
        partial = target.with_name(target.name + ".tmp")
        if pin.member:
            _unpack_member(download_to, pin.member, partial)
        else:
            _ = download_to.replace(partial)
        partial.chmod(0o755)
        _ = partial.replace(target)
    finally:
        download_to.unlink(missing_ok=True)
    _ = (folder / ".sha256").write_text(pin.sha256 + "\n", encoding="utf-8")
    return target


def _unpack_member(archive: pathlib.Path, member: str, target: pathlib.Path) -> None:
    """Write only *member* of the gzipped tarball to *target*; nothing else in it is touched."""
    with tarfile.open(archive, "r:gz") as tar:
        info = tar.getmember(member)
        source = tar.extractfile(info) if info.isfile() else None
        if source is None:
            raise DigestMismatchError(f"{archive.name} has no regular file {member}")
        with source, target.open("wb") as out:
            shutil.copyfileobj(source, out)

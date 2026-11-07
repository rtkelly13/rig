from __future__ import annotations

import os
import pathlib
import subprocess
import time
from collections.abc import Callable, Sequence

import pytest

from rig.estate import caches
from rig.estate.caches import Pruned, Status
from rig.estate.disksize import footprint

pytestmark = pytest.mark.ci

DAY = 86400.0
SESSION = "0f8fad5b-d9cb-469f-a165-70867728950e"
OTHER_SESSION = "7c9e6679-7425-40de-944b-e07fc1f90ae7"

type Responder = Callable[[list[str]], subprocess.CompletedProcess[str]]


def _done(argv: list[str], returncode: int = 0, stdout: str = "", stderr: str = "") -> subprocess.CompletedProcess[str]:
    return subprocess.CompletedProcess(argv, returncode, stdout, stderr)


class FakeTools:
    """Stands in for `subprocess.run` and `shutil.which`, recording each command run."""

    def __init__(self, installed: Sequence[str], respond: Responder) -> None:
        self.installed: frozenset[str] = frozenset(installed)
        self.respond: Responder = respond
        self.calls: list[list[str]] = []

    def which(self, name: str, mode: int = os.F_OK | os.X_OK, path: str | None = None) -> str | None:
        _ = (mode, path)
        return name if name in self.installed else None

    def run(self, argv: list[str], **_: object) -> subprocess.CompletedProcess[str]:
        self.calls.append(argv)
        return self.respond(argv)


def _install(monkeypatch: pytest.MonkeyPatch, tools: FakeTools) -> FakeTools:
    monkeypatch.setattr(caches.shutil, "which", tools.which)
    monkeypatch.setattr(caches.subprocess, "run", tools.run)
    return tools


def _fill(directory: pathlib.Path, name: str, size: int) -> pathlib.Path:
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / name
    _ = path.write_bytes(b"x" * size)
    return path


def test_dry_run_locates_and_measures_without_pruning(monkeypatch: pytest.MonkeyPatch, tmp_path: pathlib.Path) -> None:
    uv_cache = tmp_path / "uv"
    _ = _fill(uv_cache, "wheel", 1000)
    nuget = tmp_path / "nuget"
    _ = _fill(nuget, "pkg", 300)

    def respond(argv: list[str]) -> subprocess.CompletedProcess[str]:
        if argv[:3] == ["uv", "cache", "dir"]:
            return _done(argv, stdout=f"{uv_cache}\n")
        if argv[:4] == ["dotnet", "nuget", "locals", "http-cache"]:
            return _done(argv, stdout=f"info: something\nhttp-cache: {nuget}\n")
        return _done(argv, 1, stderr="unexpected")

    tools = _install(monkeypatch, FakeTools(["uv", "dotnet"], respond))

    results = [caches.prune_tool_cache(c, apply=False) for c in caches.TOOL_CACHES]

    assert results == [
        Pruned("uv cache", "uv cache prune", Status.WOULD_RUN, str(uv_cache), 1000, None),
        Pruned("pnpm store", "pnpm store prune", Status.SKIPPED, "pnpm not found on PATH"),
        Pruned("nuget http-cache", "dotnet nuget locals http-cache --clear", Status.WOULD_RUN, str(nuget), 300, 300),
    ]
    assert tools.calls == [["uv", "cache", "dir"], ["dotnet", "nuget", "locals", "http-cache", "--list"]]


def test_apply_reports_bytes_freed_from_before_and_after(
    monkeypatch: pytest.MonkeyPatch, tmp_path: pathlib.Path
) -> None:
    store = tmp_path / "pnpm"
    _ = _fill(store, "kept", 200)
    unused = _fill(store, "unused", 800)

    def respond(argv: list[str]) -> subprocess.CompletedProcess[str]:
        if argv[1:] == ["store", "path"]:
            return _done(argv, stdout=str(store))
        if argv[1:] == ["store", "prune"]:
            unused.unlink()
            return _done(argv, stdout="Removed 1 package")
        return _done(argv, 1)

    tools = _install(monkeypatch, FakeTools(["pnpm"], respond))

    result = caches.prune_tool_cache(caches.PNPM, apply=True)

    assert result == Pruned("pnpm store", "pnpm store prune", Status.DONE, str(store), 1000, 800)
    assert tools.calls == [["pnpm", "store", "path"], ["pnpm", "store", "prune"]]


def test_a_failing_prune_is_reported_with_its_error(monkeypatch: pytest.MonkeyPatch, tmp_path: pathlib.Path) -> None:
    cache = tmp_path / "uv"
    _ = _fill(cache, "wheel", 10)

    def respond(argv: list[str]) -> subprocess.CompletedProcess[str]:
        if argv[1:] == ["cache", "dir"]:
            return _done(argv, stdout=str(cache))
        return _done(argv, 2, stderr="error: cache is locked\n")

    _ = _install(monkeypatch, FakeTools(["uv"], respond))

    assert caches.prune_tool_cache(caches.UV, apply=True) == Pruned(
        "uv cache", "uv cache prune", Status.FAILED, "error: cache is locked", 10
    )


def test_a_command_that_cannot_start_does_not_raise(monkeypatch: pytest.MonkeyPatch) -> None:
    def explode(argv: list[str], **_: object) -> subprocess.CompletedProcess[str]:
        raise FileNotFoundError(2, "No such file", argv[0])

    monkeypatch.setattr(caches.subprocess, "run", explode)

    result = caches.run(["missing-tool"])

    assert (result.returncode, result.stdout) == (caches.NOT_STARTED, "")


def test_docker_is_skipped_when_its_daemon_is_down(monkeypatch: pytest.MonkeyPatch) -> None:
    def respond(argv: list[str]) -> subprocess.CompletedProcess[str]:
        return _done(argv, 1, stderr="Cannot connect to the Docker daemon")

    tools = _install(monkeypatch, FakeTools(["docker"], respond))

    assert caches.prune_docker(apply=True) == Pruned(
        "docker build cache",
        "docker builder prune -f --filter until=168h",
        Status.SKIPPED,
        "Docker daemon not running",
    )
    assert tools.calls == [["docker", "info", "--format", "{{.ServerVersion}}"]]


def test_docker_prune_reports_its_total(monkeypatch: pytest.MonkeyPatch) -> None:
    def respond(argv: list[str]) -> subprocess.CompletedProcess[str]:
        if argv[1] == "info":
            return _done(argv, stdout="29.5.3")
        return _done(argv, stdout="ID\tRECLAIMABLE\nabc\t1GB\nTotal:\t1.5GB\n")

    tools = _install(monkeypatch, FakeTools(["docker"], respond))

    result = caches.prune_docker(apply=True)

    assert result == Pruned(
        "docker build cache", "docker builder prune -f --filter until=168h", Status.DONE, freed=1_500_000_000
    )
    assert tools.calls[-1] == ["docker", "builder", "prune", "-f", "--filter", "until=168h"]


@pytest.mark.parametrize(
    ("output", "expected"),
    [
        ("Total:\t0B\n", 0),
        ("Total reclaimed space: 12.5MB", 12_500_000),
        ("Total:  3kB", 3000),
        ("nothing to prune", None),
    ],
)
def test_docker_reclaimed_parses_decimal_units(output: str, expected: int | None) -> None:
    assert caches.docker_reclaimed(output) == expected


def _session(root: pathlib.Path, project: str, name: str, *, size: int, when: float) -> pathlib.Path:
    session = root / project / name
    blob = _fill(session / "scratchpad", "notes.txt", size)
    for path in (blob, blob.parent, session):
        os.utime(path, (when, when))
    return session


def test_claude_temp_removes_only_idle_session_dirs(tmp_path: pathlib.Path) -> None:
    now = time.time()
    root = tmp_path / "claude"
    stale = _session(root, "C--code-workspace", SESSION, size=500, when=now - 9 * DAY)
    fresh = _session(root, "C--code-workspace", OTHER_SESSION, size=70, when=now - 1 * DAY)
    named = _session(root, "C--code-workspace", "not-a-session", size=30, when=now - 30 * DAY)

    dry = caches.prune_claude_temp(root, apply=False, idle_days=7, now=now)
    applied = caches.prune_claude_temp(root, apply=True, idle_days=7, now=now)

    command = f"delete session dirs idle 7+ days under {root}"
    assert (dry, applied) == (
        Pruned("claude temp", command, Status.WOULD_RUN, "1 idle session dir(s)", 500, 500),
        Pruned("claude temp", command, Status.DONE, "1 session dir(s) removed", 500, 500),
    )
    assert (stale.exists(), fresh.exists(), named.exists()) == (False, True, True)


def test_a_recent_write_anywhere_in_a_session_keeps_it(tmp_path: pathlib.Path) -> None:
    now = time.time()
    root = tmp_path / "claude"
    session = _session(root, "p", SESSION, size=10, when=now - 9 * DAY)
    _ = _fill(session / "tasks", "out.txt", 5)

    assert caches.idle_sessions(root, idle_days=7, now=now) == []


def test_prune_all_runs_every_category_in_order(monkeypatch: pytest.MonkeyPatch, tmp_path: pathlib.Path) -> None:
    _ = _install(monkeypatch, FakeTools([], lambda argv: _done(argv, 1)))

    results = caches.prune_all(apply=False, claude_temp=tmp_path / "absent")

    assert results == [
        Pruned("uv cache", "uv cache prune", Status.SKIPPED, "uv not found on PATH"),
        Pruned("pnpm store", "pnpm store prune", Status.SKIPPED, "pnpm not found on PATH"),
        Pruned(
            "nuget http-cache", "dotnet nuget locals http-cache --clear", Status.SKIPPED, "dotnet not found on PATH"
        ),
        Pruned(
            "claude temp",
            f"delete session dirs idle 7+ days under {tmp_path / 'absent'}",
            Status.SKIPPED,
            f"{tmp_path / 'absent'} does not exist",
        ),
        Pruned(
            "docker build cache",
            "docker builder prune -f --filter until=168h",
            Status.SKIPPED,
            "docker not found on PATH",
        ),
    ]


@pytest.mark.skipif(os.name != "nt", reason="junctions are a Windows feature")
def test_footprint_does_not_follow_junctions(tmp_path: pathlib.Path) -> None:
    elsewhere = _fill(tmp_path / "elsewhere", "big", 5000).parent
    checkout = tmp_path / "checkout"
    _ = _fill(checkout, "own", 100)
    (checkout / "projects").mkdir()
    link = checkout / "projects" / "clone"
    _ = subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(elsewhere)], check=True, capture_output=True)

    assert footprint(checkout).bytes_used == 100

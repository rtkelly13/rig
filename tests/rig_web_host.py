"""The C# ``rig web`` host in front of Python web routes served in-process, for the browser tests.

The browser tests fake the Python routes' state in-process (monkeypatched GitHub, a temp database), so
the C# host is told to front those routes (``RIG_WEB_PYTHON_ROUTES``) rather than start its own child.
The browser then talks to the C# host only, as it does in production.

The host reads nothing of this machine's: its checkout (``RIG_HOME``) is a temp folder holding a copy of
the frontend and, when a test lays one out with `estate`, a registry; its code root (``CODE_ROOT``) is
the temp ``code/`` folder beside it, whose ``personal/`` holds that estate's git checkouts. It runs from
the checkout, and the suite's ``LOCALAPPDATA`` is already a temp folder (tests/conftest.py). It binds
port 0 and is stopped by the pid this module started.

The CLI is built once per run: CI builds it before pytest and names the assembly in ``RIG_CLI_DLL``;
locally the first worker to get here builds it into ``temp/rig-web-tests`` while the others wait.
"""

from __future__ import annotations

import functools
import json
import os
import queue
import re
import shutil
import subprocess
import threading
import time
import urllib.request
from collections.abc import Generator
from contextlib import contextmanager
from pathlib import Path
from typing import IO

import pytest

from rig import tracing

REPO = Path(__file__).resolve().parents[1]
TEMPLATES = REPO / "src" / "rig" / "web_host" / "templates"
_BUILD = REPO / "temp" / "rig-web-tests"
_BANNER = re.compile(r"Rig Web Dashboard running at (http://\S+?)/?$")
_START = 60.0
_LOCK_WAIT = 600.0


@functools.cache
def _build_cli() -> Path:
    """Build the C# CLI into ``temp/rig-web-tests/cli`` once per run; return ``rig.dll``.

    The cache keeps a worker to one build; under xdist, a marker named for the run keeps the other workers from
    building again once one has, checked after taking the lock that makes them wait for it.
    """
    _BUILD.mkdir(parents=True, exist_ok=True)
    output = _BUILD / "cli"
    run = os.environ.get("PYTEST_XDIST_TESTRUNUID")
    marker = _BUILD / f"built-{run}" if run else None
    lock = _BUILD / "build.lock"
    deadline = time.monotonic() + _LOCK_WAIT
    while True:
        try:
            lock.mkdir()
            break
        except FileExistsError:
            if time.monotonic() > deadline:
                pytest.fail(f"another worker held {lock} too long; remove it if no test run is building")
            time.sleep(0.5)
    try:
        if marker is not None and marker.exists():
            return output / "rig.dll"
        for stale in _BUILD.glob("built-*"):
            stale.unlink()
        try:
            built = tracing.run(
                ["dotnet", "build", str(REPO / "src" / "Rig.Cli"), "-m:2", "-v", "q", "-o", str(output)],
                capture_output=True,
                encoding="utf-8",
                errors="replace",
            )
        except FileNotFoundError:
            pytest.fail("the browser tests run against the C# rig web host, which needs dotnet on PATH")
        if built.returncode != 0:
            pytest.fail(f"building the C# rig CLI failed:\n{built.stdout}\n{built.stderr}")
        if marker is not None:
            marker.touch()
        return output / "rig.dll"
    finally:
        lock.rmdir()


def cli_assembly() -> Path:
    """The built C# CLI: ``RIG_CLI_DLL`` when CI built it, else a local build."""
    configured = os.environ.get("RIG_CLI_DLL")
    if configured:
        return Path(configured).resolve()
    return _build_cli()


def checkout(root: Path) -> Path:
    """A rig checkout under *root* holding a copy of the frontend and nothing else: no registry, no brand assets.

    Beside it is an empty code root, ``code/`` with an AGENTS.md and ``personal/``, that `fronting` points the host at.
    """
    home = root / "rig-checkout"
    _ = shutil.copytree(TEMPLATES, home / "src" / "rig" / "web_host" / "templates")
    (home / "assets").mkdir()
    (_code_root(home) / "personal").mkdir(parents=True)
    _ = (_code_root(home) / "AGENTS.md").write_text("# code root\n", encoding="utf-8")
    return home


def estate(home: Path, projects: dict[str, dict[str, str]]) -> None:
    """Lay out an estate for the checkout *home*: each project ``owner/<name>`` in the registry, and a git checkout
    under the code root's ``personal/<name>`` holding its manifests (path to text), staged so git lists them."""
    entries = [
        {"name": name, "repo": f"https://github.com/owner/{name}", "description": name.title(), "domain": "Libraries"}
        for name in projects
    ]
    registry = home / "config" / "managed-projects.json"
    registry.parent.mkdir(parents=True, exist_ok=True)
    _ = registry.write_text(json.dumps({"projects": entries}), encoding="utf-8")
    for name, files in projects.items():
        folder = _code_root(home) / "personal" / name
        folder.mkdir(parents=True)
        for relative, text in files.items():
            _ = (folder / relative).write_text(text, encoding="utf-8")
        _ = tracing.run(["git", "init", "--quiet"], cwd=folder, check=True)
        _ = tracing.run(["git", "add", "-f", "."], cwd=folder, check=True)


def _code_root(home: Path) -> Path:
    return home.parent / "code"


def _warm(address: str) -> None:
    """Ask the host for its own route and a page once, so the first page a test loads is not also its JIT warm-up.

    Each test starts a fresh host, and on a loaded Windows runner its first answers took longer than Playwright's
    five-second expectations.
    """
    for path in ("/api/status", "/"):
        with urllib.request.urlopen(address + path, timeout=_START) as answer:  # noqa: S310 - the loopback host above
            _ = answer.read()


def _lines(stream: IO[str], into: queue.Queue[str | None]) -> None:
    for line in stream:
        into.put(line.rstrip())
    into.put(None)


@contextmanager
def fronting(python_routes: str, home: Path) -> Generator[str]:
    """Run ``rig web`` on a free port in front of *python_routes*, from the checkout *home*; yield its address."""
    env = {
        **os.environ,
        "RIG_HOME": str(home),
        "CODE_ROOT": str(_code_root(home)),
        "RIG_WEB_PYTHON_ROUTES": python_routes,
        "DOTNET_NOLOGO": "1",
    }
    # A server this module owns and stops; tracing.run only waits for a process to finish.
    process = subprocess.Popen(  # noqa: TID251
        ["dotnet", str(cli_assembly()), "web", "--port", "0", "--host", "127.0.0.1", "--no-open"],
        cwd=home,
        env=env,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    lines: queue.Queue[str | None] = queue.Queue()
    assert process.stdout is not None
    reader = threading.Thread(target=_lines, args=(process.stdout, lines), daemon=True)
    reader.start()
    seen: list[str] = []
    try:
        deadline = time.monotonic() + _START
        address = None
        while address is None:
            try:
                line = lines.get(timeout=max(0.0, deadline - time.monotonic()))
            except queue.Empty:
                pytest.fail("rig web did not start in time:\n" + "\n".join(seen))
            if line is None:
                pytest.fail(f"rig web exited with {process.wait()} before serving:\n" + "\n".join(seen))
            seen.append(line)
            if match := _BANNER.search(line):
                address = match.group(1)
        _warm(address)
        yield address
    finally:
        # Only the process started above is ever signalled.
        process.terminate()
        try:
            _ = process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            _ = process.wait()
        reader.join(timeout=5)

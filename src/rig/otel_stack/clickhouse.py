"""Running the on-demand pair: ClickHouse, and the contrib collector that writes into it.

Nothing here is scheduled. `up` installs (pinned, verified) what is missing,
writes the configs, starts ClickHouse and waits for it to answer, then starts
the contrib collector and waits for its port. `down` stops both and waits until
they have exited, so "down" means nothing is left running. The always-on core
collector is untouched by either.

Both run detached, with their pids recorded beside the configs, and their output
appended to one log each. A pid file whose process has gone is treated as not
running and cleaned up.
"""

from __future__ import annotations

import contextlib
import os
import pathlib
import signal
import subprocess
import sys
import time
from collections.abc import Callable
from dataclasses import dataclass

from rig.otel_stack import clickhouse_config, pinned
from rig.otel_stack.collector import Fetch, download
from rig.otel_stack.paths import clickhouse_data, clickhouse_home, clickhouse_log, contrib_log
from rig.otel_stack.query import ClickHouse
from rig.otel_stack.runtime import listening, resident_mb


@dataclass(frozen=True, slots=True)
class Part:
    """One of the two processes `up` starts."""

    name: str
    pid_file: pathlib.Path
    log: pathlib.Path


def server_part() -> Part:
    return Part("clickhouse", clickhouse_home() / "clickhouse.pid", clickhouse_log())


def collector_part() -> Part:
    return Part("otelcol-contrib", clickhouse_home() / "otelcol-contrib.pid", contrib_log())


def parts() -> list[Part]:
    return [server_part(), collector_part()]


class StartupError(RuntimeError):
    """A process was started but did not come up in time; the log says why."""


@dataclass(frozen=True, slots=True)
class Configs:
    """The config files `up` writes beside the data, regenerated on every start."""

    server: pathlib.Path
    users: pathlib.Path
    collector: pathlib.Path


def write_configs(home: pathlib.Path | None = None) -> Configs:
    folder = home or clickhouse_home()
    data = folder / "data"
    data.mkdir(parents=True, exist_ok=True)
    written = Configs(
        server=folder / "server.xml", users=folder / "users.xml", collector=folder / "otelcol-contrib.yaml"
    )
    _ = written.server.write_text(clickhouse_config.server_xml(data, written.users), encoding="utf-8")
    _ = written.users.write_text(clickhouse_config.users_xml(), encoding="utf-8")
    _ = written.collector.write_text(clickhouse_config.collector_yaml(), encoding="utf-8")
    return written


def alive(pid: int) -> bool:
    """Whether *pid* exists. Never on Windows, where nothing here starts and signal 0 is CTRL_C_EVENT."""
    if sys.platform == "win32":
        return False
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def running_pid(part: Part) -> int | None:
    """The recorded pid when that process still exists; a stale pid file is removed."""
    try:
        text = part.pid_file.read_text(encoding="utf-8").strip()
    except OSError:
        return None
    pid = int(text) if text.isdigit() else None
    if pid is not None and alive(pid):
        return pid
    part.pid_file.unlink(missing_ok=True)
    return None


#: ClickHouse otherwise forks itself under a watchdog parent, so the recorded pid would be the
#: watchdog's, its memory a misleading 2 MB, and there would be one more process awake.
_CLICKHOUSE_ENV = {"CLICKHOUSE_WATCHDOG_ENABLE": "0"}


def _spawn(part: Part, argv: list[str], cwd: pathlib.Path, env: dict[str, str] | None = None) -> int:
    part.log.parent.mkdir(parents=True, exist_ok=True)
    with part.log.open("ab") as out:
        process = subprocess.Popen(
            argv,
            cwd=cwd,
            stdin=subprocess.DEVNULL,
            stdout=out,
            stderr=subprocess.STDOUT,
            start_new_session=True,
            env={**os.environ, **env} if env else None,
        )
    _ = part.pid_file.write_text(f"{process.pid}\n", encoding="utf-8")
    return process.pid


def _wait(ready: Callable[[], bool], part: Part, seconds: float, sleep: Callable[[float], None]) -> None:
    deadline = time.monotonic() + seconds
    while not ready():
        if running_pid(part) is None:
            raise StartupError(f"{part.name} exited during startup; see {part.log}")
        if time.monotonic() >= deadline:
            raise StartupError(f"{part.name} did not come up within {seconds:.0f}s; see {part.log}")
        sleep(0.5)


@dataclass(frozen=True, slots=True)
class UpResult:
    started: list[str]
    already: list[str]


def up(*, fetch: Fetch = download, wait_seconds: float = 60, sleep: Callable[[float], None] = time.sleep) -> UpResult:
    """Install what is missing, then start ClickHouse and the contrib collector unless already running."""
    clickhouse_bin = pinned.install(pinned.clickhouse_pin(), fetch=fetch)
    contrib_bin = pinned.install(pinned.contrib_pin(), fetch=fetch)
    configs = write_configs()
    started: list[str] = []
    already: list[str] = []

    server = server_part()
    if running_pid(server) is None:
        _ = _spawn(
            server,
            [str(clickhouse_bin), "server", f"--config-file={configs.server}"],
            clickhouse_data(),
            _CLICKHOUSE_ENV,
        )
        started.append(server.name)
    else:
        already.append(server.name)
    with ClickHouse.local() as client:
        _wait(client.ping, server, wait_seconds, sleep)

    collector = collector_part()
    if running_pid(collector) is None:
        _ = _spawn(collector, [str(contrib_bin), "--config", str(configs.collector)], clickhouse_home())
        started.append(collector.name)
    else:
        already.append(collector.name)
    _wait(lambda: listening(clickhouse_config.COLLECTOR_PORT), collector, wait_seconds, sleep)
    return UpResult(started=started, already=already)


def down(*, wait_seconds: float = 30, sleep: Callable[[float], None] = time.sleep) -> list[str]:
    """Stop the collector, then ClickHouse; wait for each to exit (SIGKILL after *wait_seconds*)."""
    stopped: list[str] = []
    for part in reversed(parts()):
        pid = running_pid(part)
        if pid is None:
            continue
        _signal(pid, signal.SIGTERM)
        deadline = time.monotonic() + wait_seconds
        while alive(pid) and time.monotonic() < deadline:
            sleep(0.5)
        if alive(pid) and sys.platform != "win32":
            _signal(pid, signal.SIGKILL)
        part.pid_file.unlink(missing_ok=True)
        stopped.append(part.name)
    return stopped


def _signal(pid: int, sig: signal.Signals) -> None:
    with contextlib.suppress(OSError):
        os.kill(pid, sig)


@dataclass(frozen=True, slots=True)
class PartStatus:
    name: str
    pid: int | None
    resident_mb: float | None


def status() -> list[PartStatus]:
    statuses: list[PartStatus] = []
    for part in parts():
        pid = running_pid(part)
        statuses.append(PartStatus(part.name, pid, resident_mb(pid) if pid is not None else None))
    return statuses


def data_bytes(folder: pathlib.Path | None = None) -> int:
    root = folder or clickhouse_data()
    if not root.is_dir():
        return 0
    return sum(path.stat().st_size for path in root.rglob("*") if path.is_file() and not path.is_symlink())

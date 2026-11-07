"""Whether the collector is up, and running it without a service manager.

macOS runs it under its LaunchAgent (`service`). Windows has no agent: the jobs
coordinator calls `collector ensure` at logon and every tick, which starts the
binary detached (no console window) only when nothing listens on the HTTP port.

Something else can hold the ports, such as a Docker collector publishing 4317/4318.
`owner` names whoever listens, so `ensure` refuses to treat a foreign collector as
this one; on Windows a loopback bind can even succeed alongside Docker's wildcard
bind, leaving which process gets the traffic up to the OS.
"""

from __future__ import annotations

import contextlib
import os
import pathlib
import re
import shutil
import signal
import socket
import subprocess
import sys
import urllib.parse
from dataclasses import dataclass

from rig.otel_stack.collector_config import HOST, HTTP_PORT, render
from rig.otel_stack.paths import config_path, data_dir, forward_path, log_path, pid_path

#: The image name (Windows) or executable stem the core collector runs as.
COLLECTOR_NAME = "otelcol"
#: `otelcol` as a whole path component, alone or followed by its arguments; paths may hold spaces.
_COLLECTOR_EXECUTABLE = re.compile(rf"(?:^|[/\\]){COLLECTOR_NAME}(?:\.exe)?(?:\s|$)", re.IGNORECASE)


def listening(port: int, host: str = HOST, *, timeout: float = 0.5) -> bool:
    """Whether something accepts TCP connections on *host*:*port*."""
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False


def validate_forward(url: str) -> str:
    """*url* as a forward endpoint: an http(s) base URL with a host and port. Raises ValueError otherwise."""
    parts = urllib.parse.urlsplit(url.strip())
    if parts.scheme not in {"http", "https"} or not parts.hostname or parts.port is None:
        raise ValueError(f"forward endpoint must look like http://127.0.0.1:24318, got {url!r}")
    if parts.path not in {"", "/"} or parts.query or parts.fragment:
        raise ValueError(f"forward endpoint is a base URL; the collector appends /v1/<signal> itself, got {url!r}")
    return f"{parts.scheme}://{parts.netloc}"


def forward() -> str | None:
    """The configured forward endpoint, or None when the collector forwards nowhere."""
    try:
        text = forward_path().read_text(encoding="utf-8").strip()
    except OSError:
        return None
    return validate_forward(text) if text else None


def set_forward(url: str | None) -> str | None:
    """Record *url* as the forward endpoint (None turns forwarding off). Takes effect on the next start."""
    path = forward_path()
    if url is None:
        path.unlink(missing_ok=True)
        return None
    endpoint = validate_forward(url)
    path.parent.mkdir(parents=True, exist_ok=True)
    _ = path.write_text(endpoint + "\n", encoding="utf-8")
    return endpoint


def write_config() -> pathlib.Path:
    path = config_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    data_dir().mkdir(parents=True, exist_ok=True)
    _ = path.write_text(render(forward=forward()), encoding="utf-8")
    return path


def start(binary: pathlib.Path) -> int:
    """Start the collector detached and record its pid. The caller checks `listening` first."""
    config = write_config()
    log = log_path()
    log.parent.mkdir(parents=True, exist_ok=True)
    flags = (subprocess.CREATE_NO_WINDOW | subprocess.DETACHED_PROCESS) if sys.platform == "win32" else 0
    with log.open("ab") as out:
        process = subprocess.Popen(
            [str(binary), "--config", str(config)],
            stdin=subprocess.DEVNULL,
            stdout=out,
            stderr=subprocess.STDOUT,
            creationflags=flags,
            start_new_session=sys.platform != "win32",
        )
    record = Launched(pid=process.pid, binary=str(binary), started=process_started(process.pid))
    _ = pid_path().write_text(record.serialise(), encoding="utf-8")
    return process.pid


@dataclass(frozen=True, slots=True)
class Launched:
    """What `start` launched: enough to tell it from whatever later reuses its pid."""

    pid: int
    binary: str
    #: `ps -o lstart=` at launch; "" where there is no `ps` (Windows).
    started: str = ""

    def serialise(self) -> str:
        return f"{self.pid}\t{self.binary}\t{self.started}\n"

    @staticmethod
    def parse(text: str) -> Launched | None:
        fields = text.rstrip("\n").split("\t")
        if not fields[0].isdigit():
            return None
        binary = fields[1] if len(fields) > 1 else ""
        started = fields[2] if len(fields) > 2 else ""
        return Launched(pid=int(fields[0]), binary=binary, started=started)


def process_started(pid: int) -> str:
    """When *pid* started, as `ps` reports it; "" where that cannot be read."""
    if sys.platform == "win32":
        return ""
    done = subprocess.run(["ps", "-o", "lstart=", "-p", str(pid)], capture_output=True, text=True, check=False)
    return done.stdout.strip()


def _command_of(pid: int) -> str:
    """The executable of *pid*: its full command line on macOS/Linux, its image name on Windows."""
    if sys.platform == "win32":
        done = subprocess.run(
            ["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
            capture_output=True,
            text=True,
            check=False,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        return done.stdout.split(",")[0].strip('"') if done.stdout.startswith('"') else ""
    done = subprocess.run(["ps", "-o", "command=", "-p", str(pid)], capture_output=True, text=True, check=False)
    return done.stdout.strip()


def still_ours(record: Launched) -> bool:
    """Whether *record*'s pid is still the collector it launched, not a later process given the same pid."""
    command = _command_of(record.pid)
    if not command:
        return False
    if sys.platform == "win32":
        return command.lower() == pathlib.Path(record.binary).name.lower()
    return record.binary in command and (not record.started or process_started(record.pid) == record.started)


def recorded() -> Launched | None:
    try:
        text = pid_path().read_text(encoding="utf-8")
    except OSError:
        return None
    return Launched.parse(text)


def recorded_pid() -> int | None:
    record = recorded()
    return record.pid if record is not None and still_ours(record) else None


def parse_netstat_listener(text: str, port: int) -> int | None:
    """The pid in `netstat -ano -p TCP` output listening on *port* on any local address."""
    for line in text.splitlines():
        fields = line.split()
        if (
            len(fields) == 5
            and fields[0] == "TCP"
            and fields[3] == "LISTENING"
            and fields[1].endswith(f":{port}")
            and fields[4].isdigit()
        ):
            return int(fields[4])
    return None


def _windows_listener_pid(port: int) -> int | None:
    done = subprocess.run(
        ["netstat", "-ano", "-p", "TCP"],
        capture_output=True,
        text=True,
        check=False,
        creationflags=subprocess.CREATE_NO_WINDOW,
    )
    return parse_netstat_listener(done.stdout, port)


@dataclass(frozen=True, slots=True)
class Owner:
    """Who listens on a port: its pid, its executable, and whether that is this collector."""

    pid: int
    command: str

    @property
    def ours(self) -> bool:
        return is_collector(self.command)


def is_collector(command: str) -> bool:
    """Whether *command* (an image name or command line) runs the core `otelcol`, not contrib or a container."""
    return _COLLECTOR_EXECUTABLE.search(command) is not None


def owner(port: int = HTTP_PORT) -> Owner | None:
    """Who listens on *port*, where the platform can say; None when nobody does or it cannot be read."""
    pid = _windows_listener_pid(port) if sys.platform == "win32" else listener_pid(port)
    if pid is None:
        return None
    return Owner(pid=pid, command=_command_of(pid))


def listener_pid(port: int = HTTP_PORT) -> int | None:
    """The pid listening on *port* (macOS/Linux via lsof, Windows via netstat), else the one `start` recorded."""
    if sys.platform == "win32":
        found = _windows_listener_pid(port)
        if found is not None:
            return found
    lsof = shutil.which("lsof")
    if lsof is not None and sys.platform != "win32":
        done = subprocess.run(
            [lsof, "-nP", f"-iTCP:{port}", "-sTCP:LISTEN", "-t"], capture_output=True, text=True, check=False
        )
        first = done.stdout.split()[:1]
        if first and first[0].isdigit():
            return int(first[0])
    return recorded_pid()


def resident_mb(pid: int) -> float | None:
    """Resident memory of *pid* in MB, where `ps` exists (not Windows)."""
    if sys.platform == "win32":
        return None
    done = subprocess.run(["ps", "-o", "rss=", "-p", str(pid)], capture_output=True, text=True, check=False)
    text = done.stdout.strip()
    return int(text) / 1024 if text.isdigit() else None


def stop_recorded() -> bool:
    """Stop a collector `start` launched. False when there was none still running.

    A pid file outlives its process (a crash, a reboot), and the pid can then belong
    to anything. Only a process that still matches the launched binary and start
    time is signalled; a stale record is discarded and nothing is killed.
    """
    record = recorded()
    if record is None:
        return False
    ours = still_ours(record)
    if ours:
        with contextlib.suppress(OSError):
            os.kill(record.pid, signal.SIGTERM)
    pid_path().unlink(missing_ok=True)
    return ours

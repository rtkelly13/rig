"""The local OTel collector, phase 1: pinned install, config, LaunchAgent, Windows job, retention."""

from __future__ import annotations

import datetime as dt
import hashlib
import io
import os
import pathlib
import plistlib
import socket
import sys
import tarfile
from collections.abc import Callable
from dataclasses import dataclass
from typing import Annotated

import pytest
from pydantic import Field
from typer.testing import CliRunner

from desktop_manager.jobs.registry import Cadence, Environment, Trigger, default_jobs
from rig.otel_stack import cli, collector, retention, runtime, service
from rig.otel_stack.collector_config import render
from rig.otel_stack.paths import pid_path
from rig.typed_json import decode
from rig.typed_yaml import decode_yaml

pytestmark = pytest.mark.ci


# --------------------------------------------------------------------------- install


def _archive(binary: str, payload: bytes) -> bytes:
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:gz") as tar:
        for name, data in ((binary, payload), ("README.md", b"not extracted")):
            info = tarfile.TarInfo(name)
            info.size = len(data)
            tar.addfile(info, io.BytesIO(data))
    return buffer.getvalue()


def _pin(monkeypatch: pytest.MonkeyPatch, archive: bytes) -> None:
    pinned = collector.Release("otelcol_test.tar.gz", hashlib.sha256(archive).hexdigest(), "otelcol")
    monkeypatch.setitem(collector.RELEASES, "test_os", pinned)


def _fetcher(archive: bytes, calls: list[str]) -> Callable[[str, pathlib.Path], None]:
    def fetch(url: str, dest: pathlib.Path) -> None:
        calls.append(url)
        _ = dest.write_bytes(archive)

    return fetch


def test_install_verifies_then_extracts_only_the_binary_and_is_idempotent(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    archive = _archive("otelcol", b"#!/bin/sh\n")
    _pin(monkeypatch, archive)
    calls: list[str] = []

    path = collector.install(key="test_os", home=tmp_path, fetch=_fetcher(archive, calls))
    assert path.read_bytes() == b"#!/bin/sh\n"
    assert os.access(path, os.X_OK)
    assert collector.verified(key="test_os", home=tmp_path)
    assert sorted(p.name for p in path.parent.iterdir()) == [".sha256", "otelcol"], "only the binary, no archive left"
    assert calls[0].endswith(f"/v{collector.VERSION}/otelcol_test.tar.gz")

    _ = collector.install(key="test_os", home=tmp_path, fetch=_fetcher(archive, calls))
    assert len(calls) == 1, "a verified install is not downloaded again"


def test_a_digest_mismatch_extracts_nothing(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    _pin(monkeypatch, _archive("otelcol", b"the real one"))
    tampered = _archive("otelcol", b"something else")

    with pytest.raises(collector.DigestMismatchError):
        _ = collector.install(key="test_os", home=tmp_path, fetch=_fetcher(tampered, []))
    folder = collector.install_dir(tmp_path)
    assert list(folder.iterdir()) == [], "no binary, no marker, no partial archive"
    assert not collector.verified(key="test_os", home=tmp_path)


def test_every_supported_platform_is_pinned_to_the_same_version() -> None:
    assert set(collector.RELEASES) >= {"darwin_arm64", "darwin_amd64", "linux_amd64", "windows_amd64"}
    for key, release in collector.RELEASES.items():
        assert release.asset.startswith(f"otelcol_{collector.VERSION}_{key}"), key
        assert len(release.sha256) == 64
    assert collector.RELEASES["windows_amd64"].binary == "otelcol.exe"


def test_download_refuses_anything_but_https(tmp_path: pathlib.Path) -> None:
    with pytest.raises(ValueError, match="non-HTTPS"):
        collector.download("file:///etc/passwd", tmp_path / "x")


# --------------------------------------------------------------------------- config


@dataclass(frozen=True, slots=True)
class _Endpoint:
    endpoint: str


@dataclass(frozen=True, slots=True)
class _Protocols:
    grpc: _Endpoint
    http: _Endpoint


@dataclass(frozen=True, slots=True)
class _Otlp:
    protocols: _Protocols


@dataclass(frozen=True, slots=True)
class _Receivers:
    otlp: _Otlp


@dataclass(frozen=True, slots=True)
class _Rotation:
    max_megabytes: int
    max_days: int
    max_backups: int


@dataclass(frozen=True, slots=True)
class _FileExporter:
    path: str
    rotation: _Rotation


@dataclass(frozen=True, slots=True)
class _Pipeline:
    receivers: list[str]
    processors: list[str]
    exporters: list[str]


@dataclass(frozen=True, slots=True)
class _Level:
    level: str


@dataclass(frozen=True, slots=True)
class _Telemetry:
    metrics: _Level
    logs: _Level


@dataclass(frozen=True, slots=True)
class _Service:
    telemetry: _Telemetry
    pipelines: dict[str, _Pipeline]


@dataclass(frozen=True, slots=True)
class _Config:
    receivers: _Receivers
    processors: dict[str, dict[str, str]]
    exporters: dict[str, _FileExporter]
    service: _Service


def test_config_is_loopback_otlp_into_one_rotating_file_per_signal(tmp_path: pathlib.Path) -> None:
    config = decode_yaml(_Config, render(data=tmp_path / "data"))

    assert config.receivers.otlp.protocols.grpc.endpoint == "127.0.0.1:4317"
    assert config.receivers.otlp.protocols.http.endpoint == "127.0.0.1:4318"
    assert list(config.processors) == ["batch"], "no memory_limiter: its timer would wake an idle laptop"
    assert set(config.exporters) == {"file/traces", "file/metrics", "file/logs"}
    traces = config.exporters["file/traces"]
    assert traces.path == (tmp_path / "data" / "traces.jsonl").as_posix()
    assert traces.rotation == _Rotation(max_megabytes=100, max_days=7, max_backups=50)
    assert config.service.telemetry.metrics.level == "none"
    assert config.service.pipelines["metrics"] == _Pipeline(["otlp"], ["batch"], ["file/metrics"])


# --------------------------------------------------------------------------- service and job


_load_plist: Callable[[bytes], object] = plistlib.loads


@dataclass(frozen=True, slots=True)
class _Agent:
    label: Annotated[str, Field(alias="Label")]
    program_arguments: Annotated[list[str], Field(alias="ProgramArguments")]
    run_at_load: Annotated[bool, Field(alias="RunAtLoad")]
    keep_alive: Annotated[bool, Field(alias="KeepAlive")]
    throttle: Annotated[int, Field(alias="ThrottleInterval")]
    process_type: Annotated[str, Field(alias="ProcessType")]
    low_priority_io: Annotated[bool, Field(alias="LowPriorityIO")]
    nice: Annotated[int, Field(alias="Nice")]


def test_the_launch_agent_keeps_a_background_collector_running(tmp_path: pathlib.Path) -> None:
    agent = decode(_Agent, _load_plist(service.render(tmp_path / "otelcol", tmp_path / "otelcol.yaml")))
    assert agent.label == "dev.rtkelly.rig.otelcol"
    assert agent.program_arguments == [str(tmp_path / "otelcol"), "--config", str(tmp_path / "otelcol.yaml")]
    assert agent.run_at_load and agent.keep_alive
    assert agent.throttle >= 30, "a broken config must not become a tight restart loop"
    assert (agent.process_type, agent.low_priority_io, agent.nice) == ("Background", True, 10)


def test_windows_ensures_the_collector_at_logon_and_every_tick(tmp_path: pathlib.Path) -> None:
    env = Environment(
        scripts=tmp_path / "Scripts",
        plan=tmp_path / "plan.json",
        workspace_root=tmp_path / "ws",
        personal_root=tmp_path / "personal",
        work_roots=(tmp_path / "acme",),
        decisions_feed=tmp_path / "decisions.json",
        orca_user_data=tmp_path / "orca-personal",
    )
    job = {j.name: j for j in default_jobs(env)}["otel-collector"]
    assert job.argv[1:] == ("collector", "ensure")
    assert job.triggers == frozenset({Trigger.LOGON, Trigger.TICK})
    assert job.cadence is Cadence.EVERY_RUN


# --------------------------------------------------------------------------- runtime


def test_listening_probes_the_port() -> None:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
        server.bind(("127.0.0.1", 0))
        server.listen()
        address: Callable[[], tuple[str, int]] = server.getsockname
        port = address()[1]
        assert runtime.listening(port)
    assert not runtime.listening(port)


def test_config_is_written_with_its_data_folder() -> None:
    path = runtime.write_config()
    assert path.is_file()
    assert "file/traces" in path.read_text(encoding="utf-8")


# --------------------------------------------------------------------------- retention


def test_old_rotated_collector_files_go_but_the_active_file_stays(tmp_path: pathlib.Path) -> None:
    now = dt.datetime(2026, 10, 25, 12, tzinfo=dt.UTC)

    def file(name: str, age_days: float) -> pathlib.Path:
        path = tmp_path / name
        _ = path.write_text("{}\n", encoding="utf-8")
        stamp = (now - dt.timedelta(days=age_days)).timestamp()
        os.utime(path, (stamp, stamp))
        return path

    active = file("traces.jsonl", 30)
    old = file("traces-2026-10-01T10-00-00.000.jsonl", 24)
    recent = file("metrics-2026-10-24T10-00-00.000.jsonl", 1)
    stranger = file("notes-2026-10-01.jsonl", 24)

    assert retention.prune(tmp_path, now=now, apply=True) == [old]
    assert active.exists() and recent.exists() and stranger.exists()
    assert not old.exists()


# --------------------------------------------------------------------------- review findings


def test_a_data_folder_with_an_apostrophe_still_renders_valid_yaml(tmp_path: pathlib.Path) -> None:
    data = tmp_path / "O'Brien" / "data"
    config = decode_yaml(_Config, render(data=data))
    assert config.exporters["file/traces"].path == (data / "traces.jsonl").as_posix()


def _launched(pid: int, binary: str, started: str) -> None:
    pid_path().parent.mkdir(parents=True, exist_ok=True)
    _ = pid_path().write_text(runtime.Launched(pid, binary, started).serialise(), encoding="utf-8")


@dataclass(slots=True)
class _Process:
    """What the pid currently is, as `ps`/`tasklist` would report it, and what got signalled."""

    command: str
    started: str
    killed: list[int]

    def install(self, monkeypatch: pytest.MonkeyPatch) -> None:
        monkeypatch.setattr(os, "kill", self.kill)
        monkeypatch.setattr(runtime, "_command_of", self.command_of)
        monkeypatch.setattr(runtime, "process_started", self.started_of)

    def kill(self, pid: int, sig: int) -> None:
        assert sig > 0
        self.killed.append(pid)

    def command_of(self, pid: int) -> str:
        assert pid > 0
        return self.command

    def started_of(self, pid: int) -> str:
        assert pid > 0
        return self.started


def test_a_stale_pid_record_is_discarded_without_killing_its_new_owner(monkeypatch: pytest.MonkeyPatch) -> None:
    now = _Process("/Applications/Safari.app/Contents/MacOS/Safari", "Sun Oct 25 10:00:00 2026", [])
    now.install(monkeypatch)
    _launched(4242, "/opt/otel/otelcol", "Sun Oct 25 09:00:00 2026")

    assert runtime.recorded_pid() is None
    assert runtime.stop_recorded() is False
    assert now.killed == []
    assert not pid_path().exists(), "the stale record is cleared"


def test_a_reused_pid_running_the_same_binary_later_is_not_ours(monkeypatch: pytest.MonkeyPatch) -> None:
    if os.name == "nt":
        pytest.skip("start time is not recorded on Windows; the image name is the check there")
    now = _Process("/opt/otel/otelcol --config other.yaml", "Sun Oct 25 10:00:00 2026", [])
    now.install(monkeypatch)
    _launched(4242, "/opt/otel/otelcol", "Sun Oct 25 09:00:00 2026")
    assert runtime.stop_recorded() is False
    assert now.killed == []


def test_the_collector_it_launched_is_stopped(monkeypatch: pytest.MonkeyPatch) -> None:
    windows = os.name == "nt"
    binary = "otelcol.exe" if windows else "/opt/otel/otelcol"
    now = _Process(binary if windows else f"{binary} --config c.yaml", "Sun Oct 25 09:00:00 2026", [])
    now.install(monkeypatch)
    _launched(4242, binary, "" if windows else "Sun Oct 25 09:00:00 2026")
    assert runtime.recorded_pid() == 4242
    assert runtime.stop_recorded() is True
    assert now.killed == [4242]


# --------------------------------------------------------------------------- forwarding


@dataclass(frozen=True, slots=True)
class _Retry:
    enabled: bool
    max_elapsed_time: str


@dataclass(frozen=True, slots=True)
class _Queue:
    enabled: bool
    queue_size: int


@dataclass(frozen=True, slots=True)
class _ForwardExporter:
    endpoint: str
    retry_on_failure: _Retry
    sending_queue: _Queue


@dataclass(frozen=True, slots=True)
class _ForwardExporters:
    forward: Annotated[_ForwardExporter, Field(alias="otlp_http/forward")]
    traces: Annotated[_FileExporter, Field(alias="file/traces")]


@dataclass(frozen=True, slots=True)
class _ForwardConfig:
    exporters: _ForwardExporters
    service: _Service


def test_a_forward_endpoint_adds_a_bounded_exporter_beside_every_file(tmp_path: pathlib.Path) -> None:
    config = decode_yaml(_ForwardConfig, render(data=tmp_path / "data", forward="http://127.0.0.1:24318"))

    forward = config.exporters.forward
    assert forward.endpoint == "http://127.0.0.1:24318"
    assert forward.sending_queue == _Queue(enabled=True, queue_size=200), "a down downstream holds little memory"
    assert forward.retry_on_failure == _Retry(enabled=True, max_elapsed_time="30s")
    for signal in ("traces", "metrics", "logs"):
        assert config.service.pipelines[signal].exporters == [f"file/{signal}", "otlp_http/forward"]


def test_without_a_forward_endpoint_only_the_files_are_written(tmp_path: pathlib.Path) -> None:
    text = render(data=tmp_path / "data")
    assert "otlp_http" not in text


@pytest.mark.parametrize(
    ("given", "recorded"),
    [
        ("http://127.0.0.1:24318", "http://127.0.0.1:24318"),
        ("http://localhost:24318/", "http://localhost:24318"),
        (" https://collector.internal:4318 ", "https://collector.internal:4318"),
    ],
)
def test_a_forward_endpoint_is_an_http_base_url(given: str, recorded: str) -> None:
    assert runtime.validate_forward(given) == recorded


@pytest.mark.parametrize(
    "given",
    ["127.0.0.1:24318", "grpc://127.0.0.1:24317", "http://127.0.0.1", "http://127.0.0.1:24318/v1/traces", "http://:1"],
)
def test_anything_else_is_refused_as_a_forward_endpoint(given: str) -> None:
    with pytest.raises(ValueError, match="forward endpoint"):
        _ = runtime.validate_forward(given)


def test_the_forward_endpoint_is_recorded_rendered_and_cleared() -> None:
    assert runtime.forward() is None
    assert runtime.set_forward("http://127.0.0.1:24318/") == "http://127.0.0.1:24318"
    assert runtime.forward() == "http://127.0.0.1:24318"
    assert "otlp_http/forward" in runtime.write_config().read_text(encoding="utf-8")

    assert runtime.set_forward(None) is None
    assert runtime.forward() is None
    assert "otlp_http" not in runtime.write_config().read_text(encoding="utf-8")


# --------------------------------------------------------------------------- who holds the port

_NETSTAT = """
Active Connections

  Proto  Local Address          Foreign Address        State           PID
  TCP    0.0.0.0:135            0.0.0.0:0              LISTENING       1288
  TCP    0.0.0.0:14318          0.0.0.0:0              LISTENING       77
  TCP    127.0.0.1:4318         127.0.0.1:50122        ESTABLISHED     40308
  TCP    0.0.0.0:4318           0.0.0.0:0              LISTENING       40308
  TCP    [::]:4318              [::]:0                 LISTENING       40308
"""


def test_netstat_names_the_pid_listening_on_the_port_and_nothing_else() -> None:
    assert runtime.parse_netstat_listener(_NETSTAT, 4318) == 40308, "not 14318's pid, not an ESTABLISHED row"
    assert runtime.parse_netstat_listener(_NETSTAT, 4317) is None


@pytest.mark.parametrize(
    ("command", "ours"),
    [
        ("otelcol.exe", True),
        ("OTELCOL.EXE", True),
        (r"C:\Users\a b\AppData\Local\shared-utilities\otel\bin\0.162.0\otelcol.exe", True),
        (
            "/Users/r/Library/Application Support/shared-utilities/otel/bin/0.162.0/otelcol --config /x/otelcol.yaml",
            True,
        ),
        ("otelcol-contrib.exe", False),
        ("/opt/otel/otelcol-contrib --config c.yaml", False),
        ("com.docker.backend.exe", False),
        ("", False),
    ],
)
def test_only_the_core_collector_counts_as_ours(command: str, ours: bool) -> None:
    assert runtime.is_collector(command) is ours


def _held_by(monkeypatch: pytest.MonkeyPatch, holder: runtime.Owner | None) -> list[str]:
    started: list[str] = []

    def owner(port: int = 4318) -> runtime.Owner | None:
        assert port == 4318
        return holder

    def start(binary: pathlib.Path) -> int:
        started.append(str(binary))
        return 1

    def install() -> pathlib.Path:
        return pathlib.Path("otelcol")

    def listening(port: int) -> bool:
        assert port == 4318
        return holder is not None

    monkeypatch.setattr(runtime, "owner", owner)
    monkeypatch.setattr(runtime, "start", start)
    monkeypatch.setattr(runtime, "listening", listening)
    monkeypatch.setattr(collector, "install", install)
    monkeypatch.setattr(sys, "platform", "win32")
    return started


def test_ensure_refuses_a_port_held_by_another_collector(monkeypatch: pytest.MonkeyPatch) -> None:
    started = _held_by(monkeypatch, runtime.Owner(pid=40308, command="com.docker.backend.exe"))
    result = CliRunner().invoke(cli.app, ["collector", "ensure"])
    assert result.exit_code == cli.FOREIGN_LISTENER_EXIT
    assert "com.docker.backend.exe" in " ".join(result.output.split())
    assert started == [], "a foreign listener is never mistaken for a running collector, nor raced"


def test_ensure_accepts_its_own_collector_and_starts_one_when_the_port_is_free(monkeypatch: pytest.MonkeyPatch) -> None:
    started = _held_by(monkeypatch, runtime.Owner(pid=9, command="otelcol.exe"))
    assert CliRunner().invoke(cli.app, ["collector", "ensure"]).exit_code == 0
    assert started == []

    started = _held_by(monkeypatch, None)
    assert CliRunner().invoke(cli.app, ["collector", "ensure"]).exit_code == 0
    assert started == ["otelcol"]

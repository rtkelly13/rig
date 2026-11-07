"""Where the collector's binary, config, data and log live: all under rig's per-user folder."""

from __future__ import annotations

import pathlib

from desktop_manager.runtime import base_dir

#: One rotated JSON-lines file per signal; the un-suffixed name is the one being written.
SIGNALS = ("traces", "metrics", "logs")


def otel_home() -> pathlib.Path:
    return base_dir() / "otel"


def data_dir() -> pathlib.Path:
    return otel_home() / "data"


def config_path() -> pathlib.Path:
    return otel_home() / "otelcol.yaml"


def forward_path() -> pathlib.Path:
    """The OTLP/HTTP endpoint the collector also forwards to; absent means no forwarding."""
    return otel_home() / "forward"


def pid_path() -> pathlib.Path:
    """Written when `collector start` runs the binary itself (Windows, or macOS without the agent)."""
    return otel_home() / "otelcol.pid"


def log_path() -> pathlib.Path:
    return base_dir() / "logs" / "otelcol.log"


def active_file(signal: str, data: pathlib.Path | None = None) -> pathlib.Path:
    return (data or data_dir()) / f"{signal}.jsonl"


def clickhouse_home() -> pathlib.Path:
    """Everything the on-demand ClickHouse pair writes: configs, pids, backfill state, and its data."""
    return otel_home() / "clickhouse"


def clickhouse_data() -> pathlib.Path:
    return clickhouse_home() / "data"


def clickhouse_log() -> pathlib.Path:
    return base_dir() / "logs" / "clickhouse.log"


def contrib_log() -> pathlib.Path:
    return base_dir() / "logs" / "otelcol-contrib.log"

"""Organise-run tracing: the day-file exporter, retention and the report.

The spans a run emits are tested with the run itself, in `test_chrome_organise`.
"""

from __future__ import annotations

import json
from collections.abc import Iterator
from datetime import date
from pathlib import Path
from typing import Any

import pytest

from desktop_manager.chrome import traces
from rig import tracing

pytestmark = pytest.mark.ci


@pytest.fixture
def own_dir(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> Iterator[Path]:
    """Trace straight into a directory of the test's own, as the installed task does."""
    monkeypatch.delenv("SHARED_UTILITIES_TRACE_DIR", raising=False)
    directory = tmp_path / "traces"
    tracing.set_trace_dir(directory)
    yield directory
    tracing.shutdown_tracing()
    tracing.set_trace_dir(None)


def _lines(directory: Path) -> list[dict[str, Any]]:
    return [json.loads(line) for path in sorted(directory.rglob("*.ndjson")) for line in path.read_text().splitlines()]


def _spans(directory: Path) -> list[dict[str, Any]]:
    return [s for e in _lines(directory) for r in e["resourceSpans"] for sc in r["scopeSpans"] for s in sc["spans"]]


# -- exporter -----------------------------------------------------------------


class TestExporter:
    def test_writes_one_json_envelope_per_line_to_a_dated_file(self, own_dir: Path) -> None:
        with tracing.span("outer", scope="test", count=3) as outer:
            outer.event("check", blocked=True, reason="busy")
            with tracing.span("inner", scope="test"):
                pass
        tracing.flush()

        files = list(own_dir.iterdir())
        assert len(files) == 1
        assert traces.trace_files(own_dir)[0].path == files[0]
        envelopes = _lines(own_dir)
        spans = [s for e in envelopes for r in e["resourceSpans"] for sc in r["scopeSpans"] for s in sc["spans"]]
        by_name = {span["name"]: span for span in spans}
        assert by_name["inner"]["parentSpanId"] == by_name["outer"]["spanId"]
        assert by_name["outer"]["attributes"] == [{"key": "count", "value": {"intValue": "3"}}]
        [event] = by_name["outer"]["events"]
        assert event["name"] == "check"
        assert {"key": "reason", "value": {"stringValue": "busy"}} in event["attributes"]

    def test_records_an_exception_event_and_error_status(self, own_dir: Path) -> None:
        with pytest.raises(RuntimeError), tracing.span("boom", scope="test"):
            raise RuntimeError("no chrome")
        tracing.flush()

        [span] = _spans(own_dir)
        assert span["status"]["message"] == "RuntimeError: no chrome"
        [event] = span["events"]
        assert event["name"] == "exception"
        attributes = {a["key"]: a["value"]["stringValue"] for a in event["attributes"]}
        assert attributes["exception.type"] == "RuntimeError"
        assert "no chrome" in attributes["exception.stacktrace"]

    def test_open_spans_flush_as_incomplete_then_the_complete_copy_wins(self, own_dir: Path) -> None:
        with tracing.span("long", scope="test"):
            tracing.flush(include_open=True)
            [snapshot] = _spans(own_dir)
            assert {"key": "span.incomplete", "value": {"boolValue": True}} in snapshot["attributes"]
        tracing.flush()

        span = _spans(own_dir)[-1]
        assert {"key": "span.incomplete", "value": {"boolValue": True}} not in span.get("attributes", [])
        assert len(_lines(own_dir)) == 2

    def test_the_environment_override_beats_set_trace_dir(self, isolate_trace_output: Path, tmp_path: Path) -> None:
        tracing.set_trace_dir(tmp_path / "ignored")
        try:
            with tracing.span("isolated", scope="test"):
                pass
            tracing.flush()
        finally:
            tracing.set_trace_dir(None)
        assert not (tmp_path / "ignored").exists()
        assert [span["name"] for span in _spans(isolate_trace_output)] == ["isolated"]

    def test_trace_home_moves_only_the_default_and_set_trace_dir_still_wins(
        self, monkeypatch: pytest.MonkeyPatch, tmp_path: Path
    ) -> None:
        monkeypatch.delenv("SHARED_UTILITIES_TRACE_DIR", raising=False)
        monkeypatch.setenv(tracing.TRACE_HOME_ENV, str(tmp_path / "home"))
        tracing.init_tracing("tool")
        try:
            assert tracing.trace_file().parent == tmp_path / "home" / "tool"
            tracing.set_trace_dir(tmp_path / "own")
            assert tracing.trace_file().parent == tmp_path / "own"
        finally:
            tracing.set_trace_dir(None)
            tracing.init_tracing("shared-utilities")

    def test_tracing_off_writes_nothing(self, own_dir: Path, monkeypatch: pytest.MonkeyPatch) -> None:
        monkeypatch.setenv("SHARED_UTILITIES_TRACING", "0")
        with tracing.span("quiet", scope="test"):
            tracing.flush(include_open=True)
        tracing.flush()
        assert not own_dir.exists()


# -- retention ----------------------------------------------------------------


def _day_files(directory: Path, *days: str) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    for day in days:
        (directory / f"{day}.ndjson").write_text("{}\n")


class TestPrune:
    TODAY = date(2026, 10, 6)

    def test_deletes_files_more_than_thirty_days_old(self, tmp_path: Path) -> None:
        _day_files(tmp_path, "2026-08-01", "2026-09-05", "2026-09-06", "2026-10-06")
        (tmp_path / "notes.txt").write_text("not a trace")

        removed = traces.prune(tmp_path, today=self.TODAY)

        assert [path.name for path in removed] == ["2026-08-01.ndjson", "2026-09-05.ndjson"]
        assert sorted(path.name for path in tmp_path.iterdir()) == [
            "2026-09-06.ndjson",
            "2026-10-06.ndjson",
            "notes.txt",
        ]

    def test_dry_run_lists_without_deleting(self, tmp_path: Path) -> None:
        _day_files(tmp_path, "2026-01-01", "2026-10-01")
        due = traces.prune(tmp_path, today=self.TODAY, dry_run=True)
        assert [path.name for path in due] == ["2026-01-01.ndjson"]
        assert len(list(tmp_path.iterdir())) == 2

    def test_daily_prune_runs_once_a_day(self, tmp_path: Path) -> None:
        _day_files(tmp_path, "2026-01-01")
        assert [p.name for p in traces.prune_daily(tmp_path, today=self.TODAY) or []] == ["2026-01-01.ndjson"]
        _day_files(tmp_path, "2026-01-02")
        assert traces.prune_daily(tmp_path, today=self.TODAY) is None
        assert (tmp_path / "2026-01-02.ndjson").exists()
        assert traces.prune_daily(tmp_path, today=date(2026, 10, 7)) == [tmp_path / "2026-01-02.ndjson"]

    def test_daily_prune_of_a_missing_directory_creates_it(self, tmp_path: Path) -> None:
        assert traces.prune_daily(tmp_path / "new", today=self.TODAY) == []

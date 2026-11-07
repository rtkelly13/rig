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
from typer.testing import CliRunner

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


def _spans(directory: Path) -> list[traces.SpanRecord]:
    return traces.parse_lines(line for path in directory.rglob("*.ndjson") for line in path.read_text().splitlines())


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
        assert span.error == "RuntimeError: no chrome"
        [event] = span.events
        assert event.name == "exception"
        assert event.attributes["exception.type"] == "RuntimeError"
        assert "no chrome" in event.attributes["exception.stacktrace"]

    def test_open_spans_flush_as_incomplete_then_the_complete_copy_wins(self, own_dir: Path) -> None:
        with tracing.span("long", scope="test"):
            tracing.flush(include_open=True)
            [snapshot] = _spans(own_dir)
            assert snapshot.incomplete
        tracing.flush()

        [span] = _spans(own_dir)
        assert not span.incomplete
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
        assert [span.name for span in _spans(isolate_trace_output)] == ["isolated"]

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

    def test_prune_command(self, tmp_path: Path) -> None:
        from desktop_manager.chrome.cli import chrome_tabs_app

        _day_files(tmp_path, "2020-01-01")
        result = CliRunner().invoke(chrome_tabs_app, ["traces", "prune", "--dry-run", "--dir", str(tmp_path)])
        assert result.exit_code == 0, result.output
        assert "2020-01-01.ndjson" in result.output
        assert (tmp_path / "2020-01-01.ndjson").exists()


# -- report -------------------------------------------------------------------

_SECOND = 1_000_000_000
_BASE = 1_790_000_000 * _SECOND


def _value(value: Any) -> dict[str, Any]:
    if isinstance(value, bool):
        return {"boolValue": value}
    if isinstance(value, int):
        return {"intValue": str(value)}
    if isinstance(value, float):
        return {"doubleValue": value}
    return {"stringValue": str(value)}


def _raw(
    name: str,
    span_id: str,
    *,
    parent: str = "",
    start: float = 0,
    end: float = 1,
    trace: str = "t1",
    error: str = "",
    events: list[tuple[str, float, dict[str, Any]]] | None = None,
    **attrs: Any,
) -> dict[str, Any]:
    raw: dict[str, Any] = {
        "traceId": trace,
        "spanId": span_id,
        "parentSpanId": parent,
        "name": name,
        "startTimeUnixNano": str(_BASE + int(start * _SECOND)),
        "endTimeUnixNano": str(_BASE + int(end * _SECOND)),
        "status": {"code": 2, "message": error} if error else {"code": 1},
        "attributes": [{"key": k.replace("__", "."), "value": _value(v)} for k, v in attrs.items()],
    }
    if events:
        raw["events"] = [
            {
                "name": event,
                "timeUnixNano": str(_BASE + int(at * _SECOND)),
                "attributes": [{"key": k, "value": _value(v)} for k, v in detail.items()],
            }
            for event, at, detail in events
        ]
    return raw


def _envelope(*spans: dict[str, Any]) -> str:
    return json.dumps({"resourceSpans": [{"scopeSpans": [{"scope": {"name": traces.SCOPE}, "spans": list(spans)}]}]})


def _fixture_lines() -> list[str]:
    """Three runs: grouped after a wait, deferred, and one killed mid-wait (incomplete)."""
    checks = [
        ("check", 2, {"blocked": True, "reason": "the machine was used 12s ago (waits for 1 min idle)"}),
        ("check", 50, {"blocked": True, "reason": "the machine was used 3s ago (waits for 1 min idle)"}),
        ("check", 110, {"blocked": False}),
    ]
    grouped = [
        _raw(
            traces.RUN_SPAN,
            "aaaa000000000001",
            start=0,
            end=130,
            chrome__trigger="scheduled",
            run__outcome="ok",
            group__outcome="grouped",
            group__planned=4,
            group__moved=4,
            tabs__closed=3,
            wait__seconds=108.0,
            wait__checks=3,
            wait__last_block="the machine was used 3s ago (waits for 1 min idle)",
        ),
        _raw("chrome_tabs.read_windows", "r1", parent="aaaa000000000001", start=0, end=1, chrome__windows=2),
        _raw("chrome_tabs.wait_for_idle", "w1", parent="aaaa000000000001", start=2, end=110, events=checks),
        _raw("chrome_tabs.group", "g1", parent="aaaa000000000001", start=110, end=130),
        _raw("chrome_tabs.group_window", "gw1", parent="g1", start=111, end=129, window__number=1),
    ]
    deferred = [
        _raw(
            traces.RUN_SPAN,
            "bbbb000000000002",
            trace="t2",
            start=3600,
            end=3610,
            chrome__trigger="scheduled",
            run__outcome="ok",
            group__outcome="deferred",
            tabs__closed=1,
            wait__seconds=0.0,
            wait__checks=1,
            wait__last_block="the session is locked",
        ),
        _raw(
            "chrome_tabs.wait_for_idle",
            "w2",
            trace="t2",
            parent="bbbb000000000002",
            start=3601,
            end=3601,
            events=[("check", 3601, {"blocked": True, "reason": "the session is locked"})],
        ),
    ]
    killed_open = _raw(traces.RUN_SPAN, "cccc000000000003", trace="t3", start=7200, end=7300, chrome__trigger="manual")
    killed_open["attributes"].append({"key": "span.incomplete", "value": {"boolValue": True}})
    return [_envelope(*grouped), "not json", _envelope(*deferred), _envelope(killed_open)]


class TestReport:
    def test_runs_are_rooted_and_ordered_with_their_descendants(self) -> None:
        found = traces.runs(traces.parse_lines(_fixture_lines()))
        assert [run.id for run in found] == ["aaaa0000", "bbbb0000", "cccc0000"]
        assert {span.name for span in found[0].spans} == {
            "chrome_tabs.read_windows",
            "chrome_tabs.wait_for_idle",
            "chrome_tabs.group",
            "chrome_tabs.group_window",
        }
        assert [run.outcome for run in found] == ["ok", "ok", "incomplete"]
        assert [run.grouping for run in found] == ["grouped", "deferred", "-"]
        assert found[0].blocks == [
            "the machine was used 12s ago (waits for 1 min idle)",
            "the machine was used 3s ago (waits for 1 min idle)",
        ]

    def test_the_complete_copy_of_a_span_replaces_its_snapshot(self) -> None:
        complete = _raw(traces.RUN_SPAN, "dddd000000000004", end=5, run__outcome="ok")
        snapshot = _raw(traces.RUN_SPAN, "dddd000000000004", end=2, span__incomplete=True)
        for lines in ([_envelope(snapshot), _envelope(complete)], [_envelope(complete), _envelope(snapshot)]):
            [run] = traces.runs(traces.parse_lines(lines))
            assert run.outcome == "ok"
            assert run.root.seconds == 5

    def test_summary_aggregates_outcomes_waits_and_folds_block_reasons(self) -> None:
        summary = traces.summarise(traces.runs(traces.parse_lines(_fixture_lines())))
        assert (summary.runs, summary.completed, summary.ok, summary.incomplete) == (3, 2, 2, 1)
        assert summary.success_rate == 1.0
        assert summary.grouping == {"grouped": 1, "deferred": 1, "-": 1}
        assert summary.waits == [108.0, 0.0]
        assert summary.closed == 4
        assert summary.blocks == [
            traces.BlockCount("the machine was used Ns ago (waits for N min idle)", 2),
            traces.BlockCount("the session is locked", 1),
        ]
        rendered = "\n".join(traces.render_summary(summary, 7))
        assert "3 runs, 2 completed, 1 incomplete" in rendered
        assert "success rate        100% (2/2)" in rendered

    def test_render_runs_is_newest_first(self) -> None:
        lines = traces.render_runs(traces.runs(traces.parse_lines(_fixture_lines())))
        assert [line.split()[0] for line in lines] == ["cccc0000", "bbbb0000", "aaaa0000"]
        assert "incomplete" in lines[0]
        assert "grouped 4/4" in lines[2]
        assert "the machine was used 3s ago" in lines[2]

    def test_render_tree_nests_children_and_shows_events(self) -> None:
        run = traces.find_run(traces.runs(traces.parse_lines(_fixture_lines())), "aaaa")
        assert run is not None
        tree = traces.render_tree(run)
        names = [line.split()[0] for line in tree if not line.startswith(" " * 4) or "chrome_tabs." in line]
        assert names[0] == traces.RUN_SPAN
        assert any(line.startswith("    chrome_tabs.group_window") for line in tree)
        assert any("- +2.0s check blocked=True" in line for line in tree)

    def test_find_run_defaults_to_the_latest(self) -> None:
        found = traces.runs(traces.parse_lines(_fixture_lines()))
        assert traces.find_run(found, None) is found[-1]
        assert traces.find_run(found, "BBBB") is found[1]
        assert traces.find_run(found, "ffff") is None

    def test_load_spans_keeps_only_the_window_of_days(self, tmp_path: Path) -> None:
        (tmp_path / "2026-10-06.ndjson").write_text(_envelope(_raw(traces.RUN_SPAN, "new0000000000001")) + "\n")
        (tmp_path / "2026-09-01.ndjson").write_text(_envelope(_raw(traces.RUN_SPAN, "old0000000000001")) + "\n")
        recent = traces.load_spans(tmp_path, days=7, today=date(2026, 10, 6))
        assert [span.span_id for span in recent] == ["new0000000000001"]
        assert len(traces.load_spans(tmp_path)) == 2

    def test_cli_commands_read_a_directory(self, tmp_path: Path) -> None:
        from datetime import UTC, datetime

        from desktop_manager.chrome.cli import chrome_tabs_app

        today = datetime.now(UTC).date().isoformat()
        (tmp_path / f"{today}.ndjson").write_text("\n".join(_fixture_lines()) + "\n")
        runner = CliRunner()

        listed = runner.invoke(chrome_tabs_app, ["traces", "runs", "--dir", str(tmp_path)])
        assert listed.exit_code == 0, listed.output
        assert "aaaa0000" in listed.output

        as_json = runner.invoke(chrome_tabs_app, ["traces", "runs", "--json", "--dir", str(tmp_path)])
        assert [row["id"] for row in json.loads(as_json.output)][0] == "cccc0000"

        shown = runner.invoke(chrome_tabs_app, ["traces", "show", "aaaa", "--dir", str(tmp_path)])
        assert shown.exit_code == 0, shown.output
        assert "chrome_tabs.wait_for_idle" in shown.output

        missing = runner.invoke(chrome_tabs_app, ["traces", "show", "ffff", "--dir", str(tmp_path)])
        assert missing.exit_code == 1

        summary = runner.invoke(chrome_tabs_app, ["traces", "summary", "--dir", str(tmp_path)])
        assert summary.exit_code == 0, summary.output

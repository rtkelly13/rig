"""``--record-sizes PATH``: write each test's style, size and resources to a JSON file.

Classification lives in ``rig.repo_governance.size_probe``; this is only the recorder.
An audit hook (PEP 578) sees every event in the process, but it only counts events raised
during a test's *call* phase. Fixture setup is left out: a session fixture would otherwise be
credited to whichever test ran first on its xdist worker, and the tags would change between
runs. Workers pass what they saw through ``user_properties``, and only the controller writes.
"""

from __future__ import annotations

import dataclasses
import json
import pathlib
import sys
from collections.abc import Generator

import pytest

from rig.repo_governance.size_probe import Resource, Row, resource_of, size_of, style_of


class SizeProbe:
    def __init__(self, out: pathlib.Path) -> None:
        self.out = out
        self.rows: dict[str, Row] = {}
        self._current: set[Resource] | None = None

    def audit(self, event: str, args: tuple[object, ...]) -> None:
        current = self._current
        if current is None:
            return
        try:
            resource = resource_of(event, args)
        except Exception:  # an audit hook must never raise into the code under test
            return
        if resource is not None:
            current.add(resource)

    @pytest.hookimpl(wrapper=True)
    def pytest_runtest_call(self, item: pytest.Item) -> Generator[None, object, object]:
        self._current = set()
        try:
            return (yield)
        finally:
            used = sorted(self._current)
            self._current = None
            item.user_properties.append(("resources", used))
            item.user_properties.append(("markers", sorted({m.name for m in item.iter_markers()})))

    def pytest_runtest_logreport(self, report: pytest.TestReport) -> None:
        if report.when != "call":
            return
        props = dict(report.user_properties)
        resources = list(props.get("resources", []))
        markers = list(props.get("markers", []))
        self.rows[report.nodeid] = Row(
            style=style_of(markers),
            size=size_of(resources, markers),
            resources=resources,
            duration=round(report.duration, 4),
        )

    def pytest_sessionfinish(self, session: pytest.Session) -> None:
        if hasattr(session.config, "workerinput"):
            return  # an xdist worker: the controller has every row
        self.out.parent.mkdir(parents=True, exist_ok=True)
        rows = {nodeid: dataclasses.asdict(row) for nodeid, row in sorted(self.rows.items())}
        self.out.write_text(json.dumps(rows, indent=1) + "\n", encoding="utf-8")


def addoption(parser: pytest.Parser) -> None:
    parser.addoption(
        "--record-sizes",
        metavar="PATH",
        help="write each test's style, size (small/medium/large) and resources as JSON (docs/test-quality.md)",
    )


def register(config: pytest.Config) -> None:
    out = config.getoption("--record-sizes")
    if not out:
        return
    probe = SizeProbe(pathlib.Path(out))
    sys.addaudithook(probe.audit)  # hooks cannot be removed: one per session, never per test
    config.pluginmanager.register(probe, "size_probe")

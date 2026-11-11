"""Test sizes: what a test's body actually uses decides whether it is small, medium or large."""

from __future__ import annotations

import json
import os
import pathlib

import pytest
from hypothesis import given
from hypothesis import strategies as st

from rig.repo_governance.size_probe import SIZES, largest, resource_of, size_of, style_of

pytestmark = pytest.mark.ci
pytest_plugins = ["pytester"]


@pytest.mark.parametrize(
    ("event", "args", "expected"),
    [
        ("subprocess.Popen", ("git", ["git", "status"], None, None), "subprocess"),
        ("os.system", (b"ls",), "subprocess"),
        ("open", ("/repo/out.txt", "w", 0), "filesystem"),
        ("open", ("/repo/out.txt", "r+", 0), "filesystem"),
        ("open", ("/repo/out.txt", None, os.O_WRONLY | os.O_CREAT), "filesystem"),
        ("open", ("/repo/in.txt", "r", 0), None),
        ("open", ("/repo/in.txt", None, os.O_RDONLY), None),
        ("open", (f"/repo/src/__pycache__{os.sep}m.cpython-313.pyc", "wb", 0), None),
        ("os.mkdir", ("/repo/temp/x", 0o777, -1), "filesystem"),
        ("os.remove", ("/repo/.hypothesis/examples/ab", -1), None),
        ("shutil.rmtree", ("/repo/temp/x", None), "filesystem"),
        ("socket.connect", (object(), ("127.0.0.1", 8080)), "localhost"),
        ("socket.connect", (object(), ("::1", 8080, 0, 0)), "localhost"),
        ("socket.connect", (object(), "/tmp/agent.sock"), "localhost"),
        ("socket.bind", (object(), ("", 0)), "localhost"),
        ("socket.connect", (object(), ("140.82.112.3", 443)), "network"),
        ("socket.getaddrinfo", ("localhost", 80, 0, 0, 0), "localhost"),
        ("socket.getaddrinfo", ("api.github.com", 443, 0, 0, 0), "network"),
        ("import", ("json", None, [], [], []), None),
        ("os.listdir", ("/repo",), None),
    ],
)
def test_an_audit_event_names_the_resource_it_uses(event: str, args: tuple[object, ...], expected: str | None) -> None:
    assert resource_of(event, args) == expected


@pytest.mark.parametrize(
    ("resources", "markers", "expected"),
    [
        ([], [], "small"),
        ([], ["property", "ci"], "small"),
        (["filesystem"], [], "medium"),
        (["subprocess", "localhost"], [], "medium"),
        (["network"], [], "large"),
        ([], ["integration"], "large"),
    ],
)
def test_size_is_the_widest_resource_used(resources: list[str], markers: list[str], expected: str) -> None:
    assert size_of(resources, markers) == expected  # pyright: ignore[reportArgumentType]


def test_style_comes_from_the_derived_markers_and_bdd_wins() -> None:
    assert [style_of(m) for m in ([], ["property"], ["bdd"], ["bdd", "property"])] == [
        "example",
        "property",
        "bdd",
        "bdd",
    ]


@given(st.lists(st.sampled_from(SIZES)))
def test_largest_is_never_smaller_than_any_case(sizes: list[str]) -> None:
    widest = largest(sizes)  # pyright: ignore[reportArgumentType]

    assert all(SIZES.index(widest) >= SIZES.index(s) for s in sizes)  # pyright: ignore[reportArgumentType]
    assert widest in sizes or (not sizes and widest == "small")


@given(st.text(), st.tuples(st.none() | st.integers() | st.text() | st.binary()))
def test_resource_of_never_raises(event: str, args: tuple[object, ...]) -> None:
    assert resource_of(event, args) in {None, "subprocess", "filesystem", "localhost", "network"}


_PLUGIN_CONFTEST = """
from tests import sizes

def pytest_addoption(parser):
    sizes.addoption(parser)

def pytest_configure(config):
    sizes.register(config)
"""

_SAMPLE_TESTS = """
import os
import pytest

@pytest.fixture
def made_dir(tmp_path):
    os.mkdir(tmp_path / "setup-only")
    return tmp_path

def test_pure():
    assert sum(range(4)) == 6

def test_writes(tmp_path):
    (tmp_path / "out.txt").write_text("x", encoding="utf-8")

def test_writes_only_in_its_fixture(made_dir):
    assert made_dir.is_dir()

@pytest.mark.integration
def test_live_desktop():
    pass

@pytest.mark.property
def test_tagged_property():
    pass
"""


def test_the_recorder_sizes_each_test_by_its_call_phase_only(pytester: pytest.Pytester) -> None:
    pytester.makeconftest(_PLUGIN_CONFTEST)
    pytester.makepyfile(test_sample=_SAMPLE_TESTS)
    out = pytester.path / "out" / "sizes.json"

    result = pytester.runpytest("-W", "ignore::pytest.PytestUnknownMarkWarning", f"--record-sizes={out}")

    result.assert_outcomes(passed=5)
    rows = json.loads(out.read_text(encoding="utf-8"))
    assert {test.split("::")[1]: (row["style"], row["size"], row["resources"]) for test, row in rows.items()} == {
        "test_pure": ("example", "small", []),
        "test_writes": ("example", "medium", ["filesystem"]),
        "test_writes_only_in_its_fixture": ("example", "small", []),
        "test_live_desktop": ("example", "large", []),
        "test_tagged_property": ("property", "small", []),
    }


def test_without_the_option_nothing_is_recorded(pytester: pytest.Pytester) -> None:
    pytester.makeconftest(_PLUGIN_CONFTEST)
    pytester.makepyfile(test_sample="def test_pure():\n    pass\n")

    pytester.runpytest().assert_outcomes(passed=1)
    assert not any(pathlib.Path(pytester.path).rglob("*.json"))


def test_the_derived_style_marker_reaches_the_suites_own_tests(request: pytest.FixtureRequest) -> None:
    """A derived marker can silently stop applying when a plugin renames what conftest looks for."""
    items = request.session.items
    properties = [i for i in items if i.path.name == "test_coverage_by_test.py" and "never_own_fewer" in i.name]
    if not properties:
        pytest.skip("run with the whole suite collected")

    assert all(i.get_closest_marker("property") for i in properties)

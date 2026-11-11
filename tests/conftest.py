from __future__ import annotations

import os
import sys
from collections.abc import Iterator
from pathlib import Path

import pytest
from hypothesis import HealthCheck, settings
from hypothesis.database import DirectoryBasedExampleDatabase

_REPO_ROOT = Path(__file__).resolve().parent.parent

# Property tests: `dev` explores fresh examples each run and remembers failures under the
# gitignored temp/ (repo rule 5); `ci` is derandomized, so a CI run is reproducible bit for bit
# and a red build can be replayed locally with HYPOTHESIS_PROFILE=ci. No deadline in either:
# xdist workers share CPUs, so wall-clock deadlines are flaky rather than informative.
settings.register_profile(
    "dev",
    # Explicit parent: with CI set, Hypothesis loads its built-in ci profile on import, and a
    # parentless profile would inherit derandomize=True from it (HypoFuzz then refuses to fuzz).
    parent=settings.get_profile("default"),
    deadline=None,
    database=DirectoryBasedExampleDatabase(str(_REPO_ROOT / "temp" / "hypothesis")),
)
settings.register_profile(
    "ci",
    # A fixed budget: derandomized, so every CI run costs the same and replays exactly.
    max_examples=100,
    deadline=None,
    derandomize=True,
    database=None,
    print_blob=True,
    suppress_health_check=[HealthCheck.too_slow],
)
settings.load_profile(os.environ.get("HYPOTHESIS_PROFILE") or ("ci" if os.environ.get("CI") else "dev"))


_SRC_PACKAGES = tuple(f"{p.name}." for p in (_REPO_ROOT / "src").iterdir() if p.is_dir())
_caches: dict[int, list[object]] = {}


def _src_caches() -> list[object]:
    """Every ``functools.cache``/``lru_cache`` defined in a ``src`` module imported so far.

    Rescanned only when ``sys.modules`` has grown, so the per-test cost is a length check.
    """
    seen = len(sys.modules)
    if seen not in _caches:
        _caches.clear()
        _caches[seen] = [
            value
            for name, module in list(sys.modules.items())
            if name.startswith(_SRC_PACKAGES)
            for value in list(vars(module).values())
            if hasattr(value, "cache_clear") and getattr(value, "__module__", None) == name
        ]
    return _caches[seen]


@pytest.fixture(autouse=True)
def clear_src_caches() -> None:
    """Start every test with empty ``src`` caches.

    A test should not pass or fail because another test warmed a cache first. It also keeps
    per-test coverage repeatable: without this, a cached body is credited to whichever test
    happened to call it first on its xdist worker.
    """
    for cached in _src_caches():
        cached.cache_clear()  # pyright: ignore[reportAttributeAccessIssue]


@pytest.fixture(autouse=True)
def forbid_real_virtual_desktop_changes(monkeypatch: pytest.MonkeyPatch) -> None:
    """Fail any test that renames or removes a real Windows virtual desktop.

    The suite runs on the developer's own desktop, where a stray rename silently
    relabels their live workspace (it did: integration restores of the sample plan
    renamed desktops 1 and 2 to ``Work``/``Admin`` on every run). Tests that need
    pyvda swap a fake into ``sys.modules``, which this leaves untouched.
    """
    if sys.platform != "win32":
        return
    try:
        import pyvda  # noqa: TID251  # pyright: ignore[reportMissingImports]
    except Exception:
        return

    def _forbidden(action: str):
        def _fail(*_args, **_kwargs):
            pytest.fail(f"test tried to {action} a real virtual desktop; patch desktop_manager.win32 instead")

        return _fail

    monkeypatch.setattr(pyvda.VirtualDesktop, "rename", _forbidden("rename"))
    monkeypatch.setattr(pyvda.VirtualDesktop, "remove", _forbidden("remove"))
    monkeypatch.setattr(pyvda.VirtualDesktop, "create", _forbidden("create"))


@pytest.fixture(autouse=True)
def forbid_real_wrapup_follower(monkeypatch: pytest.MonkeyPatch) -> None:
    """Fail any test that starts a real `estate wrapup` background follower.

    The follower is a separate process, so none of a test's in-process fakes reach it: it reads
    the live Orca and types into real agent sessions (it did: two live sessions got a test's
    "Done yet?"). A test that covers the background path patches ``wrapup_follow.start`` itself.
    """
    from rig.estate import wrapup_follow

    def refuse(args: list[str], *, now: object) -> Path:
        raise AssertionError(f"test tried to start a real wrap-up follower: {args}")

    monkeypatch.setattr(wrapup_follow, "start", refuse)


@pytest.fixture(autouse=True)
def forbid_real_chrome(monkeypatch: pytest.MonkeyPatch) -> None:
    """Fail any test that reaches the developer's own Chrome.

    On macOS the real driver closes and groups tabs in the live browser: an
    organise test that faked only the Windows driver closed three real tabs. A
    test that needs a driver patches ``backend.get_driver`` (or ``run_jxa``) itself.
    """
    from desktop_manager.chrome import backend
    from desktop_manager.chrome.drivers import macos_jxa

    def _fail(*_args, **_kwargs):
        pytest.fail("test reached the real Chrome driver; patch backend.get_driver or macos_jxa.run_jxa")

    monkeypatch.setattr(backend, "get_driver", _fail)
    monkeypatch.setattr(macos_jxa, "run_jxa", _fail)


@pytest.fixture(autouse=True)
def isolate_trace_output(monkeypatch: pytest.MonkeyPatch, tmp_path_factory: pytest.TempPathFactory) -> Iterator[Path]:
    """Send spans to a temp directory, never the repo's ``temp/traces``.

    Flushes before the env var is restored, so nothing buffered during the test
    reaches the real trace files at interpreter exit.
    """
    from rig import tracing

    trace_dir = tmp_path_factory.mktemp("traces")
    monkeypatch.setenv("SHARED_UTILITIES_TRACE_DIR", str(trace_dir))
    yield trace_dir
    tracing.shutdown_tracing()


@pytest.fixture(autouse=True)
def isolate_app_state_dirs(monkeypatch: pytest.MonkeyPatch, tmp_path_factory: pytest.TempPathFactory) -> None:
    """Point per-user app state at temp directories, never the developer's own.

    Runtime copies, restore logs, Chrome tab state and the default workspace plan all
    live under ``%LOCALAPPDATA%`` or ``%APPDATA%``. A test that forgets to redirect
    them writes into the live install — restore logs from test runs turned up in the
    real ``workspace-restore/logs``. ``DESKTOP_MANAGER_LOG_DIR`` is cleared too:
    ``current_environment()`` sets it process-wide with ``setdefault``, so one test's
    value would otherwise leak into every later one.

    Playwright finds its browsers under ``%LOCALAPPDATA%``, so its path is pinned to
    the real install first. ``WORKSPACE_HOME`` is pointed at an empty folder so the
    developer's Orca marks in ``~/.workspace/config.json`` never change what discovery offers.
    """
    real_local = os.environ.get("LOCALAPPDATA")
    if real_local and "PLAYWRIGHT_BROWSERS_PATH" not in os.environ:
        monkeypatch.setenv("PLAYWRIGHT_BROWSERS_PATH", str(Path(real_local) / "ms-playwright"))
    monkeypatch.delenv("DESKTOP_MANAGER_LOG_DIR", raising=False)
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path_factory.mktemp("localappdata")))
    monkeypatch.setenv("APPDATA", str(tmp_path_factory.mktemp("appdata")))
    monkeypatch.setenv("WORKSPACE_HOME", str(tmp_path_factory.mktemp("workspace-home")))


@pytest.fixture(autouse=True)
def csharp_calls() -> Iterator[list[list[str]]]:
    """Record each hand-over to the C# rig CLI instead of starting dotnet; every one succeeds.

    A Python caller of a ported command (devenv projecting the command policy, say) would
    otherwise build and run the real C# CLI against the developer's home. A test that cares
    what was handed over asks for this fixture and reads the argv lists.
    """
    from rig import csharp_cli

    calls: list[list[str]] = []

    def record(command: list[str], _env: dict[str, str]) -> int:
        calls.append(command)
        return 0

    token = csharp_cli.RUNNER.set(record)
    yield calls
    csharp_cli.RUNNER.reset(token)


def pytest_addoption(parser: pytest.Parser) -> None:
    from tests import sizes

    sizes.addoption(parser)


def pytest_configure(config: pytest.Config) -> None:
    from tests import sizes

    sizes.register(config)


def pytest_collection_modifyitems(config, items) -> None:
    """Tag each test by style, and skip tests marked ``windows`` when not on Windows.

    Style markers are derived, never hand-applied, so the split cannot drift:
    ``property`` for Hypothesis tests and ``bdd`` for pytest-bdd scenarios. Select
    them with ``-m property`` / ``-m bdd``; everything else is example-based.

    Windows tests exercise win32 APIs and Windows path semantics that only behave
    correctly on the target OS; on Linux CI they would fail spuriously.
    """
    for item in items:
        function = getattr(item, "function", None)
        if getattr(function, "is_hypothesis_test", False):
            item.add_marker(pytest.mark.property)
        # pytest-bdd 8 marks a scenario only by this fixture (``__scenario__`` went in 8.0).
        if "_pytest_bdd_example" in getattr(item, "fixturenames", ()):
            item.add_marker(pytest.mark.bdd)

    if not sys.platform.startswith("win"):
        skip_windows = pytest.mark.skip(reason="requires Windows OS with win32 APIs")
        for item in items:
            if "windows" in item.keywords:
                item.add_marker(skip_windows)


@pytest.fixture
def as_windows(monkeypatch: pytest.MonkeyPatch) -> None:
    """Pass ``@requires(Platform.WINDOWS)`` gates for CLI tests whose Windows calls are faked."""
    from rig.platforms import Platform

    monkeypatch.setattr("rig.platforms.get_current_platform", lambda: Platform.WINDOWS)


@pytest.fixture
def work_scope() -> tuple[str, ...]:
    """Give this machine a work scope: `acme/` and `workspace/` under the code root.

    `isolate_app_state_dirs` points `WORKSPACE_HOME` at an empty folder, so without this
    fixture no work scope is configured, as on a machine that never set one.
    """
    dirs = ("acme", "workspace")
    config = Path(os.environ["WORKSPACE_HOME"]) / "config.json"
    _ = config.write_text(f'{{"tools": {{"estate": {{"work_dirs": ["{dirs[0]}", "{dirs[1]}"]}}}}}}', encoding="utf-8")
    return dirs


@pytest.fixture
def clone(tmp_path: Path) -> Path:
    """A real clone with `main` pushed to a bare origin (`tests.fakes.git.clone_with_origin`)."""
    from tests.fakes.git import clone_with_origin

    return clone_with_origin(tmp_path)

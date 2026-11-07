"""`desktop-manager runtime install` puts `rig` on PATH, pointing at the runtime's `current` copy."""

from __future__ import annotations

from pathlib import Path

import pytest

from desktop_manager import rig_shim, runtime

pytestmark = pytest.mark.ci


def test_the_posix_shim_execs_the_target_with_every_argument() -> None:
    text = rig_shim.render(Path("/a b/current/.venv/bin/rig"), windows=False)
    assert text.startswith("#!/bin/sh\n")
    assert rig_shim.MARKER in text
    assert text.endswith('exec "/a b/current/.venv/bin/rig" "$@"\n')


def test_the_windows_shim_passes_every_argument() -> None:
    text = rig_shim.render(Path(r"C:\rt\current\.venv\Scripts\rig.exe"), windows=True)
    assert text.startswith("@echo off\r\n")
    assert rig_shim.MARKER in text
    assert text.endswith('"C:\\rt\\current\\.venv\\Scripts\\rig.exe" %*\r\n')


def test_install_writes_then_leaves_an_identical_shim(tmp_path: Path) -> None:
    shim = tmp_path / "bin" / "rig"
    assert rig_shim.install(shim, tmp_path / "runtime") is rig_shim.Outcome.WRITTEN
    target = runtime.executable(runtime.current_link(tmp_path / "runtime"), "rig")
    assert str(target) in shim.read_text(encoding="utf-8")
    assert rig_shim.install(shim, tmp_path / "runtime") is rig_shim.Outcome.UNCHANGED


def test_install_rewrites_its_own_older_shim(tmp_path: Path) -> None:
    shim = tmp_path / "rig"
    _ = shim.write_text(f"# {rig_shim.MARKER}\nstale-line\n", encoding="utf-8")
    assert rig_shim.install(shim, tmp_path / "runtime") is rig_shim.Outcome.WRITTEN
    assert "stale-line" not in shim.read_text(encoding="utf-8")


def test_install_leaves_a_file_rig_did_not_write(tmp_path: Path) -> None:
    shim = tmp_path / "rig"
    _ = shim.write_text("#!/bin/sh\necho mine\n", encoding="utf-8")
    assert rig_shim.install(shim, tmp_path / "runtime") is rig_shim.Outcome.FOREIGN
    assert shim.read_text(encoding="utf-8") == "#!/bin/sh\necho mine\n"


@pytest.mark.parametrize(
    ("outcome", "said"),
    [
        (rig_shim.Outcome.WRITTEN, "runs the current copy"),
        (rig_shim.Outcome.UNCHANGED, "rig on PATH"),
        (rig_shim.Outcome.FOREIGN, "rig did not write it"),
    ],
)
def test_install_says_what_happened_to_the_shim(
    outcome: rig_shim.Outcome, said: str, monkeypatch: pytest.MonkeyPatch
) -> None:
    from rich.console import Console

    from desktop_manager import runtime_cli

    console = Console(record=True, width=200)
    monkeypatch.setattr(runtime_cli, "console", console)
    runtime_cli._report_shim(outcome)  # pyright: ignore[reportPrivateUsage]
    assert said in console.export_text()

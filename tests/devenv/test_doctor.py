"""Tests for environment doctor diagnostics."""

import pytest

from rig import csharp_cli
from rig.devenv.doctor.core import run_doctor


@pytest.mark.ci
def test_run_doctor_runs_cleanly(monkeypatch: pytest.MonkeyPatch):
    def fake_git_config(key: str) -> str:
        return "developer" if key == "user.name" else "dev@example.com"

    monkeypatch.setattr("rig.devenv.steps.git.get_git_config_val", fake_git_config)
    rc = run_doctor()
    # Returns 0 when all required tools are healthy or 1 if missing tools
    assert rc in (0, 1)


@pytest.mark.ci
def test_doctor_checks_the_hook_through_rig_devenv_claude(csharp_calls: list[list[str]]):
    _ = run_doctor()

    assert ["devenv", "claude", "--check"] in [call[call.index("--") + 1 :] for call in csharp_calls]


@pytest.mark.ci
def test_doctor_counts_a_hook_that_is_not_current():
    def not_current(_command: list[str], _env: dict[str, str]) -> int:
        return 1

    token = csharp_cli.RUNNER.set(not_current)
    try:
        assert run_doctor() == 1
    finally:
        csharp_cli.RUNNER.reset(token)


@pytest.mark.ci
def test_doctor_checks_the_gh_overlay_through_rig_devenv_gh(csharp_calls: list[list[str]]):
    _ = run_doctor()

    assert ["devenv", "gh", "--check"] in [call[call.index("--") + 1 :] for call in csharp_calls]


@pytest.mark.ci
def test_doctor_lists_fonts_through_rig_devenv_fonts(csharp_calls: list[list[str]]):
    _ = run_doctor()

    assert ["devenv", "fonts", "--check"] in [call[call.index("--") + 1 :] for call in csharp_calls]

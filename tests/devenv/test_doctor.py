"""Tests for environment doctor diagnostics."""

import pytest

from rig.devenv.doctor.core import run_doctor


@pytest.mark.ci
def test_run_doctor_runs_cleanly(monkeypatch: pytest.MonkeyPatch):
    def fake_git_config(key: str) -> str:
        return "developer" if key == "user.name" else "dev@example.com"

    monkeypatch.setattr("rig.devenv.steps.git.get_git_config_val", fake_git_config)
    rc = run_doctor()
    # Returns 0 when all required tools are healthy or 1 if missing tools
    assert rc in (0, 1)

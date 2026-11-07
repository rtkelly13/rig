from __future__ import annotations

from pathlib import Path

from rig.repo_governance.workflow_concurrency import (
    inspect_workflow_concurrency,
)


def test_inspect_workflow_concurrency_flags_missing_block(tmp_path: Path):
    wf = tmp_path / "ci.yml"
    wf.write_text(
        """name: CI
on:
  pull_request:
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - run: echo "hello"
""",
        encoding="utf-8",
    )
    issues = inspect_workflow_concurrency(wf)
    assert len(issues) == 1
    assert "missing top-level 'concurrency' block" in issues[0]


def test_inspect_workflow_concurrency_passes_with_cancel_in_progress(tmp_path: Path):
    wf = tmp_path / "ci.yml"
    wf.write_text(
        """name: CI
on:
  pull_request:
concurrency:
  group: ci-${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - run: echo "hello"
""",
        encoding="utf-8",
    )
    issues = inspect_workflow_concurrency(wf)
    assert len(issues) == 0


def test_inspect_workflow_concurrency_ignores_non_pr_workflows(tmp_path: Path):
    wf = tmp_path / "backup.yml"
    wf.write_text(
        """name: Backup
on:
  schedule:
    - cron: '0 0 * * *'
jobs:
  backup:
    runs-on: ubuntu-latest
    steps:
      - run: echo "backup"
""",
        encoding="utf-8",
    )
    issues = inspect_workflow_concurrency(wf)
    assert len(issues) == 0

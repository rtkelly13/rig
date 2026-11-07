import json
import pathlib

import pytest

from rig.repo_governance.generate_coverage_comment import generate_comment

pytestmark = pytest.mark.ci


def test_comment_lists_files_least_covered_first(tmp_path: pathlib.Path):
    report = {
        "meta": {"version": "7"},
        "totals": {"percent_covered": 75.5, "covered_lines": 3, "num_statements": 4},
        "files": {
            "/home/runner/work/src/pkg/a.py": {
                "summary": {"covered_lines": 10, "num_statements": 10, "percent_covered": 100},
                "missing_lines": [],
                "executed_lines": [1, 2],
            },
            "src/pkg/b.py": {
                "summary": {"covered_lines": 1, "num_statements": 8, "percent_covered": 12.5},
                "missing_lines": [1, 2, 3, 4, 5, 6, 7],
            },
        },
    }
    source = tmp_path / "coverage.json"
    source.write_text(json.dumps(report), encoding="utf-8")
    out = tmp_path / "summary.md"

    generate_comment(str(source), str(out))

    text = out.read_text(encoding="utf-8")
    assert "**Overall Code Coverage:** `75.5%`" in text
    rows = [line for line in text.splitlines() if line.startswith("| `")]
    assert rows == [
        "| `src/pkg/b.py` | `1/8` | `1, 2, 3, 4, 5 (+2 more)` | `12.5%` |",
        "| `src/pkg/a.py` | `10/10` | `None 🎉` | `100.0%` |",
    ]

import pathlib
import sys
from dataclasses import dataclass, field

from pydantic import StrictFloat, StrictInt

from rig.typed_json import decode_json


@dataclass(frozen=True, slots=True)
class _Summary:
    covered_lines: StrictInt = 0
    num_statements: StrictInt = 0
    percent_covered: StrictFloat | StrictInt = 0.0


@dataclass(frozen=True, slots=True)
class _FileCoverage:
    summary: _Summary = field(default_factory=_Summary)
    missing_lines: tuple[StrictInt, ...] = ()


@dataclass(frozen=True, slots=True)
class _CoverageReport:
    """The parts of coverage.py's ``coverage json`` output the comment reads."""

    totals: _Summary = field(default_factory=_Summary)
    files: dict[str, _FileCoverage] = field(default_factory=dict[str, _FileCoverage])


def _missing_text(missing: tuple[int, ...]) -> str:
    missing_str = ", ".join(str(m) for m in missing[:5])
    if len(missing) > 5:
        missing_str += f" (+{len(missing) - 5} more)"
    return missing_str or "None 🎉"


def generate_comment(json_path: str = "coverage.json", output_path: str = "coverage_summary.md"):
    p = pathlib.Path(json_path)
    if not p.exists():
        sys.stderr.write(f"Coverage JSON {json_path} not found.\n")
        sys.exit(0)

    report = decode_json(_CoverageReport, p.read_text(encoding="utf-8"))

    lines = [
        "## 📊 Code Coverage Analysis Report",
        "",
        f"**Overall Code Coverage:** `{report.totals.percent_covered:.1f}%`",
        "",
        "| Module / File | Executed Lines | Missing Lines | Coverage % |",
        "| :--- | :--- | :--- | :--- |",
    ]

    for filepath, fdata in sorted(report.files.items(), key=lambda item: item[1].summary.percent_covered):
        summary = fdata.summary
        missing_str = _missing_text(fdata.missing_lines)

        rel_path = filepath
        if "src/" in filepath:
            rel_path = filepath[filepath.find("src/") :]

        lines.append(
            f"| `{rel_path}` | `{summary.covered_lines}/{summary.num_statements}` "
            f"| `{missing_str}` | `{summary.percent_covered:.1f}%` |"
        )

    lines.append("")
    lines.append("> *Generated automatically by GitHub Actions CI via `shared-utilities` governance toolkit.*")

    pathlib.Path(output_path).write_text("\n".join(lines), encoding="utf-8")
    sys.stderr.write(f"Coverage markdown comment written to {output_path}\n")


if __name__ == "__main__":
    json_arg = sys.argv[1] if len(sys.argv) > 1 else "coverage.json"
    out_arg = sys.argv[2] if len(sys.argv) > 2 else "coverage_summary.md"
    generate_comment(json_arg, out_arg)

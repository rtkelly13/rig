#!/usr/bin/env python3
"""Deterministic Cobertura coverage diff -> Markdown for a sticky PR comment.

Replaces the previous agentic (gh-aw) coverage-diff workflow with a plain,
deterministic step: same input coverage reports always produce the same comment.

Usage:
    coverage_diff.py <baseline_coverage.xml> <pr_coverage.xml> <output.md>

PR context is read from the environment:
    BASELINE_BRANCH, PR_NUMBER, PR_SHA, RUN_URL
"""

from __future__ import annotations

import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

CHANGED_THRESHOLD = 0.5  # pp; files with |delta| <= this are omitted
REGRESSION_THRESHOLD = 2.0  # pp; files worse than this get a callout

GENERATED_MARKERS = (
    ".git/",
    ".venv/",
    "venv/",
    "build/",
    "dist/",
    "node_modules/",
    ".mypy_cache/",
    ".pytest_cache/",
    "site-packages/",
)


def parse_cobertura(path):
    """Return {filename: (covered_lines, executable_lines)}.

    Aggregated by (filename, line number) so a file split across multiple
    <class> elements is never double counted. A line counts as covered when
    hits > 0 in any class that references it. Returns None if the file is absent.
    """
    if not path or not Path(path).is_file():
        return None
    root = ET.parse(path).getroot()
    files: dict[str, dict[int, bool]] = {}
    for cls in root.iter("class"):
        filename = cls.get("filename")
        if not filename:
            continue
        norm = filename.replace("\\", "/")
        if any(marker in norm for marker in GENERATED_MARKERS):
            continue
        lines = files.setdefault(norm, {})
        for line in cls.iter("line"):
            try:
                number = int(line.get("number"))
                hits = int(line.get("hits", "0"))
            except (TypeError, ValueError):
                continue
            lines[number] = lines.get(number, False) or (hits > 0)
    result = {}
    for filename, lines in files.items():
        if not lines:
            continue
        covered = sum(1 for is_covered in lines.values() if is_covered)
        result[filename] = (covered, len(lines))
    return result


def pct(covered, executable):
    return (covered / executable * 100.0) if executable else 0.0


def in_checkout(filename):
    # coverage.py filenames are relative to the repo root; also allow a src/ prefix.
    return Path(filename).exists() or (Path("src") / filename).exists()


def write(path, body):
    Path(path).write_text(body, encoding="utf-8")
    sys.stdout.write(body + "\n")


def main():
    baseline_path, pr_path, out_path = sys.argv[1], sys.argv[2], sys.argv[3]
    branch = os.environ.get("BASELINE_BRANCH", "main")
    number = os.environ.get("PR_NUMBER", "?")
    sha = os.environ.get("PR_SHA", "")
    run_url = os.environ.get("RUN_URL", "")
    short_sha = sha[:7] if sha else "?"

    pr = parse_cobertura(pr_path)
    if pr is None:
        write(
            out_path,
            (
                "## Coverage Diff\n\n"
                "Coverage diff is waiting for CI artifacts.\n\n"
                "- Baseline artifact: `coverage-main`\n"
                f"- PR artifact: `coverage-pr-{number}-{sha}`\n"
                f"- Workflow run: {run_url}\n"
            ),
        )
        return

    baseline = parse_cobertura(baseline_path)  # None on the first PR / no baseline
    pr = {f: v for f, v in pr.items() if in_checkout(f)}

    pr_overall = pct(sum(v[0] for v in pr.values()), sum(v[1] for v in pr.values()))
    if baseline is None:
        overall_line = f"**Overall coverage:** `n/a` -> `{pr_overall:.1f}%` (no baseline yet)"
        base_note = "- No `coverage-main` baseline artifact found yet; showing PR coverage only."
        baseline = {}
    else:
        base_overall = pct(sum(v[0] for v in baseline.values()), sum(v[1] for v in baseline.values()))
        delta = pr_overall - base_overall
        overall_line = f"**Overall coverage:** `{base_overall:.1f}%` -> `{pr_overall:.1f}%` (`{delta:+.1f} pp`)"
        base_note = "- Compared only files present in the current checkout."

    rows = []
    for filename in sorted(set(pr) | set(baseline)):
        base_pct = pct(*baseline[filename]) if filename in baseline else 0.0
        pr_pct = pct(*pr[filename]) if filename in pr else 0.0
        rows.append((filename, base_pct, pr_pct, pr_pct - base_pct))

    changed = sorted(
        (r for r in rows if abs(r[3]) > CHANGED_THRESHOLD), key=lambda r: r[3]
    )  # largest regression (most negative) first
    regressions = [r for r in changed if r[3] < -REGRESSION_THRESHOLD]

    out = [
        "## Coverage Diff\n",
        f"**Baseline branch:** `{branch}`  ",
        f"**PR:** `#{number}` at `{short_sha}`  ",
        overall_line + "\n",
        "### Changed files\n",
    ]
    if changed:
        out.append("| File | Baseline % | PR % | Delta |")
        out.append("| --- | ---: | ---: | ---: |")
        out += [f"| `{f}` | {b:.1f} | {p:.1f} | {d:+.1f} |" for f, b, p, d in changed]
    else:
        out.append("No file-level changes above 0.5 pp.")
    out.append("")
    if regressions:
        out.append("### Regressions over 2.0 pp\n")
        out += [f"- `{f}`: {b:.1f}% -> {p:.1f}% (`{d:+.1f} pp`)" for f, b, p, d in regressions]
        out.append("")
    out.append("### Notes\n")
    out.append(base_note)
    out.append("- Omitted files with absolute delta <= 0.5 pp.")
    if run_url:
        out.append(f"- Workflow run: {run_url}")
    write(out_path, "\n".join(out) + "\n")


if __name__ == "__main__":
    main()

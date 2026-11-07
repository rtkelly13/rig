#!/usr/bin/env python3
"""Fail unless every job the aggregate ``ci`` job needs succeeded.

Usage:
    require_jobs_succeeded.py

Reads ``NEEDS_JSON``, the workflow's ``toJSON(needs)``: a mapping of job id to an
object whose ``result`` is ``success``, ``failure``, ``cancelled`` or ``skipped``.
"""

from __future__ import annotations

import json
import os
import sys


def failed_jobs(needs: object) -> list[str]:
    if not isinstance(needs, dict) or not needs:
        raise SystemExit("::error::NEEDS_JSON is not a non-empty mapping of jobs.")
    failed: list[str] = []
    for job, outcome in needs.items():
        result = outcome.get("result") if isinstance(outcome, dict) else None
        if result not in ("success", "skipped"):
            failed.append(f"{job} ({result})")
    return sorted(failed)


def main() -> int:
    failed = failed_jobs(json.loads(os.environ["NEEDS_JSON"]))
    if failed:
        sys.stdout.write(f"::error::Required jobs did not succeed: {', '.join(failed)}\n")
        return 1
    sys.stdout.write("All checks passed.\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())

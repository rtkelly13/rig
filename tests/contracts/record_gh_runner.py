"""Records the real `rig.proc.run_gh` answering the queries `rig.github_prs.resolve_repo` makes.

Run it when `build_query` changes, or when the contract test reports an unrecorded query:

    uv run python -m tests.contracts.record_gh_runner

It needs `gh` signed in, and rewrites `tests/contracts/recordings/gh_runner.json`. The scenarios
in `SCENARIOS` are what `tests/contracts/test_gh_runner_contract.py` replays and mirrors with
`FakeGh`; after re-recording, update that test's fake world to match what GitHub now says.
"""

from __future__ import annotations

import json
import sys
from dataclasses import dataclass, field
from pathlib import Path

from rig.github_prs import resolve_repo
from rig.proc import GhResult, run_gh

RECORDING = Path(__file__).parent / "recordings" / "gh_runner.json"

#: (owner, repo, PR numbers). A public repo with merged, closed and open PRs plus a number that does
#: not exist, and a repository that does not exist.
SCENARIOS: tuple[tuple[str, str, tuple[int, ...]], ...] = (
    ("rtkelly-labs", "mermaid-toolkit", (38, 70, 71, 72, 999999)),
    ("rtkelly-labs", "no-such-repo-contract", (1,)),
)


@dataclass
class _Recorder:
    answers: dict[str, dict[str, object]] = field(default_factory=dict[str, dict[str, object]])

    def __call__(self, query: str) -> GhResult:
        result = run_gh(query)
        self.answers[query] = {"returncode": result.returncode, "stdout": result.stdout, "stderr": result.stderr}
        return result


def main() -> None:
    recorder = _Recorder()
    for owner, repo, numbers in SCENARIOS:
        _ = resolve_repo(owner, repo, numbers, recorder)
    RECORDING.parent.mkdir(parents=True, exist_ok=True)
    _ = RECORDING.write_text(json.dumps(recorder.answers, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    _ = sys.stdout.write(f"Recorded {len(recorder.answers)} answers to {RECORDING}\n")


if __name__ == "__main__":
    main()

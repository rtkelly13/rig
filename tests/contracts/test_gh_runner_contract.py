"""Contract: `FakeGh` answers the PR resolver's queries the way the real `run_gh` does.

One suite, run against both stand-ins for the `GhRunner` port: the fake every resolver test uses,
and `RecordedGh`, which replays what `run_gh` answered for the same queries. When the real API's
answer changes shape (a new error format, a paging rule), the recorded side moves and the fake
must be fixed to match, so the resolver tests keep testing against GitHub rather than a guess.

The recording is real `gh api graphql` output. Re-record when `build_query` changes or this suite
reports an unrecorded query:

    uv run python -m tests.contracts.record_gh_runner

then update `_fake_world` to what GitHub now says.
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass

import pytest

from rig.github_prs import RepoResolution, resolve_repo
from rig.proc import GhResult, GhRunner
from rig.typed_json import decode_json
from tests.contracts.record_gh_runner import RECORDING, SCENARIOS
from tests.fakes.gh_runner import FakeGh, FakeRepo, as_port

pytestmark = pytest.mark.ci


@dataclass(frozen=True)
class _Answer:
    returncode: int
    stdout: str
    stderr: str


@dataclass
class RecordedGh:
    """The real `run_gh`, replayed from `RECORDING`. Keyed on the whole query, so a changed query fails loudly."""

    answers: dict[str, _Answer]

    def __call__(self, query: str) -> GhResult:
        answer = self.answers.get(query)
        if answer is None:
            pytest.fail(
                f"unrecorded query, re-record with `uv run python -m tests.contracts.record_gh_runner`: {query}"
            )
        return GhResult(answer.returncode, answer.stdout, answer.stderr)


def _recorded() -> GhRunner:
    return RecordedGh(decode_json(dict[str, _Answer], RECORDING.read_bytes()))


def _open(number: int, head: str, created: str) -> dict[str, object]:
    return {"number": number, "headRefName": head, "baseRefName": "main", "createdAt": created}


def _fake_world() -> GhRunner:
    """What GitHub held for `SCENARIOS` when they were recorded."""
    mermaid = FakeRepo(
        states={38: "CLOSED", 70: "MERGED", 71: "MERGED", 72: "MERGED"},
        open_nodes=[
            _open(73, "dependabot/npm_and_yarn/dev-tooling-4535283de0", "2026-11-02T00:47:31Z"),
            _open(74, "dependabot/npm_and_yarn/tsup-9.0.0", "2026-11-02T00:48:09Z"),
            _open(75, "dependabot/npm_and_yarn/pele-1.12.0", "2026-11-02T00:48:17Z"),
            _open(76, "dependabot/npm_and_yarn/types/node-26.6.6", "2026-11-02T00:48:29Z"),
            _open(77, "dependabot/npm_and_yarn/typescript-7.0.2", "2026-11-02T00:48:42Z"),
        ],
    )
    return as_port(FakeGh({"rtkelly-labs/mermaid-toolkit": mermaid}))


RUNNERS: dict[str, Callable[[], GhRunner]] = {"fake": _fake_world, "recorded": _recorded}


def _resolve_all(runner: GhRunner) -> list[RepoResolution]:
    return [resolve_repo(owner, repo, numbers, runner) for owner, repo, numbers in SCENARIOS]


@pytest.mark.parametrize("name", sorted(RUNNERS))
def test_resolver_reads_every_state_and_failure(name: str) -> None:
    found, gone = _resolve_all(RUNNERS[name]())
    assert {n: s.state for n, s in found.states.items()} == {
        38: "CLOSED",
        70: "MERGED",
        71: "MERGED",
        72: "MERGED",
        999999: "UNKNOWN",
    }
    assert found.states[999999].error == "Could not resolve to a PullRequest with the number of 999999."
    assert found.error == ""
    assert gone.error == "Could not resolve to a Repository with the name 'rtkelly-labs/no-such-repo-contract'."
    assert gone.chains == []


def test_the_fake_answers_exactly_as_the_recording() -> None:
    assert _resolve_all(_fake_world()) == _resolve_all(_recorded())

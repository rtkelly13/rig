"""The triage inbox's agent verdicts: read from `estate wrapup` state files, matched to PRs by branch."""

from __future__ import annotations

import subprocess
from dataclasses import replace
from pathlib import Path

import pytest

from rig import proc
from rig.estate.wrapup_state import AnswerRecord, AskRecord, WrapupState
from rig.gh_origin import RepoIdentity
from rig.triage import agents, web
from rig.triage.agents import AgentSignal
from rig.triage.facts import Facts
from rig.triage.models import Entry
from rig.workspace.layout import project_dir
from rig.workspace.models import ManagedRepoSpec
from tests.test_triage_queue import item

pytestmark = pytest.mark.ci

ASK = AskRecord("2026-11-07T12:00:00.000+00:00", "t:l", "s", "a" * 40, "clean; PR #9 open", "abc")


def _answer(at: str, verdict: str, detail: str = "") -> AnswerRecord:
    return AnswerRecord(at, "t:l", verdict, detail, "b" * 40, ASK.at)


def test_the_newest_event_wins() -> None:
    assert agents.latest(WrapupState(), "wt") is None
    asked = agents.latest(WrapupState(asks=(ASK,)), "wt")
    assert asked == AgentSignal("asked", "clean; PR #9 open", ASK.at, "a" * 40, "wt")
    pending = _answer("2026-11-07T12:05:00.000+00:00", "pending", "merge PR #9")
    assert agents.latest(WrapupState(asks=(ASK,), answers=(pending,)), "wt") == AgentSignal(
        "pending", "merge PR #9", pending.at, "b" * 40, "wt"
    )
    done = _answer("2026-11-07T12:30:00.000+00:00", "done")
    signal = agents.latest(WrapupState(asks=(ASK,), answers=(done, pending)), "wt")
    assert signal is not None and signal.verdict == "done"
    later = replace(ASK, at="2026-11-07T13:00:00.000+00:00")
    signal = agents.latest(WrapupState(asks=(ASK, later), answers=(done,)), "wt")
    assert signal is not None and signal.verdict == "asked"


def _git(*args: str, cwd: Path) -> None:
    assert proc.git("-c", "user.name=t", "-c", "user.email=t@example.invalid", *args, cwd=cwd).ok


def test_scan_keys_each_worktree_by_repo_and_branch(tmp_path: Path) -> None:
    repo = tmp_path / "repo"
    repo.mkdir()
    _git("init", "-q", "-b", "main", cwd=repo)
    _git("commit", "-q", "--allow-empty", "-m", "init", cwd=repo)
    feature = tmp_path / "feature"
    _git("worktree", "add", "-q", "-b", "rtk/feature", str(feature), cwd=repo)
    state = WrapupState(branch="rtk/feature", asks=(ASK,))

    def read(path: Path) -> WrapupState | None:
        return state if path.resolve() == feature.resolve() else None

    found = agents.scan({"owner/repo": repo, "owner/gone": tmp_path / "missing"}, read)
    assert list(found) == [agents.Key("owner/repo", "rtk/feature")]
    assert found[agents.Key("owner/repo", "rtk/feature")].worktree == "feature"


def test_signals_are_reused_within_the_ttl(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(agents, "_cache", type(agents._cache)())  # pyright: ignore[reportPrivateUsage]
    calls: list[int] = []

    def roots() -> dict[str, Path]:
        calls.append(1)
        return {}

    base = 1e12
    assert agents.signals(roots=roots, clock=lambda: base) == {}
    assert agents.signals(roots=roots, clock=lambda: base + 1) == {}
    assert len(calls) == 1
    _ = agents.signals(roots=roots, clock=lambda: base + agents.TTL_SECONDS)
    assert len(calls) == 2


def test_each_managed_repo_maps_to_its_checkout(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    config = tmp_path / "rig" / "config" / "managed-projects.json"
    estate = tmp_path / "code" / "personal"
    specs = [ManagedRepoSpec("rig", "rtkelly13/rig"), ManagedRepoSpec("Other", "https://example.invalid/Other.git")]

    def specs_for(_cfg: Path | None) -> list[ManagedRepoSpec]:
        return specs

    def identity(root: Path) -> RepoIdentity:
        return RepoIdentity(root, "git@github.com:rtkelly13/rig.git")

    monkeypatch.setattr(agents, "load_managed_specs", specs_for)
    monkeypatch.setattr(agents, "estate_root_path", lambda: estate)
    monkeypatch.setattr(agents, "get_repo_root_and_origin", identity)
    assert agents.estate_roots(config) == {
        "rtkelly13/rig": tmp_path / "rig",
        "https://example.invalid/other": project_dir(estate, "Other", "https://example.invalid/Other.git"),
    }


def _entry(**facts: object) -> Entry:
    return Entry(item(facts=Facts(**facts)), ())  # pyright: ignore[reportArgumentType]


def test_the_payload_carries_the_verdict_for_the_branch(monkeypatch: pytest.MonkeyPatch) -> None:
    signal = AgentSignal("done", "", ASK.at, "a" * 40, "feature")
    monkeypatch.setattr(agents, "signals", lambda: {agents.Key("owner/repo", "rtk/feature"): signal})
    rows = web.with_agents(
        [
            _entry(head="rtk/feature"),
            _entry(head="rtk/feature", cross_repository=True),
            _entry(head="other"),
            Entry(item(), ()),
        ]
    )
    assert [row.agent for row in rows] == [signal, None, None, None]


def test_a_failed_scan_leaves_the_inbox_working(monkeypatch: pytest.MonkeyPatch) -> None:
    def fail() -> dict[agents.Key, AgentSignal]:
        raise subprocess.SubprocessError("git missing")

    monkeypatch.setattr(agents, "signals", fail)
    assert web.with_agents([_entry(head="rtk/feature")])[0].agent is None

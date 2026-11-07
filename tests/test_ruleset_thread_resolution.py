"""The governance ruleset requires PR review threads to be resolved, and drift from that is seen and fixed."""

from __future__ import annotations

import json
import subprocess
from collections.abc import Callable
from dataclasses import dataclass

import pytest

from rig.repo_governance.github_rulesets import (
    RULESET_NAME,
    align_github_ruleset,
    build_ruleset_payload,
    inspect_github_ruleset,
    registered_required_checks,
)
from rig.typed_json import JsonObject, decode_json

pytestmark = pytest.mark.ci

REGISTERED = "rtkelly13/blog"
UNREGISTERED = "rtkelly13/no-such-registered-repo-xyz"


@dataclass(frozen=True, slots=True)
class _PullRequestParameters:
    required_review_thread_resolution: bool | None = None


def _rules(payload: JsonObject) -> list[JsonObject]:
    return decode_json(list[JsonObject], json.dumps(payload["rules"]))


def _rule(payload: JsonObject, kind: str) -> JsonObject | None:
    return next((rule for rule in _rules(payload) if rule["type"] == kind), None)


def test_every_ruleset_requires_review_threads_resolved() -> None:
    for repo in (REGISTERED, UNREGISTERED):
        pull_request = _rule(build_ruleset_payload(repo), "pull_request")
        assert pull_request is not None
        parameters = decode_json(_PullRequestParameters, json.dumps(pull_request["parameters"]))
        assert parameters.required_review_thread_resolution is True


def test_an_unregistered_repo_gets_no_status_checks_rather_than_borrowed_ones() -> None:
    assert _rule(build_ruleset_payload(REGISTERED), "required_status_checks") is not None
    assert _rule(build_ruleset_payload(UNREGISTERED), "required_status_checks") is None


# --------------------------------------------------------------------------- inspection against a fake gh


type Responder = Callable[[list[str]], str]


def _fake_gh(monkeypatch: pytest.MonkeyPatch, respond: Responder, calls: list[list[str]]) -> None:
    def run(args: list[str], **_kwargs: object) -> subprocess.CompletedProcess[str]:
        calls.append(args)
        return subprocess.CompletedProcess(args, 0, stdout=respond(args), stderr="")

    monkeypatch.setattr(subprocess, "run", run)


def _api(rulesets: list[JsonObject], detail: JsonObject) -> Responder:
    def respond(args: list[str]) -> str:
        endpoint = next(a for a in args if a.startswith("repos/"))
        return json.dumps(detail if endpoint.count("/") == 4 else rulesets)

    return respond


def _detail(*, enforcement: str = "active", resolve: bool | None = True) -> JsonObject:
    payload = build_ruleset_payload(REGISTERED)
    pull_request = _rule(payload, "pull_request")
    assert pull_request is not None
    parameters = decode_json(JsonObject, json.dumps(pull_request["parameters"]))
    if resolve is None:
        parameters.pop("required_review_thread_resolution")
    else:
        parameters["required_review_thread_resolution"] = resolve
    pull_request["parameters"] = parameters
    payload["rules"] = [pull_request if rule["type"] == "pull_request" else rule for rule in _rules(payload)]
    payload["enforcement"] = enforcement
    return payload


def test_an_active_ruleset_that_requires_resolution_is_aligned(monkeypatch: pytest.MonkeyPatch) -> None:
    calls: list[list[str]] = []
    _fake_gh(monkeypatch, _api([{"id": 7, "name": RULESET_NAME, "enforcement": "active"}], _detail()), calls)
    result = inspect_github_ruleset(REGISTERED)
    assert (result.status, result.is_aligned, result.id) == ("ACTIVE", True, 7)
    assert align_github_ruleset(REGISTERED).status == "already_active"


@pytest.mark.parametrize(
    ("detail", "words"),
    [
        (_detail(resolve=False), "pull_request: live"),
        (_detail(resolve=None), "pull_request: live"),
        (_detail(enforcement="disabled"), "enforcement disabled"),
        ({"enforcement": "active", "rules": [{"type": "deletion"}]}, "missing rule pull_request"),
    ],
)
def test_drift_is_reported_and_repaired_in_place(
    monkeypatch: pytest.MonkeyPatch, detail: JsonObject, words: str
) -> None:
    calls: list[list[str]] = []
    listed: list[JsonObject] = [{"id": 7, "name": RULESET_NAME, "enforcement": "active"}]
    _fake_gh(monkeypatch, _api(listed, detail), calls)

    result = inspect_github_ruleset(REGISTERED)
    assert not result.is_aligned
    assert words in result.status
    assert result.id == 7

    planned = align_github_ruleset(REGISTERED, dry_run=True)
    assert (planned.status, planned.action) == ("dry_run", "PUT")


def test_a_disabled_ruleset_is_updated_not_duplicated(monkeypatch: pytest.MonkeyPatch) -> None:
    calls: list[list[str]] = []
    listed: list[JsonObject] = [{"id": 9, "name": RULESET_NAME, "enforcement": "disabled"}]
    _fake_gh(monkeypatch, _api(listed, _detail(enforcement="disabled")), calls)
    assert align_github_ruleset(REGISTERED).status == "success"
    assert ["gh", "api", "-X", "PUT", f"repos/{REGISTERED}/rulesets/9", "--input", "-"] in calls


def test_a_missing_ruleset_is_created(monkeypatch: pytest.MonkeyPatch) -> None:
    calls: list[list[str]] = []
    _fake_gh(monkeypatch, _api([], _detail()), calls)
    assert inspect_github_ruleset(REGISTERED).status == "MISSING"
    assert align_github_ruleset(REGISTERED, dry_run=True).action == "POST"


def test_profile_check_names_are_registered():
    assert registered_required_checks("rtkelly-labs/Diffra") == ["build", "pr-title"]


@pytest.mark.parametrize("error", [OSError("gh unavailable"), subprocess.CompletedProcess(["gh"], 1, "", "Forbidden")])
def test_failed_inspection_never_creates_a_duplicate(monkeypatch, error):
    calls = []

    def run(args, **kwargs):
        calls.append(args)
        if isinstance(error, OSError):
            raise error
        return error

    monkeypatch.setattr(subprocess, "run", run)
    assert align_github_ruleset(REGISTERED).status == "error"
    assert len(calls) == 1


@pytest.mark.parametrize(
    "field,value,words",
    [
        ("target", "tag", "must target branches"),
        ("conditions", {"ref_name": {"include": ["refs/heads/other"], "exclude": []}}, "branch conditions"),
        ("bypass_actors", [{"actor_id": 1, "actor_type": "Team", "bypass_mode": "always"}], "bypass actors"),
    ],
)
def test_ruleset_must_cover_main_without_bypass(monkeypatch, field, value, words):
    detail = _detail()
    detail[field] = value
    calls = []
    _fake_gh(monkeypatch, _api([{"id": 7, "name": RULESET_NAME, "enforcement": "active"}], detail), calls)
    result = inspect_github_ruleset(REGISTERED)
    assert not result.is_aligned
    assert words in result.status

"""`repo-governance plan` through its seams: the policy, a checkout on disk, and a fake GitHub."""

from __future__ import annotations

import pathlib

import pytest
from typer.testing import CliRunner

from rig.proc import ApiRequest, GitHubApi
from rig.repo_governance.align_repo_conventions import app
from rig.repo_governance.cli_plan import PlanContext
from rig.repo_governance.github_rulesets import RULESET_NAME, RepoSettings, build_ruleset_payload
from rig.repo_governance.label_taxonomy import STANDARD_LABEL_TAXONOMY, RepoLabel, label_differences
from rig.repo_governance.pages_manager import pages_drift, read_pages
from rig.repo_governance.policy_config import PagesPolicy, PolicyConfig
from rig.repo_governance.resource import RepoPlan
from rig.typed_json import decode_json
from tests.fakes.github_rest import FakeGitHubApi

SLUG = "acme/widget"
SETTINGS = {
    "default_branch": "main",
    "delete_branch_on_merge": True,
    "allow_squash_merge": True,
    "allow_merge_commit": False,
    "allow_rebase_merge": False,
    "allow_auto_merge": True,
    "private": False,
}
WORKFLOW = """\
on: pull_request
jobs:
  lint:
    runs-on: ubuntu-latest
    steps:
      - run: "true"
"""


def _config(required: list[str], **layer: object) -> PolicyConfig:
    return PolicyConfig.parse(
        {"defaults": {"expected_owner": "acme", "allowed_owners": ["acme"], "required_checks": required, **layer}}
    )


def _checkout(tmp_path: pathlib.Path) -> pathlib.Path:
    workflows = tmp_path / "widget" / ".github" / "workflows"
    workflows.mkdir(parents=True)
    (workflows / "ci.yml").write_text(WORKFLOW, encoding="utf-8")
    return tmp_path / "widget"


def _aligned(cfg: PolicyConfig) -> FakeGitHubApi:
    """GitHub as the policy wants it: nothing to plan."""
    return FakeGitHubApi(
        {
            f"repos/{SLUG}": SETTINGS,
            f"repos/{SLUG}/rulesets": [{"id": 7, "name": RULESET_NAME, "enforcement": "active"}],
            f"repos/{SLUG}/rulesets/7": build_ruleset_payload(SLUG, cfg),
            f"repos/{SLUG}/labels?per_page=100": [
                {"name": label.name, "color": label.color, "description": label.description}
                for label in STANDARD_LABEL_TAXONOMY
            ],
        }
    )


def _plan(checkout: pathlib.Path, cfg: PolicyConfig, api: GitHubApi, *flags: str, slug: str = SLUG) -> tuple[int, str]:
    ports = PlanContext(api=api, slug_of=lambda _path: slug, config=lambda: cfg)
    result = CliRunner().invoke(app, ["plan", str(checkout), *flags], obj=ports)
    return result.exit_code, result.stdout


def _plans(stdout: str) -> list[RepoPlan]:
    return decode_json(list[RepoPlan], stdout)


def test_fake_is_the_port() -> None:
    api: GitHubApi = FakeGitHubApi()
    assert not api(ApiRequest("GET", "repos/acme/missing")).ok


def test_aligned_repo_plans_nothing_and_exits_0(tmp_path: pathlib.Path) -> None:
    cfg = _config(["lint"])
    api = _aligned(cfg)

    code, out = _plan(_checkout(tmp_path), cfg, api, "--json")

    assert code == 0
    assert _plans(out) == [RepoPlan(SLUG)]
    assert api.writes == []


def test_settings_drift_is_one_change_and_apply_sends_one_patch(tmp_path: pathlib.Path) -> None:
    cfg = _config(["lint"])
    api = _aligned(cfg)
    api.reads[f"repos/{SLUG}"] = {**SETTINGS, "allow_merge_commit": True, "delete_branch_on_merge": False}
    checkout = _checkout(tmp_path)

    code, out = _plan(checkout, cfg, api, "--json")

    assert code == 1
    [plan] = _plans(out)
    assert [(c.resource, c.action) for c in plan.changes] == [("repo-settings", "update")]
    assert api.writes == []

    code, _ = _plan(checkout, cfg, api, "--apply", "--json")

    assert code == 0
    [write] = api.writes
    assert (write.method, write.route) == ("PATCH", f"repos/{SLUG}")
    assert decode_json(RepoSettings, write.body or "") == RepoSettings()


def test_stale_required_check_is_refused_and_never_written(tmp_path: pathlib.Path) -> None:
    cfg = _config(["lint", "build"])  # no pull_request job reports `build`
    api = _aligned(cfg)
    del api.reads[f"repos/{SLUG}/rulesets/7"]
    api.reads[f"repos/{SLUG}/rulesets"] = []  # and the ruleset is missing, so a write would be wanted

    code, out = _plan(_checkout(tmp_path), cfg, api, "--apply", "--json")

    assert code == 1
    [plan] = _plans(out)
    [change] = plan.changes
    assert (change.resource, change.kind) == ("ruleset", "refused")
    assert "build" in change.detail
    assert api.writes == []


def test_missing_ruleset_is_created_on_apply(tmp_path: pathlib.Path) -> None:
    cfg = _config(["lint"])
    api = _aligned(cfg)
    api.reads[f"repos/{SLUG}/rulesets"] = []

    code, _ = _plan(_checkout(tmp_path), cfg, api, "--apply")

    assert code == 0
    [write] = api.writes
    assert (write.method, write.route) == ("POST", f"repos/{SLUG}/rulesets")


def test_unserved_rulesets_on_a_private_repo_are_a_note_not_drift(tmp_path: pathlib.Path) -> None:
    cfg = _config(["lint"])
    api = _aligned(cfg)
    del api.reads[f"repos/{SLUG}/rulesets"]
    api.failures[("GET", f"repos/{SLUG}/rulesets")] = "Upgrade to GitHub Pro or make this repository public"

    code, out = _plan(_checkout(tmp_path), cfg, api, "--json")

    assert code == 0
    [plan] = _plans(out)
    assert [(c.resource, c.kind) for c in plan.changes] == [("ruleset", "note")]


def test_unreadable_github_exits_2(tmp_path: pathlib.Path) -> None:
    cfg = _config(["lint"])
    api = _aligned(cfg)
    api.failures[("GET", f"repos/{SLUG}")] = "gh: Bad credentials (HTTP 401)"

    code, out = _plan(_checkout(tmp_path), cfg, api, "--json")

    assert code == 2
    [plan] = _plans(out)
    assert plan.errors and plan.errors[0].startswith("repo-settings:")


def test_wrong_owner_is_refused_before_github_is_read(tmp_path: pathlib.Path) -> None:
    cfg = _config(["lint"])
    api = _aligned(cfg)

    code, out = _plan(_checkout(tmp_path), cfg, api, "--apply", "--json", slug="stranger/widget")

    assert code == 1
    [plan] = _plans(out)
    assert [(c.resource, c.kind) for c in plan.changes] == [("owner", "refused")]
    assert api.calls == []


def test_label_and_pages_writes_go_through_the_port(tmp_path: pathlib.Path) -> None:
    cfg = _config(["lint"], pages={"enabled": True, "cname": "widget.example"})
    api = _aligned(cfg)
    labels = api.reads[f"repos/{SLUG}/labels?per_page=100"]
    assert isinstance(labels, list)
    api.reads[f"repos/{SLUG}/labels?per_page=100"] = labels[1:]

    code, _ = _plan(_checkout(tmp_path), cfg, api, "--apply")

    assert code == 0
    assert [(w.method, w.route) for w in api.writes] == [
        ("POST", f"repos/{SLUG}/labels"),
        ("POST", f"repos/{SLUG}/pages"),
        ("PUT", f"repos/{SLUG}/pages"),
    ]


@pytest.mark.parametrize(
    ("labels", "expected"),
    [
        ({}, [(label.name, "create") for label in STANDARD_LABEL_TAXONOMY]),
        ({label.name: RepoLabel(label.color.upper(), label.description) for label in STANDARD_LABEL_TAXONOMY}, []),
        (
            {
                **{label.name: RepoLabel(label.color, label.description) for label in STANDARD_LABEL_TAXONOMY},
                "type: bug": RepoLabel("000000", "old"),
            },
            [("type: bug", "update")],
        ),
    ],
    ids=["none", "case-insensitive colour", "one stale"],
)
def test_label_differences(labels: dict[str, RepoLabel], expected: list[tuple[str, str]]) -> None:
    diffs = label_differences(STANDARD_LABEL_TAXONOMY, labels)
    assert [(d.label_name, d.action) for d in diffs] == expected


def test_label_update_url_encodes_the_name(tmp_path: pathlib.Path) -> None:
    cfg = _config(["lint"])
    api = _aligned(cfg)
    labels = api.reads[f"repos/{SLUG}/labels?per_page=100"]
    assert isinstance(labels, list)
    api.reads[f"repos/{SLUG}/labels?per_page=100"] = [
        {"name": "good first issue", "color": "000000", "description": ""},
        *labels[:8],
        *labels[9:],
    ]

    _plan(_checkout(tmp_path), cfg, api, "--apply")

    [write] = api.writes
    assert (write.method, write.route) == ("PATCH", f"repos/{SLUG}/labels/good%20first%20issue")


@pytest.mark.parametrize(
    ("site", "expected"),
    [
        (None, ["Pages is disabled"]),
        ({"build_type": "workflow", "cname": "widget.example", "https_enforced": True}, []),
        (
            {"build_type": "legacy", "cname": "old.example", "https_enforced": False},
            [
                "build_type is 'legacy', expected 'workflow'",
                "cname is 'old.example', expected 'widget.example'",
                "https_enforced is False, expected True",
            ],
        ),
    ],
    ids=["off", "aligned", "everything differs"],
)
def test_pages_drift(site: dict[str, object] | None, expected: list[str]) -> None:
    api = FakeGitHubApi({} if site is None else {f"repos/{SLUG}/pages": site})
    policy = PagesPolicy(enabled=True, cname="widget.example")
    assert pages_drift(policy, read_pages(api, SLUG)) == expected


def test_environments_apply_what_is_declared_and_note_what_is_not(tmp_path: pathlib.Path) -> None:
    cfg = _config(["lint"], environments={"release": {"wait_timer": 5, "reviewers": ["octo"]}})
    api = _aligned(cfg)
    api.reads[f"repos/{SLUG}/environments"] = {"environments": [{"name": "release"}, {"name": "github-pages"}]}
    api.reads[f"repos/{SLUG}/environments/release/deploy_reviewers"] = {"reviewers": []}
    api.reads["users/octo"] = {"id": 42}
    checkout = _checkout(tmp_path)

    code, out = _plan(checkout, cfg, api, "--json")

    assert code == 1
    [plan] = _plans(out)
    assert [(c.action, c.subject, c.kind) for c in plan.changes] == [
        ("wait-timer", "release", "apply"),
        ("add-reviewers", "release", "apply"),
        ("undeclared", "github-pages", "note"),
    ]

    code, _ = _plan(checkout, cfg, api, "--apply")

    assert code == 0
    assert [(w.method, w.route, w.body) for w in api.writes] == [
        ("PUT", f"repos/{SLUG}/environments/release", '{"wait_timer":5}'),
        (
            "PATCH",
            f"repos/{SLUG}/environments/release/deploy_reviewers",
            '{"reviewers":[{"id":42,"reviewer_type":"User"}]}',
        ),
    ]


def test_a_failed_write_exits_2(tmp_path: pathlib.Path) -> None:
    cfg = _config(["lint"])
    api = _aligned(cfg)
    api.reads[f"repos/{SLUG}/rulesets"] = []
    api.failures[("POST", f"repos/{SLUG}/rulesets")] = "gh: Validation Failed (HTTP 422)"

    code, out = _plan(_checkout(tmp_path), cfg, api, "--apply")

    assert code == 2
    assert "Validation Failed" in out

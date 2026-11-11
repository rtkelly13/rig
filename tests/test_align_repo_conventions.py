import json
import pathlib
import subprocess
import textwrap
from unittest.mock import patch

import pytest
from typer.testing import CliRunner

from rig.repo_governance.align_repo_conventions import (
    align_github_repo_settings,
    app,
    build_ruleset_payload,
    get_gh_repo_name,
    inspect_check_names,
    inspect_repo_exists,
    inspect_repo_settings,
)
from rig.repo_governance.check_names import (
    discover_pr_check_names,
    lint_check_name,
    suggest_preferred_name,
)
from rig.repo_governance.github_rulesets import (
    RULESET_NAME,
    align_github_ruleset,
    registered_required_checks,
    ruleset_drift,
)
from rig.repo_governance.policy_config import PolicyConfig, load_policy_config
from rig.typed_json import load_json
from tests.fakes.github_rest import FakeGitHubApi

runner = CliRunner()

REPO_ROOT = pathlib.Path(__file__).resolve().parents[1]


def _requiring(repo: str, checks: list[str]):
    """Declare *checks* as *repo*'s required checks, the way config/repo_policies.json would."""
    cfg = PolicyConfig.parse({"repo_overrides": {repo.rsplit("/", 1)[-1]: {"required_checks": checks}}})
    return patch("rig.repo_governance.github_rulesets.load_policy_config", return_value=cfg)


def _done(stdout: str = "") -> subprocess.CompletedProcess[str]:
    """A command that exited 0 and printed *stdout*."""
    return subprocess.CompletedProcess([], 0, stdout, "")


def _failed(stderr: str) -> subprocess.CompletedProcess[str]:
    """A command that exited 1 and printed *stderr*."""
    return subprocess.CompletedProcess([], 1, "", stderr)


def test_inspect_repo_exists_success(tmp_path: pathlib.Path):
    (tmp_path / ".git").mkdir()
    with (
        patch(
            "rig.repo_governance.repo_discovery.get_gh_repo_name",
            return_value="rtkelly13/test-repo",
        ),
        patch("subprocess.run") as mock_run,
    ):
        mock_run.return_value = _done("rtkelly13/test-repo\n")
        res = inspect_repo_exists(tmp_path)
        assert res.exists is True
        assert res.status == "EXISTS"
        assert res.repo_name == "rtkelly13/test-repo"


def test_inspect_repo_exists_missing_local(tmp_path: pathlib.Path):
    res = inspect_repo_exists(tmp_path)
    assert res.exists is False
    assert res.status == "MISSING_LOCAL"


def test_inspect_repo_exists_not_linked(tmp_path: pathlib.Path):
    (tmp_path / ".git").mkdir()
    with patch("rig.repo_governance.repo_discovery.get_gh_repo_name", return_value=None):
        res = inspect_repo_exists(tmp_path)
        assert res.exists is False
        assert res.status == "NOT_LINKED_TO_GITHUB"


def test_inspect_repo_exists_not_found_on_github(tmp_path: pathlib.Path):
    (tmp_path / ".git").mkdir()
    err = _failed("Could not resolve")
    with (
        patch(
            "rig.repo_governance.repo_discovery.get_gh_repo_name",
            return_value="rtkelly13/nonexistent",
        ),
        patch("subprocess.run", return_value=err),
    ):
        res = inspect_repo_exists(tmp_path)
        assert res.exists is False
        assert res.status == "NOT_FOUND_ON_GITHUB"


def test_get_gh_repo_name_success(tmp_path: pathlib.Path):
    with patch("subprocess.run") as mock_run:
        mock_run.return_value = _done("rtkelly13/design-system\n")
        repo_name = get_gh_repo_name(tmp_path)
        assert repo_name == "rtkelly13/design-system"


def test_get_gh_repo_name_failure(tmp_path: pathlib.Path):
    with patch("subprocess.run", side_effect=FileNotFoundError("gh command failed")):
        assert get_gh_repo_name(tmp_path) is None


_SETTINGS_REPO = "rtkelly13/design-system"
_ALIGNED_SETTINGS = {
    "default_branch": "main",
    "delete_branch_on_merge": True,
    "allow_squash_merge": True,
    "allow_merge_commit": False,
    "allow_rebase_merge": False,
    "allow_auto_merge": True,
}


def _settings(**overrides: object) -> FakeGitHubApi:
    return FakeGitHubApi({f"repos/{_SETTINGS_REPO}": {**_ALIGNED_SETTINGS, **overrides}})


def test_inspect_repo_settings_aligned():
    res = inspect_repo_settings(_SETTINGS_REPO, api=_settings(), cfg=PolicyConfig.parse({}))
    assert res.is_aligned is True
    assert res.delete_on_merge is True
    assert res.squash_only is True


def test_inspect_repo_settings_drifted():
    api = _settings(delete_branch_on_merge=False, allow_merge_commit=True)
    res = inspect_repo_settings(_SETTINGS_REPO, api=api, cfg=PolicyConfig.parse({}))
    assert res.is_aligned is False


def test_inspect_repo_settings_base_branch_aligned():
    res = inspect_repo_settings(_SETTINGS_REPO, expected_branch="main", api=_settings(), cfg=PolicyConfig.parse({}))
    assert res.is_aligned is True
    assert res.base_branch_aligned is True
    assert res.default_branch == "main"
    assert res.expected_branch == "main"


def test_inspect_repo_settings_base_branch_deviation():
    api = _settings(default_branch="master")
    res = inspect_repo_settings(_SETTINGS_REPO, expected_branch="main", api=api, cfg=PolicyConfig.parse({}))
    assert res.is_aligned is False
    assert res.base_branch_aligned is False
    assert res.default_branch == "master"
    assert res.expected_branch == "main"


def test_align_github_repo_settings_execution():
    cfg = PolicyConfig.parse({})
    api = _settings(default_branch="master", allow_merge_commit=True)
    res = align_github_repo_settings(_SETTINGS_REPO, dry_run=False, api=api, cfg=cfg)
    assert res.status == "success"
    [write] = api.writes
    assert (write.method, write.route) == ("PATCH", f"repos/{_SETTINGS_REPO}")
    assert inspect_repo_settings(_SETTINGS_REPO, api=api, cfg=cfg).is_aligned


def test_align_github_repo_settings_dry_run():
    res = align_github_repo_settings("rtkelly13/design-system", dry_run=True)
    assert res.status == "dry_run"
    assert res.command.startswith("PATCH repos/rtkelly13/design-system ")
    assert '"default_branch":"main"' in res.command

    res_custom = align_github_repo_settings("rtkelly13/design-system", expected_branch="develop", dry_run=True)
    assert '"default_branch":"develop"' in res_custom.command


_RESOLVED_THREADS = {
    "data": {
        "repository": {
            "pullRequest": {
                "reviewThreads": {
                    "pageInfo": {"hasNextPage": False, "endCursor": None},
                    "nodes": [{"id": "t1", "isResolved": True, "comments": {"nodes": []}}],
                }
            }
        }
    }
}


def _gh_for_pr(checks: subprocess.CompletedProcess[str], merge_state: str = "CLEAN") -> tuple[list[list[str]], object]:
    """A `gh` that answers `pr checks` with *checks*, `pr view` with *merge_state*, and resolved threads."""
    calls: list[list[str]] = []

    def run(cmd: list[str], **_: object) -> subprocess.CompletedProcess[str]:
        calls.append(cmd)
        if "checks" in cmd:
            return checks
        if "view" in cmd:
            view = {"number": 123, "mergeStateStatus": merge_state}
            return subprocess.CompletedProcess(cmd, 0, json.dumps(view), "")
        return subprocess.CompletedProcess(cmd, 0, json.dumps(_RESOLVED_THREADS), "")

    return calls, run


def _checks(code: int, out: str = "", err: str = "") -> subprocess.CompletedProcess[str]:
    return subprocess.CompletedProcess(["gh"], code, out, err)


def test_verify_pr_checks_passes_when_required_checks_pass_and_github_can_merge():
    calls, run = _gh_for_pr(_checks(0, "ci\tpass\t1m\n"))
    with patch("subprocess.run", side_effect=run):
        result = runner.invoke(app, ["verify-pr-checks", "123", "--repo", "rtkelly13/blog"])
    assert result.exit_code == 0, result.output
    assert "every conversation thread is resolved" in " ".join(result.output.split())
    checks_cmd = next(c for c in calls if "checks" in c)
    assert checks_cmd[-3:] == ["--repo", "rtkelly13/blog", "--required"]


@pytest.mark.parametrize(
    ("checks", "expected"),
    [
        (_checks(1, "ci\tfail\t1m\n"), "not passing"),
        (_checks(8, "ci\tpending\t0\n"), "still pending"),
    ],
)
def test_verify_pr_checks_blocks_on_a_required_check_that_has_not_passed(checks, expected):
    _, run = _gh_for_pr(checks)
    with patch("subprocess.run", side_effect=run):
        result = runner.invoke(app, ["verify-pr-checks", "123", "--repo", "rtkelly13/blog"])
    assert result.exit_code == 1
    assert "BLOCKED" in result.output
    assert expected in result.output
    # gh's own table is shown, so the reader sees which check.
    assert result.output.splitlines()[-1].split()[0] == "ci"


def test_verify_pr_checks_with_no_required_checks_falls_through_to_the_merge_state():
    no_required = _checks(1, err="no required checks reported on the 'feature' branch")
    _, run = _gh_for_pr(no_required)
    with patch("subprocess.run", side_effect=run):
        result = runner.invoke(app, ["verify-pr-checks", "123", "--repo", "rtkelly13/blog"])
    assert result.exit_code == 0, result.output


@pytest.mark.parametrize("state", ["BLOCKED", "BEHIND", "DIRTY", "DRAFT", "UNKNOWN"])
def test_verify_pr_checks_blocks_when_github_would_not_merge(state):
    _, run = _gh_for_pr(_checks(0), merge_state=state)
    with patch("subprocess.run", side_effect=run):
        result = runner.invoke(app, ["verify-pr-checks", "123", "--repo", "rtkelly13/blog"])
    assert result.exit_code == 1
    assert f"mergeStateStatus is {state}" in result.output


def test_verify_pr_checks_blocks_when_the_pr_cannot_be_read():
    def run(cmd: list[str], **_: object) -> subprocess.CompletedProcess[str]:
        if "view" in cmd:
            return subprocess.CompletedProcess(cmd, 1, "", "no pull requests found")
        return subprocess.CompletedProcess(cmd, 0, "", "")

    with patch("subprocess.run", side_effect=run):
        result = runner.invoke(app, ["verify-pr-checks", "123", "--repo", "rtkelly13/blog"])
    assert result.exit_code == 1
    assert "could not read PR" in result.output


# --- Required check names: shape, lexicon and drift ------------------------------------------


class TestCheckNameShape:
    """The lexicon rule: lowercase snake_case, at most two words, preferably a listed name."""

    @pytest.mark.ci
    @pytest.mark.parametrize(
        "name",
        [
            "ci",
            "build",
            "test",
            "tests",
            "lint",
            "visual",
            "pr_title",
            "pr-checks",
            "pr_checks",
            "benchmark",
            "deploy",
            "action_shas",
        ],
    )
    def test_lexicon_names_produce_no_warnings(self, name):
        assert lint_check_name(name) == []

    @pytest.mark.ci
    def test_uppercase_is_flagged(self):
        assert "not all lowercase" in lint_check_name("Build")

    @pytest.mark.ci
    @pytest.mark.parametrize("name", ["Lint & Typecheck", "PR checks", "unit-tests", "Lint (biome)"])
    def test_non_snake_case_separators_are_flagged(self, name):
        assert any("not snake_case" in w for w in lint_check_name(name))

    @pytest.mark.ci
    def test_more_than_two_words_is_flagged(self):
        warnings = lint_check_name("build_and_test_all")
        assert any("4 words, at most 2 allowed" in w for w in warnings)

    @pytest.mark.ci
    def test_two_words_is_allowed(self):
        assert not any("words, at most" in w for w in lint_check_name("api_snapshot"))

    @pytest.mark.ci
    def test_the_name_that_actually_drifted_is_flagged(self):
        """The regression this rule exists for — design-system's old required check."""
        warnings = lint_check_name("Build, Typecheck, Unit Tests, Storybook & Visual Regression")
        assert any("not all lowercase" in w for w in warnings)
        assert any("7 words" in w for w in warnings)

    @pytest.mark.ci
    def test_off_lexicon_but_well_shaped_name_warns_without_shape_complaints(self):
        warnings = lint_check_name("wibble")
        assert warnings == ["not in the preferred lexicon (allowed, but prefer a listed name)"]

    @pytest.mark.ci
    @pytest.mark.parametrize(
        ("name", "expected"),
        [
            ("Build", "build"),
            ("Lint & Typecheck", "lint"),
            ("Storybook & Visual Regression", "visual"),
            ("Tests & Coverage", "coverage"),
            ("PR Title", "pr_title"),
        ],
    )
    def test_suggestions_point_at_the_lexicon(self, name, expected):
        assert suggest_preferred_name(name) == expected

    @pytest.mark.ci
    def test_no_suggestion_for_an_unrelated_name(self):
        assert suggest_preferred_name("wibble") is None


class TestDiscoverPrCheckNames:
    """The check name GitHub reports is a job's `name:`, falling back to its job id."""

    @staticmethod
    def _write(tmp_path, filename, body):
        workflows = tmp_path / ".github" / "workflows"
        workflows.mkdir(parents=True, exist_ok=True)
        (workflows / filename).write_text(textwrap.dedent(body))
        return tmp_path

    @pytest.mark.ci
    def test_job_name_wins_and_job_id_is_the_fallback(self, tmp_path):
        repo = self._write(
            tmp_path,
            "ci.yml",
            """
            name: CI
            on:
              pull_request:
                branches: [main]
            jobs:
              verify:
                name: ci
                runs-on: ubuntu-latest
              unnamed:
                runs-on: ubuntu-latest
            """,
        )
        found = discover_pr_check_names(repo)
        assert set(found) == {"ci", "unnamed"}
        assert found["ci"] == "ci.yml :: verify"

    @pytest.mark.ci
    def test_on_is_read_despite_yaml_parsing_it_as_the_boolean_true(self, tmp_path):
        """`on:` is a YAML 1.1 boolean, so safe_load keys it as True, not "on"."""
        repo = self._write(
            tmp_path,
            "ci.yml",
            """
            on: [pull_request]
            jobs:
              verify:
                name: ci
            """,
        )
        assert "ci" in discover_pr_check_names(repo)

    @pytest.mark.ci
    def test_numeric_job_ids_do_not_hide_the_other_jobs(self, tmp_path):
        repo = self._write(
            tmp_path,
            "ci.yml",
            """
            on: [pull_request]
            jobs:
              1:
                runs-on: ubuntu-latest
              verify:
                name: ci
            """,
        )
        assert discover_pr_check_names(repo) == {"1": "ci.yml :: 1", "ci": "ci.yml :: verify"}

    @pytest.mark.ci
    def test_a_declared_status_context_counts_as_a_check(self, tmp_path):
        """A gate held at pending is a commit status, not a job; `STATUS_CONTEXT` names it."""
        repo = self._write(
            tmp_path,
            "regression.yml",
            """
            on:
              pull_request:
                types: [opened, synchronize, labeled]
            env:
              STATUS_CONTEXT: regression
            jobs:
              gate:
                name: gate
            """,
        )
        assert discover_pr_check_names(repo) == {
            "gate": "regression.yml :: gate",
            "regression": "regression.yml :: status",
        }

    @pytest.mark.ci
    def test_a_status_context_outside_a_pull_request_workflow_is_ignored(self, tmp_path):
        repo = self._write(
            tmp_path,
            "nightly.yml",
            """
            on: [push]
            env:
              STATUS_CONTEXT: regression
            jobs:
              run:
                name: run
            """,
        )
        assert discover_pr_check_names(repo) == {}

    @pytest.mark.ci
    def test_workflows_without_a_pull_request_trigger_are_ignored(self, tmp_path):
        """A job that never runs on a PR can never satisfy a required check on one."""
        repo = self._write(
            tmp_path,
            "publish.yml",
            """
            on:
              push:
                branches: [main]
            jobs:
              publish:
                name: publish
            """,
        )
        assert discover_pr_check_names(repo) == {}

    @pytest.mark.ci
    def test_unparseable_workflow_is_skipped_rather_than_raising(self, tmp_path):
        repo = self._write(tmp_path, "broken.yml", "this: [is: not: valid: yaml")
        self._write(
            repo,
            "ci.yml",
            """
            on: [pull_request]
            jobs:
              verify:
                name: ci
            """,
        )
        assert set(discover_pr_check_names(repo)) == {"ci"}

    @pytest.mark.ci
    def test_repo_without_a_workflows_directory_is_empty(self, tmp_path):
        assert discover_pr_check_names(tmp_path) == {}


class TestInspectCheckNames:
    """The drift half: a required check no PR job declares blocks every PR in that repo."""

    @staticmethod
    def _repo(tmp_path, *job_names):
        workflows = tmp_path / ".github" / "workflows"
        workflows.mkdir(parents=True, exist_ok=True)
        jobs = "".join(f"  job{i}:\n    name: {name}\n" for i, name in enumerate(job_names))
        (workflows / "ci.yml").write_text(f"on: [pull_request]\njobs:\n{jobs}")
        return tmp_path

    @pytest.mark.ci
    def test_declared_and_well_named_is_aligned(self, tmp_path):
        repo = self._repo(tmp_path, "ci")
        with _requiring("o/r", ["ci"]):
            res = inspect_check_names(repo, "o/r")
        assert res.is_aligned is True
        assert res.status == "aligned"
        assert res.missing == ()
        assert res.warnings == {}

    @pytest.mark.ci
    def test_a_required_check_no_job_declares_is_drift(self, tmp_path):
        """The exact design-system failure: the job was renamed, the map was not."""
        repo = self._repo(tmp_path, "Build, Typecheck, Unit Tests, Storybook & Visual Regression")
        with _requiring("o/r", ["Build, Typecheck, Storybook & Visual Regression"]):
            res = inspect_check_names(repo, "o/r")
        assert res.is_aligned is False
        assert res.status == "drift"
        assert res.missing == ("Build, Typecheck, Storybook & Visual Regression",)

    @pytest.mark.ci
    def test_a_badly_shaped_name_warns_but_stays_aligned(self, tmp_path):
        """Shape is advisory — most of these repos predate the rule."""
        repo = self._repo(tmp_path, "Lint & Typecheck")
        with _requiring("o/r", ["Lint & Typecheck"]):
            res = inspect_check_names(repo, "o/r")
        assert res.is_aligned is True
        assert res.status == "warn"
        assert "Lint & Typecheck" in res.warnings

    @pytest.mark.ci
    def test_no_pr_workflows_is_not_called_drift(self, tmp_path):
        with _requiring("o/r", ["ci"]):
            res = inspect_check_names(tmp_path, "o/r")
        assert res.status == "no_pr_workflows"
        assert res.is_aligned is True

    @pytest.mark.ci
    def test_an_unregistered_repo_has_no_borrowed_checks(self, tmp_path):
        """How Resultful and Parquet.TypeProvider went unnoticed: they matched no key at all."""
        repo = self._repo(tmp_path, "build", "pr-title")
        res = inspect_check_names(repo, "owner/never-registered")
        assert res.registered is False
        assert res.is_aligned is True
        assert res.required == ()
        assert res.missing == ()


class TestRequiredChecksConfigMatchesReality:
    """`required_checks` in config/repo_policies.json is a claim about what the workflows declare."""

    @staticmethod
    def _declared_lists() -> list[tuple[str, tuple[str, ...]]]:
        policy = load_policy_config().file
        layers = [("defaults", policy.defaults)]
        layers += [(f"profile {name}", profile) for name, profile in policy.profiles.items()]
        layers += [(f"override {name}", layer) for name, layer in policy.repo_overrides.items()]
        return [(where, layer.required_checks) for where, layer in layers if layer.required_checks is not None]

    @pytest.mark.ci
    def test_rig_requires_checks_its_own_workflows_declare(self):
        """This repo's ruleset is resolved from config, and every name is a real pull_request job."""
        required = registered_required_checks("rtkelly13/rig")
        declared = discover_pr_check_names(REPO_ROOT)
        assert required == ["ci"]
        assert set(required) <= set(declared), (required, sorted(declared))

    @pytest.mark.ci
    def test_design_system_requires_the_aggregate_and_the_regression_gate(self):
        assert registered_required_checks("rtkelly-labs/design-system") == ["ci", "regression"]

    @pytest.mark.ci
    def test_no_list_requires_the_same_check_twice(self):
        for where, checks in self._declared_lists():
            assert len(checks) == len(set(checks)), where

    @pytest.mark.ci
    def test_no_list_is_empty(self):
        """A ruleset with no required checks is a ruleset that gates nothing."""
        for where, checks in self._declared_lists():
            assert checks, where


class TestRulesetPayloadDeclaresEveryPullRequestParameter:
    """GitHub defaults any omitted rule parameter, so an undeclared one is reset on every enforce."""

    @staticmethod
    def _pr_params(repo_name="rtkelly13/design-system"):
        payload = build_ruleset_payload(repo_name)
        rule = next(r for r in payload["rules"] if r["type"] == "pull_request")
        return rule["parameters"]

    @pytest.mark.ci
    def test_extra_approval_for_unattributed_changes_is_declared_false(self):
        """Left undeclared, this made design-system#69 unmergeable by anyone with all checks green.

        `Co-Authored-By` names a non-GitHub address, so the commit reads as having an unattributed
        co-author and demands an extra approving review that no one can supply — the review count
        is 0, the author cannot self-approve, and there are no bypass actors.
        """
        params = self._pr_params()
        assert "require_extra_approval_for_unattributed_changes" in params
        assert params["require_extra_approval_for_unattributed_changes"] is False

    @pytest.mark.ci
    def test_no_pull_request_parameter_is_left_to_a_github_default(self):
        """A guard against re-introducing the same class of bug with a different parameter."""
        expected = {
            "required_approving_review_count",
            "dismiss_stale_reviews_on_push",
            "require_code_owner_review",
            "require_last_push_approval",
            "required_review_thread_resolution",
            "require_extra_approval_for_unattributed_changes",
        }
        assert set(self._pr_params()) == expected

    @pytest.mark.ci
    def test_required_checks_in_the_payload_come_from_the_config(self):
        payload = build_ruleset_payload("rtkelly13/design-system")
        rule = next(r for r in payload["rules"] if r["type"] == "required_status_checks")
        assert rule["parameters"]["required_status_checks"] == [
            {"context": c} for c in registered_required_checks("rtkelly13/design-system")
        ]


class TestRulesetForManagedForks:
    """A fork's default branch is rebuilt by its stack tool, so the ruleset must allow that push."""

    FORK = "rtkelly-labs/parquet-dotnet"

    @staticmethod
    def _rule_types(repo_name: str) -> list[str]:
        return [r["type"] for r in build_ruleset_payload(repo_name)["rules"]]

    @pytest.mark.ci
    def test_a_fork_ruleset_covers_its_own_default_branch(self):
        payload = build_ruleset_payload(self.FORK)
        assert payload["conditions"]["ref_name"]["include"] == ["refs/heads/master"]

    @pytest.mark.ci
    def test_a_fork_allows_rebaseline_pushes_but_keeps_history_and_checks(self):
        types = self._rule_types(self.FORK)
        assert "non_fast_forward" not in types
        assert "pull_request" not in types
        assert {"deletion", "required_linear_history", "required_status_checks"} <= set(types)

    @pytest.mark.ci
    def test_an_owned_repo_keeps_the_full_ruleset_on_main(self):
        payload = build_ruleset_payload("rtkelly13/design-system")
        assert payload["conditions"]["ref_name"]["include"] == ["refs/heads/main"]
        assert {"non_fast_forward", "pull_request"} <= set(self._rule_types("rtkelly13/design-system"))

    @pytest.mark.ci
    def test_editing_one_payload_does_not_change_the_next(self):
        first = build_ruleset_payload("rtkelly13/design-system")
        rule = next(r for r in first["rules"] if r["type"] == "pull_request")
        rule["parameters"]["allowed_merge_methods"] = ["squash"]
        second = build_ruleset_payload("rtkelly13/design-system")
        again = next(r for r in second["rules"] if r["type"] == "pull_request")
        assert "allowed_merge_methods" not in again["parameters"]


class TestRulesetDrift:
    """An active ruleset is compared with the declared one, not assumed current because it exists."""

    REPO = "rtkelly-labs/design-system"

    def _live(self, drop: str | None = None, extra_approval: bool = False) -> str:
        payload = build_ruleset_payload(self.REPO)
        rules = [r for r in payload["rules"] if r["type"] != drop]
        for r in rules:
            if r["type"] == "pull_request":
                r["parameters"]["require_extra_approval_for_unattributed_changes"] = extra_approval
                # GitHub returns parameters the payload never sets; they must not read as drift.
                r["parameters"]["allowed_merge_methods"] = ["squash"]
            if r["type"] == "required_status_checks":
                for check in r["parameters"]["required_status_checks"]:
                    check["integration_id"] = 15368
        payload["rules"] = rules
        return json.dumps(payload)

    def _github(self, detail: str) -> FakeGitHubApi:
        return FakeGitHubApi(
            {
                f"repos/{self.REPO}/rulesets": [{"id": 7, "name": RULESET_NAME, "enforcement": "active"}],
                f"repos/{self.REPO}/rulesets/7": load_json(detail),
            }
        )

    @pytest.mark.ci
    def test_a_ruleset_matching_the_declaration_has_no_drift(self):
        assert ruleset_drift(self.REPO, self._live()) == []

    @pytest.mark.ci
    def test_a_missing_rule_and_a_changed_parameter_are_both_drift(self):
        drift = ruleset_drift(self.REPO, self._live(drop="required_status_checks", extra_approval=True))
        assert "missing rule required_status_checks" in drift
        assert any(line.startswith("pull_request:") for line in drift)

    @pytest.mark.ci
    def test_align_updates_an_active_ruleset_that_drifted(self):
        api = self._github(self._live(drop="required_linear_history"))
        res = align_github_ruleset(self.REPO, dry_run=True, api=api)
        assert res.status == "dry_run"
        assert res.action == "PUT"
        assert res.drift == ("missing rule required_linear_history",)

    @pytest.mark.ci
    def test_align_leaves_an_active_ruleset_that_matches(self):
        api = self._github(self._live())
        res = align_github_ruleset(self.REPO, api=api)
        assert res.status == "already_active"
        assert api.writes == []


def _policy_in_cwd(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch, text: str) -> None:
    """`load_policy_config` reads `config/repo_policies.json` under the working directory first."""
    (tmp_path / "config").mkdir()
    (tmp_path / "config" / "repo_policies.json").write_text(text, encoding="utf-8")
    monkeypatch.chdir(tmp_path)


def test_sync_policies_rejects_a_misshapen_config(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch):
    _policy_in_cwd(tmp_path, monkeypatch, json.dumps({"profiles": ["not", "a", "mapping"]}))
    result = runner.invoke(app, ["sync-policies"])
    assert result.exit_code == 1
    assert "Invalid policy config" in result.output


def test_sync_policies_walks_declared_profiles(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch):
    policy = {"profiles": {"public_open_source": {"repos": ["absent-repo"], "policies": ["ai_policy"]}}}
    _policy_in_cwd(tmp_path, monkeypatch, json.dumps(policy))
    result = runner.invoke(app, ["sync-policies", "--dry-run"])
    assert result.exit_code == 0, result.output
    assert "Skipping absent-repo" in result.output


def test_verify_pr_checks_without_a_resolvable_repo_asks_for_one():
    with (
        patch("rig.repo_governance.cli_triage.get_gh_repo_name", return_value=None),
        patch("subprocess.run") as mock_run,
    ):
        result = runner.invoke(app, ["verify-pr-checks", "123"])
    assert result.exit_code == 1
    assert "pass --repo" in result.output
    mock_run.assert_not_called()

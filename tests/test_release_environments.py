"""Tests for deployment-environment policy and the planner that reads it.

The planner is pure, so it is tested directly; only the GitHub reads/writes are
left out. The entitlement case matters most: GitHub serves protection rules
conditionally, and collapsing "cannot tell" into "no reviewers" would make the
planner re-add reviewers forever and then silently no-op the failure away.
"""

from __future__ import annotations

import json

from rig.repo_governance.policy_config import EnvironmentPolicy, PolicyConfig, config_path
from rig.repo_governance.release_environments import (
    Action,
    CurrentEnvironment,
    plan_environment_actions,
)

RELEASE = EnvironmentPolicy(name="release", wait_timer=0, reviewers=("rtkelly13",))

RAW_CONFIG = {
    "defaults": {},
    "profiles": {
        "public_open_source": {
            "environments": {
                "release": {"wait_timer": 0, "reviewers": ["rtkelly13"]},
            },
            "repos": ["SomeRepo"],
        },
        "private_workspace": {"repos": ["OtherRepo"]},
    },
}


def _policy(raw_config: dict[str, object] = RAW_CONFIG) -> PolicyConfig:
    return PolicyConfig.parse(raw_config)


class TestPolicyConfigEnvironments:
    def test_profile_environment_is_parsed(self):
        envs = _policy().environments("rtkelly13/SomeRepo")
        assert envs["release"].reviewers == ("rtkelly13",)

    def test_unregistered_repo_declares_nothing(self):
        assert _policy().environments("Unregistered") == {}

    def test_profile_without_environments_declares_nothing(self):
        assert _policy().environments("OtherRepo") == {}

    def test_shipped_config_declares_release_for_source_generator(self):
        cfg = PolicyConfig.parse(json.loads(config_path().read_text(encoding="utf-8")))
        envs = cfg.environments("Parquet.SourceGenerator")
        assert "release" in envs
        # Deliberately no reviewers: sole-maintainer repos cannot self-approve, and
        # protection rules are org-only — the config declares what is actually settable.
        assert envs["release"].reviewers == ()


class TestPlanner:
    def test_missing_environment_is_created(self):
        actions = plan_environment_actions({"release": RELEASE}, {})
        assert actions == [Action("create", "release", "wait_timer=0")]

    def test_existing_environment_is_left_alone(self):
        current = {"release": CurrentEnvironment("release", wait_timer=0, reviewers=("rtkelly13",))}
        assert plan_environment_actions({"release": RELEASE}, current) == []

    def test_wait_timer_drift_is_corrected(self):
        current = {"release": CurrentEnvironment("release", wait_timer=0, reviewers=("rtkelly13",))}
        desired = {
            "release": EnvironmentPolicy(name="release", wait_timer=30, reviewers=("rtkelly13",)),
        }
        actions = plan_environment_actions(desired, current)
        assert [a.kind for a in actions] == ["wait-timer"]

    def test_missing_reviewer_is_added(self):
        current = {"release": CurrentEnvironment("release", reviewers=())}
        actions = plan_environment_actions({"release": RELEASE}, current)
        assert [a.kind for a in actions] == ["add-reviewers"]
        assert actions[0].detail == "rtkelly13"

    def test_unserved_protection_rules_report_rather_than_retry(self):
        current = {"release": CurrentEnvironment("release", reviewers=None)}
        actions = plan_environment_actions({"release": RELEASE}, current)
        assert [a.kind for a in actions] == ["reviewers-unavailable"]
        assert not any(a.mutates for a in actions)

    def test_undeclared_environment_is_reported_not_deleted(self):
        current = {"staging": CurrentEnvironment("staging")}
        actions = plan_environment_actions({}, current)
        assert [a.kind for a in actions] == ["undeclared"]
        assert not any(a.mutates for a in actions)

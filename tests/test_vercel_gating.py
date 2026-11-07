"""Tests for the Vercel deployment-creation audit.

The planner is pure — every rule is exercised by handing it facts, exactly the shape
`audit_projects` assembles from the Vercel API. The 2026-09-11 incident is the fixture
that matters most: 83 deployments created in a day, 79 canceled by `ignoreCommand`,
all of them spending the 100/day budget. A rule that only checked the project flag
would have missed it had the flag been lying; the empirical rule catches it whatever
the flag claims.
"""

from __future__ import annotations

import json

import pytest

from rig.repo_governance import vercel_gating
from rig.repo_governance.policy_config import PolicyConfig, config_path
from rig.repo_governance.vercel_gating import (
    CreatedDeployment,
    ProjectFacts,
    plan_project_findings,
    ref_is_allowed,
)

NOW = 1_789_160_000_000  # 2026-09-11T21:33Z, the day of the incident
DAY = 86_400_000


def _facts(
    *,
    create: str | None = "enabled",
    allow: tuple[str, ...] | None = None,
    deny: tuple[str, ...] | None = None,
    deployments: tuple[CreatedDeployment, ...] = (),
    head_sha: str | None = None,
    name: str = "design-system",
) -> ProjectFacts:
    return ProjectFacts(
        name=name,
        repo="design-system",
        create_deployments=create,
        production_ref="main",
        allow=allow,
        deny=deny,
        recent_deployments=deployments,
        head_sha=head_sha,
    )


def _dep(
    ref: str,
    *,
    target: str | None = None,
    state: str = "CANCELED",
    age_days: float = 0.0,
    sha: str | None = None,
) -> CreatedDeployment:
    return CreatedDeployment(
        ref=ref,
        target=target,
        state=state,
        created_at_ms=NOW - int(age_days * DAY),
        commit_sha=sha,
    )


def _rules(findings):
    return {f.rule for f in findings}


class TestCreationGate:
    def test_enabled_without_a_repo_allowlist_is_an_error(self):
        findings = plan_project_findings(_facts(create="enabled"), now_ms=NOW, window_days=3)
        assert "creation-gate" in _rules(findings)

    def test_enabled_with_a_deny_all_allowlist_is_clean(self):
        facts = _facts(create="enabled", allow=("main", "preview", "slot/*"), deny=("**",))
        findings = plan_project_findings(facts, now_ms=NOW, window_days=3)
        assert findings == []

    def test_allowlist_without_a_deny_all_is_only_a_warning(self):
        facts = _facts(create="disabled", allow=("main", "preview"), deny=())
        findings = plan_project_findings(facts, now_ms=NOW, window_days=3)
        assert _rules(findings) == {"allowlist-has-no-deny-all"}
        assert all(f.severity == "warn" for f in findings)

    def test_legacy_absent_flag_is_not_blamed_for_the_plan(self):
        # The flag missing means the project predates it; the budget-leak rule still
        # judges observed reality, but the flag itself is not the finding.
        facts = _facts(create=None, deployments=(_dep("main", target="production", state="READY"),))
        findings = plan_project_findings(facts, now_ms=NOW, window_days=3)
        assert "creation-gate" not in _rules(findings)


class TestBudgetLeak:
    def test_the_incident_is_caught(self):
        """83 created, feature branches among them: every canceled PR deploy is a leak."""
        leaks = [_dep("feat/some-pr"), _dep("feat/other-pr"), _dep("claude/scratch-branch")]
        facts = _facts(allow=("main", "preview", "slot/*"), deny=("**",), deployments=tuple(leaks))
        findings = plan_project_findings(facts, now_ms=NOW, window_days=3)
        leak = [f for f in findings if f.rule == "budget-leak"]
        assert len(leak) == 1
        assert leak[0].severity == "error"
        assert "feat/some-pr" in leak[0].detail

    def test_production_and_declared_slots_are_not_leaks(self):
        kept = (
            _dep("main", target="production", state="READY"),
            _dep("preview", state="READY"),
            _dep("slot/2", state="READY"),
        )
        facts = _facts(allow=("main", "preview", "slot/*"), deny=("**",), deployments=kept)
        assert "budget-leak" not in _rules(plan_project_findings(facts, now_ms=NOW, window_days=3))

    def test_main_backup_without_an_allowlist_entry_is_a_leak(self):
        facts = _facts(create=None, deployments=(_dep("main-backup"),))
        assert "budget-leak" in _rules(plan_project_findings(facts, now_ms=NOW, window_days=3))

    def test_deployments_outside_the_window_do_not_count(self):
        facts = _facts(allow=("main",), deny=("**",), deployments=(_dep("feat/old", age_days=30),))
        assert "budget-leak" not in _rules(plan_project_findings(facts, now_ms=NOW, window_days=3))


class TestRefAllowed:
    def test_slot_glob_matches_numbered_branch(self):
        facts = _facts(allow=("main", "slot/*"), deny=("**",))
        assert ref_is_allowed("slot/3", facts)

    def test_unlisted_branch_is_not_allowed(self):
        facts = _facts(allow=("main", "preview", "slot/*"), deny=("**",))
        assert not ref_is_allowed("feat/portal-own-integration", facts)

    def test_python_fnmatch_star_crosses_slashes(self):
        # Deliberate: `fnmatch` here is *not* Vercel's minimatch. A bare `*` matches
        # `feat/portal` under fnmatch but not under Vercel, so this module never
        # relies on `*` as a deny-all — the empirical budget-leak rule counts
        # observed deployments instead of reasoning about pattern semantics.
        facts = _facts(allow=("*",), deny=())
        assert ref_is_allowed("feat/portal", facts)


class TestProductionFreshness:
    def test_production_behind_the_local_tip_is_reported(self):
        facts = _facts(
            allow=("main",),
            deny=("**",),
            deployments=(_dep("main", target="production", state="READY", age_days=1.0, sha="aaaa111"),),
            head_sha="bbbb222",
        )
        findings = plan_project_findings(facts, now_ms=NOW, window_days=3)
        assert "production-stale" in _rules(findings)

    def test_production_at_the_tip_is_clean(self):
        facts = _facts(
            allow=("main",),
            deny=("**",),
            deployments=(_dep("main", target="production", state="READY", sha="bbbb222"),),
            head_sha="bbbb222",
        )
        assert plan_project_findings(facts, now_ms=NOW, window_days=3) == []


class TestPolicyConfigVercel:
    def test_shipped_config_declares_a_team(self):
        cfg = PolicyConfig.parse(json.loads(config_path().read_text(encoding="utf-8")))
        rules = cfg.vercel_gating("design-system")
        assert rules.team_id.startswith("team_")
        assert rules.window_days >= 1

    def test_repo_without_a_vercel_section_gets_defaults(self):
        cfg = PolicyConfig.parse({})
        assert cfg.vercel_gating("anything").window_days == 3


class TestAuditProjectsBoundary:
    """A malformed API response degrades one project or fails cleanly, never with a validation traceback."""

    PROJECT = {"id": "prj_1", "name": "site", "link": {"repo": "site"}}

    def _audit(self, monkeypatch: pytest.MonkeyPatch, responses: dict[str, object]):
        monkeypatch.setattr(vercel_gating, "vercel_token", lambda: "token")
        monkeypatch.setattr(vercel_gating, "_get", lambda path, params, token: responses[path])
        return vercel_gating.audit_projects(team_id="team_x", window_days=3, repos_dir=None)

    def test_unreadable_deployments_are_reported_and_the_audit_continues(self, monkeypatch: pytest.MonkeyPatch):
        results = self._audit(
            monkeypatch,
            {"/v9/projects": {"projects": [self.PROJECT]}, "/v6/deployments": {"deployments": "nope"}},
        )
        [audit] = results
        assert audit.facts.recent_deployments == ()
        assert "deployments-unreadable" in _rules(audit.findings)

    def test_unreadable_project_list_raises_runtime_error(self, monkeypatch: pytest.MonkeyPatch):
        with pytest.raises(RuntimeError, match="unreadable project list"):
            self._audit(monkeypatch, {"/v9/projects": {"projects": "nope"}})

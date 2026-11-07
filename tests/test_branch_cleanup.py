"""Tests for merged-branch cleanup and the policy config it reads.

The classification logic is pure, so it is tested directly. The guards get the
most attention: a wrong verdict here deletes someone's work, and one of them
exists because a dry run really did propose deleting 30+ branches on a
third party's repo.
"""

from __future__ import annotations

import json
import pathlib

import pytest

from rig.repo_governance import branch_cleanup
from rig.repo_governance.branch_cleanup import (
    BranchVerdict,
    CleanupPlan,
    can_prune,
    classify_branches,
)
from rig.repo_governance.policy_config import (
    BranchCleanupRules,
    GitignoreEntrySpec,
    PolicyConfig,
    load_policy_config,
)

DEFAULT_RULES = BranchCleanupRules(
    enabled=True,
    protected=("main", "master", "main-backup"),
    protect_open_pr_heads=True,
    protect_prefixes=("dependabot/", "renovate/"),
)


def _classify(branches, merged=None, open_=None, rules=DEFAULT_RULES):
    return {
        v.branch: v for v in classify_branches(branches, merged_heads=merged or {}, open_heads=open_ or {}, rules=rules)
    }


class TestClassification:
    def test_merged_branch_is_deletable(self):
        got = _classify(["feat/x"], merged={"feat/x": 12})
        assert got["feat/x"].status == "merged"
        assert got["feat/x"].pr == 12
        assert got["feat/x"].deletable

    def test_open_pr_is_never_deletable(self):
        got = _classify(["feat/x"], open_={"feat/x": 5})
        assert got["feat/x"].status == "open-pr"
        assert not got["feat/x"].deletable

    def test_branch_with_no_pr_is_never_deletable(self):
        """Where genuinely unmerged work lives — reported, never deleted."""
        got = _classify(["evaluation"])
        assert got["evaluation"].status == "no-pr"
        assert not got["evaluation"].deletable

    def test_open_pr_wins_over_a_previous_merge(self):
        """A reused branch is live work, whatever its history says."""
        got = _classify(["feat/x"], merged={"feat/x": 3}, open_={"feat/x": 9})
        assert got["feat/x"].status == "open-pr"

    @pytest.mark.parametrize("branch", ["main", "master", "main-backup"])
    def test_protected_branches(self, branch):
        got = _classify([branch], merged={branch: 1})
        assert got[branch].status == "protected"
        assert not got[branch].deletable

    @pytest.mark.parametrize("branch", ["dependabot/nuget/x", "renovate/y"])
    def test_bot_prefixes_are_protected(self, branch):
        """Bots recreate and retire their own branches; deleting fights them."""
        got = _classify([branch], merged={branch: 1})
        assert got[branch].status == "protected"

    def test_protection_beats_everything(self):
        got = _classify(["main"], merged={"main": 1}, open_={"main": 2})
        assert got["main"].status == "protected"

    def test_open_pr_protection_can_be_disabled(self):
        rules = BranchCleanupRules(protect_open_pr_heads=False, protected=("main",))
        got = _classify(["feat/x"], merged={"feat/x": 3}, open_={"feat/x": 9}, rules=rules)
        assert got["feat/x"].status == "merged"

    def test_empty_repo(self):
        assert classify_branches([], merged_heads={}, open_heads={}, rules=DEFAULT_RULES) == ()


class TestPushGuard:
    """The guard that stops cleanup touching repos you do not own.

    A clone of an upstream you never forked looks identical to your own repo
    on disk, and its merged-PR list is full of branches a delete would target.
    """

    @pytest.mark.parametrize("perm", ["ADMIN", "MAINTAIN", "WRITE", "admin", "write"])
    def test_permissions_that_allow_pruning(self, perm):
        assert can_prune(perm) is True

    @pytest.mark.parametrize("perm", ["READ", "TRIAGE", "NONE", "", "unknown"])
    def test_permissions_that_do_not(self, perm):
        assert can_prune(perm) is False


class TestCleanupPlan:
    def test_skipped_plan_carries_no_verdicts(self):
        """A caller that ignores `skipped` must still delete nothing."""
        plan = CleanupPlan(repo="r", verdicts=(), skipped="no push access")
        assert plan.deletable == ()

    def test_deletable_filters_to_merged_only(self):
        plan = CleanupPlan(
            repo="r",
            verdicts=(
                BranchVerdict("a", "merged", 1),
                BranchVerdict("b", "no-pr"),
                BranchVerdict("c", "open-pr", 2),
                BranchVerdict("d", "protected"),
            ),
        )
        assert [v.branch for v in plan.deletable] == ["a"]
        assert [v.branch for v in plan.of("no-pr")] == ["b"]


class TestGhBoundary:
    """What ``gh`` prints is decoded once; anything unreadable must never read as "no PRs"."""

    @pytest.fixture
    def gh(self, monkeypatch: pytest.MonkeyPatch, tmp_path: pathlib.Path):
        replies: dict[str, str] = {}

        def fake_run(args: list[str], cwd: pathlib.Path | None = None) -> str:
            if args[:2] == ["git", "branch"]:
                return "origin/feat/x\n"
            if args[:3] == ["gh", "repo", "view"]:
                return replies.get("view", "")
            if args[:3] == ["gh", "pr", "list"]:
                return replies.get(args[args.index("--state") + 1], "")
            return ""

        monkeypatch.setattr(branch_cleanup, "_run", fake_run)
        branch_cleanup._repo_identity_cached.cache_clear()
        yield replies
        branch_cleanup._repo_identity_cached.cache_clear()

    VIEW = json.dumps({"nameWithOwner": "o/r", "viewerPermission": "WRITE", "isFork": False})

    def test_identity_is_decoded_from_gh_repo_view(self, gh, tmp_path):
        gh["view"] = self.VIEW
        assert branch_cleanup.repo_identity(tmp_path) == branch_cleanup.RepoIdentity(slug="o/r", permission="WRITE")

    def test_misshapen_identity_is_unresolved(self, gh, tmp_path):
        gh["view"] = json.dumps({"nameWithOwner": "o/r"})
        assert not branch_cleanup.repo_identity(tmp_path).resolved

    def test_pr_heads_keep_the_newest_pr_per_branch(self, gh, tmp_path):
        gh["merged"] = json.dumps([{"number": 3, "headRefName": "a"}, {"number": 9, "headRefName": "a"}])
        assert branch_cleanup.list_merged_pr_heads(tmp_path) == {"a": 9}

    @pytest.mark.parametrize("reply", ["", "not json", json.dumps([{"number": "x"}])])
    def test_unreadable_pr_list_is_none_not_empty(self, gh, tmp_path, reply):
        gh["open"] = reply
        assert branch_cleanup.list_open_pr_heads(tmp_path) is None

    def test_plan_is_skipped_when_pull_requests_are_unreadable(self, gh, tmp_path):
        gh["view"] = self.VIEW
        gh["merged"] = json.dumps([{"number": 1, "headRefName": "feat/x"}])
        plan = branch_cleanup.plan_cleanup(tmp_path, fetch=False)
        assert plan.skipped == "could not read pull requests from gh"
        assert plan.deletable == ()


class TestGitignoreEntry:
    def _entry(self, **over):
        base = dict(
            name="temp",
            comment="Temporary files",
            write=("/temp/", "temp/"),
            satisfied_by=("/temp", "temp/"),
        )
        base.update(over)
        return GitignoreEntrySpec(**base).to_entry()

    def test_present_when_any_spelling_matches(self):
        """One intent, several spellings — appending a second is churn."""
        assert self._entry().is_present("node_modules\n/temp\n")
        assert self._entry().is_present("temp/\n")

    def test_absent_when_no_spelling_matches(self):
        assert not self._entry().is_present("node_modules\ndist\n")

    def test_render_includes_comment_and_all_write_lines(self):
        rendered = self._entry().render()
        assert "# Temporary files" in rendered
        assert "/temp/" in rendered
        assert "temp/" in rendered

    def test_satisfied_by_defaults_to_write(self):
        entry = GitignoreEntrySpec(name="todo", write=("TODO.md",)).to_entry()
        assert entry.satisfied_by == ("TODO.md",)


class TestPolicyResolution:
    def _config(self):
        return PolicyConfig.parse(
            {
                "defaults": {
                    "gitignore_entries": [{"name": "temp", "write": ["/temp/"]}],
                    "branch_cleanup": {"enabled": True, "protected": ["main"]},
                },
                "profiles": {
                    "public": {
                        "repos": ["Widget"],
                        "required_checks": ["build"],
                        "gitignore_entries": [{"name": "dist", "write": ["/dist/"]}],
                    },
                    "private": {"repos": ["Secret"]},
                },
            }
        )

    def test_profile_lookup_by_bare_name_and_slug(self):
        cfg = self._config()
        assert cfg.profile_for("Widget") == "public"
        assert cfg.profile_for("rtkelly13/Widget") == "public"

    def test_unregistered_repo_has_no_profile(self):
        assert self._config().profile_for("Unknown") is None

    def test_profile_overrides_replace_defaults_wholesale(self):
        """No deep merge — otherwise an inherited entry cannot be removed."""
        names = [e.name for e in self._config().gitignore_entries("Widget")]
        assert names == ["dist"]

    def test_profile_without_the_key_inherits_the_default(self):
        names = [e.name for e in self._config().gitignore_entries("Secret")]
        assert names == ["temp"]

    def test_unregistered_repo_still_gets_defaults(self):
        """Governance should apply before someone remembers to register a repo."""
        names = [e.name for e in self._config().gitignore_entries("Brand.New")]
        assert names == ["temp"]

    def test_required_checks_come_from_the_profile(self):
        assert self._config().required_checks("Widget") == ("build",)
        assert self._config().required_checks("Secret") == ()

    def test_missing_config_yields_empty_policy_not_a_crash(self, tmp_path):
        cfg = load_policy_config(tmp_path / "absent.json")
        assert cfg.gitignore_entries("anything") == ()

    def test_malformed_config_yields_empty_policy_not_a_crash(self, tmp_path):
        bad = tmp_path / "bad.json"
        bad.write_text("{not json")
        assert load_policy_config(bad).gitignore_entries("anything") == ()

    def test_schema_error_fails_loudly_instead_of_dropping_protections(self, tmp_path):
        bad = tmp_path / "typo.json"
        bad.write_text(json.dumps({"defaults": {"branch_cleanup": {"protect_prefixes": "renovate/"}}}))
        with pytest.raises(ValueError, match="protect_prefixes"):
            load_policy_config(bad)


class TestShippedConfig:
    """The real config/repo_policies.json must stay coherent."""

    def _shipped(self) -> dict:
        path = pathlib.Path(__file__).parents[1] / "config" / "repo_policies.json"
        return json.loads(path.read_text(encoding="utf-8"))

    def test_defaults_block_exists(self):
        assert "defaults" in self._shipped()

    def test_every_profile_repo_resolves_to_that_profile(self):
        cfg = PolicyConfig.parse(self._shipped())
        for name, profile in cfg.file.profiles.items():
            for repo in profile.repos:
                assert cfg.profile_for(repo) == name, f"{repo} resolved wrong"

    def test_no_repo_is_declared_in_two_profiles(self):
        seen: dict[str, str] = {}
        for name, profile in self._shipped()["profiles"].items():
            for repo in profile["repos"]:
                assert repo not in seen, f"{repo} in both {seen.get(repo)} and {name}"
                seen[repo] = name

    def test_main_is_protected_from_cleanup(self):
        rules = PolicyConfig.parse(self._shipped()).branch_cleanup("Parquet.SourceGenerator")
        assert rules.is_protected("main")

    def test_delete_branch_on_merge_is_the_default_policy(self):
        settings = PolicyConfig.parse(self._shipped()).repo_settings("Parquet.SourceGenerator")
        assert settings["delete_branch_on_merge"] is True

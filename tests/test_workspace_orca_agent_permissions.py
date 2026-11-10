"""Agent permission modes and launch arguments in the doctor and sync, and instance selection."""

from __future__ import annotations

import pathlib
from datetime import UTC, datetime

import pytest

from rig.workspace import orca_cli
from rig.workspace.orca import (
    OrcaPolicy,
    OrcaSettings,
    OrcaSnapshot,
    SettingsCopy,
    diagnose,
    load_policy,
    policy_repo_path,
    settings_manual_actions,
)
from rig.workspace.orca_agent_permissions import (
    AgentPermissionsPolicy,
    Permission,
    flags,
    launch_checks,
    permission_checks,
)
from rig.workspace.orca_rpc import USER_DATA_ENV
from rig.workspace.packages import Status

pytestmark = pytest.mark.ci

REPO_ROOT = pathlib.Path(__file__).resolve().parents[1]
NOW = datetime(2026, 11, 10, 12, 0, tzinfo=UTC)
PERMISSIONS = AgentPermissionsPolicy(
    default="own", agents={"claude": "auto", "antigravity": "full-access", "opencode": "own"}
)
POLICY = OrcaPolicy(agent_args={"claude": "", "opencode": "--auto"}, agent_permissions=PERMISSIONS)

#: Orca 1.4.255 straight after its upgrade: one Full access default, custom arguments emptied.
_UPGRADED = OrcaSettings(
    agent_default_args={"claude": "", "opencode": "", "antigravity": ""},
    permission_default="full-access",
    permission_overrides={"opencode": "own", "omp": "own"},
)
_UPGRADED_LAUNCH = {
    "claude": "--dangerously-skip-permissions",
    "opencode": "",
    "antigravity": "--dangerously-skip-permissions",
}
_WANTED = OrcaSettings(
    agent_default_args={"claude": "", "opencode": "--auto"},
    permission_default="own",
    permission_overrides={"claude": "auto", "antigravity": "full-access", "omp": "own"},
)
_WANTED_LAUNCH = {
    "claude": "--permission-mode auto",
    "opencode": "--auto",
    "antigravity": "--dangerously-skip-permissions",
}


def _snapshot(settings: OrcaSettings | None, launch: dict[str, str] | None) -> OrcaSnapshot:
    copy = SettingsCopy(pathlib.Path("profile-state.db.backup.1.db"), NOW, settings) if settings else None
    return OrcaSnapshot(copy, (), launch_args=launch)


def _statuses(snapshot: OrcaSnapshot) -> dict[tuple[str, str], Status]:
    findings = diagnose(POLICY, snapshot, estate_root=pathlib.Path("/code"), now=NOW)
    return {(f.subject, f.check): f.status for f in findings if f.subject != "settings" or "permission" in f.check}


def test_the_full_access_upgrade_fails_claudes_permission_and_launch_line() -> None:
    statuses = _statuses(_snapshot(_UPGRADED, _UPGRADED_LAUNCH))
    assert statuses[("settings", "permission default")] is Status.FAIL
    assert statuses[("agent claude", "permission")] is Status.FAIL
    assert statuses[("agent claude", "launch args")] is Status.FAIL
    assert statuses[("agent opencode", "custom args")] is Status.FAIL
    assert statuses[("agent antigravity", "launch args")] is Status.OK


def test_the_wanted_settings_pass() -> None:
    statuses = _statuses(_snapshot(_WANTED, _WANTED_LAUNCH))
    assert set(statuses.values()) == {Status.OK}
    assert ("agent omp", "permission") in statuses


def test_launch_order_does_not_matter() -> None:
    (check,) = launch_checks(
        AgentPermissionsPolicy(agents={"claude": "auto"}),
        {"claude": "--verbose"},
        {"claude": "--verbose --permission-mode auto"},
    )
    assert check.ok


def test_an_agent_orca_has_no_flags_for_is_not_checked_at_launch() -> None:
    assert launch_checks(AgentPermissionsPolicy(agents={"opencode": "auto"}), {}, {"opencode": ""}) == []


def test_an_unexpected_override_is_caught() -> None:
    checks = {c.subject: c for c in permission_checks(PERMISSIONS, ["gemini"], "own", {"gemini": "full-access"})}
    assert not checks["agent gemini"].ok
    assert checks["agent gemini"].fix == "Orca Settings → Agents → gemini: set permission to Own"


def test_orca_down_skips_the_launch_check_but_still_reads_the_backup() -> None:
    findings = diagnose(POLICY, _snapshot(_UPGRADED, None), estate_root=pathlib.Path("/code"), now=NOW)
    by_check = {(f.subject, f.check): f.status for f in findings}
    assert by_check[("agents", "launch args")] is Status.SKIP
    assert by_check[("agent claude", "permission")] is Status.FAIL
    assert ("agent claude", "launch args") not in by_check


def test_sync_lists_permissions_and_custom_args_as_manual_and_never_writes() -> None:
    actions = settings_manual_actions(POLICY, _snapshot(_UPGRADED, _UPGRADED_LAUNCH))
    assert {a.action for a in actions} == {"manual"}
    assert not any(a.applied for a in actions)
    fields = {(a.repo, a.field) for a in actions}
    assert ("agent claude", "permission") in fields
    assert ("agent opencode", "custom args") in fields
    assert all(a.field != "launch args" for a in actions)
    assert settings_manual_actions(POLICY, _snapshot(_WANTED, _WANTED_LAUNCH)) == []


def test_the_checked_in_policy_keeps_permission_flags_out_of_custom_args() -> None:
    # A permission flag in custom arguments makes Orca read them as "mixed" and lock the mode.
    policy = load_policy(REPO_ROOT / "config" / "orca.json")
    modes: tuple[Permission, ...] = ("ask", "accept-edits", "auto", "full-access")
    for agent, args in policy.agent_args.items():
        permission_tokens = {token for mode in modes for token in (flags(mode, agent) or "").split()}
        assert not permission_tokens & set(args.split()), agent


def test_the_checked_in_policy_asks_for_auto_only_where_orca_has_it() -> None:
    policy = load_policy(REPO_ROOT / "config" / "orca.json")
    assert policy.agent_permissions is not None
    auto = [agent for agent, mode in policy.agent_permissions.agents.items() if mode == "auto"]
    assert auto
    assert all(flags("auto", agent) for agent in auto)


def test_instance_defaults_to_the_orca_that_launched_the_terminal(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv(USER_DATA_ENV, str(tmp_path / "orca-personal"))
    assert orca_cli._user_data(None) == tmp_path / "orca-personal"  # pyright: ignore[reportPrivateUsage]
    assert orca_cli._user_data(str(tmp_path / "other")) == tmp_path / "other"  # pyright: ignore[reportPrivateUsage]
    monkeypatch.delenv(USER_DATA_ENV)
    assert orca_cli._user_data(None) is None  # pyright: ignore[reportPrivateUsage]


def test_policy_repo_paths_follow_the_machines_code_root(tmp_path: pathlib.Path) -> None:
    assert policy_repo_path("~/code/personal/blog", tmp_path) == tmp_path / "personal" / "blog"
    assert policy_repo_path("~/elsewhere/blog", tmp_path) == pathlib.Path.home() / "elsewhere" / "blog"

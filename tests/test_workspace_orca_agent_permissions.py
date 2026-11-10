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
from rig.workspace.orca_agent_launch import sync_agent_launch
from rig.workspace.orca_agent_permissions import (
    AgentPermissionsPolicy,
    Permission,
    configurable,
    flags,
    launch_checks,
    launch_lines,
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
    assert ("agent omp", "permission") not in statuses  # Orca offers omp no mode


def test_launch_order_does_not_matter() -> None:
    (check,) = launch_checks(
        AgentPermissionsPolicy(agents={"claude": "auto"}),
        {"claude": "--verbose"},
        {"claude": "--verbose --permission-mode auto"},
    )
    assert check.ok


def test_a_mode_rig_has_no_flags_for_is_not_checked_at_launch() -> None:
    assert launch_checks(AgentPermissionsPolicy(agents={"copilot": "auto"}), {}, {"copilot": ""}) == []


def test_an_agent_orca_offers_no_mode_launches_with_its_custom_args_alone() -> None:
    (check,) = launch_checks(AgentPermissionsPolicy(), {"opencode2": "--auto"}, {"opencode2": "--auto"})
    assert check.ok


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


def test_an_override_orca_offers_no_choice_for_is_not_checked() -> None:
    # Orca kept OpenCode at Full access through the upgrade, but launches it the same in every mode.
    assert not configurable("opencode")
    checks = permission_checks(PERMISSIONS, ["opencode2"], "own", {"opencode2": "full-access"})
    assert {c.subject for c in checks} == {"settings", "agent claude", "agent antigravity"}


def test_launch_lines_carry_the_mode_flags_and_custom_args_for_agents_that_differ() -> None:
    lines = launch_lines(PERMISSIONS, POLICY.agent_args, _UPGRADED_LAUNCH)
    assert lines == {"claude": "--permission-mode auto", "opencode": "--auto"}
    assert launch_lines(PERMISSIONS, POLICY.agent_args, _WANTED_LAUNCH) == {}


def test_sync_plans_launch_lines_and_writes_only_with_apply() -> None:
    sent: list[dict[str, str]] = []

    def write(lines: dict[str, str]) -> dict[str, str]:
        sent.append(lines)
        return {**_UPGRADED_LAUNCH, **lines}

    snapshot = _snapshot(_UPGRADED, _UPGRADED_LAUNCH)
    planned = sync_agent_launch(POLICY, snapshot, write=write)
    assert [(a.repo, a.action, a.applied) for a in planned] == [
        ("agent claude", "update", False),
        ("agent opencode", "update", False),
    ]
    assert sent == []

    applied = sync_agent_launch(POLICY, snapshot, apply=True, write=write)
    assert all(a.applied for a in applied)
    assert sent == [{"claude": "--permission-mode auto", "opencode": "--auto"}]


def test_a_line_orca_does_not_take_is_an_error_naming_the_manual_fix() -> None:
    (action,) = sync_agent_launch(
        OrcaPolicy(agent_permissions=AgentPermissionsPolicy(agents={"claude": "own"})),
        _snapshot(_WANTED, {"claude": "--permission-mode auto"}),
        apply=True,
        write=lambda _lines: {"claude": "--permission-mode auto"},
    )
    assert (action.action, action.applied) == ("error", False)
    assert "Orca Settings" in action.detail


def test_sync_reports_orca_going_away_mid_write() -> None:
    def write(_lines: dict[str, str]) -> dict[str, str] | None:
        return None

    actions = sync_agent_launch(POLICY, _snapshot(_UPGRADED, _UPGRADED_LAUNCH), apply=True, write=write)
    assert {a.action for a in actions} == {"error"}


def test_sync_leaves_agents_alone_without_a_permission_policy_or_a_running_orca() -> None:
    assert sync_agent_launch(OrcaPolicy(agent_args={"claude": ""}), _snapshot(_UPGRADED, _UPGRADED_LAUNCH)) == []
    assert sync_agent_launch(POLICY, _snapshot(_UPGRADED, None)) == []
    assert settings_manual_actions(POLICY, _snapshot(_UPGRADED, _UPGRADED_LAUNCH)) == []


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

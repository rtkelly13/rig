"""Tests for `workspace orca doctor` and `workspace orca sync`: Orca install vs config/orca.json."""

from __future__ import annotations

import json
import os
import pathlib
import sqlite3
from collections.abc import Callable
from contextlib import closing
from dataclasses import replace
from datetime import UTC, datetime, timedelta

import pytest
from typer.testing import CliRunner

from rig.estate.orca_runtime import ORCA_UNAVAILABLE_EXIT
from rig.typed_json import decode_json
from rig.workspace import orca_cli
from rig.workspace.cli import app
from rig.workspace.orca import (
    OrcaPolicy,
    OrcaRepo,
    OrcaRepoIcon,
    OrcaSettings,
    OrcaSnapshot,
    RepoPolicy,
    RpcRequest,
    SettingsCopy,
    SyncAction,
    build_repo_icon_payload,
    diagnose,
    icon_matches,
    live_repos,
    load_policy,
    newest_backup,
    read_settings,
    read_settings_copy,
    settings_manual_actions,
    sync_repos,
    take_snapshot,
)
from rig.workspace.orca_agent_sleep import AgentSleepPolicy
from rig.workspace.orca_hooks import Scripts
from rig.workspace.packages import Status

pytestmark = pytest.mark.ci

NOW = datetime(2026, 10, 24, 12, 0, tzinfo=UTC)
REPO_ROOT = pathlib.Path(__file__).resolve().parents[1]


def _write_profile_db(path: pathlib.Path, settings: str) -> None:
    with closing(sqlite3.connect(path)) as db:
        db.execute("create table profile_state_documents (domain text primary key, payload text)")
        db.execute("insert into profile_state_documents values ('settings', ?)", (settings,))
        db.commit()


def _settings_copy(
    args: dict[str, str], workspace_dir: str = "~/orca/workspaces", age_hours: float = 1
) -> SettingsCopy:
    return SettingsCopy(
        source=pathlib.Path("profile-state.db.backup.1.db"),
        saved=NOW - timedelta(hours=age_hours),
        settings=OrcaSettings(agent_default_args=args, workspace_dir=workspace_dir),
    )


def _repo(
    name: str,
    path: str,
    setup: str = "",
    archive: str = "",
    visibility: str = "hide",
    base_ref: str = "main",
    color: str = "#737373",
    icon: OrcaRepoIcon | None = None,
) -> OrcaRepo:
    icon_payload: dict[str, str] = {}
    if icon is not None:
        icon_payload = {
            "type": icon.type,
            "name": icon.name,
            "emoji": icon.emoji,
            "src": icon.src,
            "source": icon.source,
            "label": icon.label,
        }
    return decode_repo(
        {
            "path": path,
            "displayName": name,
            "hookSettings": {"scripts": {"setup": setup, "archive": archive}},
            "externalWorktreeVisibility": visibility,
            "worktreeBaseRef": base_ref,
            "badgeColor": color,
            "repoIcon": icon_payload,
        }
    )


def decode_repo(raw: dict[str, object]) -> OrcaRepo:
    """One repo as `orca repo list --json` returns it."""
    repos = live_repos(lambda _args: json.dumps({"ok": True, "result": {"repos": [raw]}}))
    assert repos is not None
    return repos[0]


def test_checked_in_policy_decodes() -> None:
    policy = load_policy(REPO_ROOT / "config" / "orca.json")
    assert policy.agent_args["claude"] == ""
    assert policy.agent_args["opencode"] == "--auto"
    assert policy.agent_permissions is not None
    assert policy.agent_permissions.wanted("claude") == "auto"
    assert policy.agent_permissions.wanted("antigravity") == "full-access"
    assert policy.agent_permissions.wanted("opencode") == "own"
    assert policy.repos["design-system"].external_worktree_visibility == "show"
    assert policy.repos["design-system"].color == "#ec4899"
    assert policy.repos["design-system"].icon == "Palette"
    assert policy.repos["rig"].color == "#6366f1"
    assert policy.repos["rig"].icon == "SquareTerminal"


def test_read_settings_from_backup(tmp_path: pathlib.Path) -> None:
    db = tmp_path / "profile-state.db.backup.1.db"
    _write_profile_db(db, json.dumps({"agentDefaultArgs": {"claude": "--x"}, "workspaceDir": "/w", "other": 1}))
    settings = read_settings(db)
    assert settings == OrcaSettings(agent_default_args={"claude": "--x"}, workspace_dir="/w")


def test_read_settings_unreadable_is_none(tmp_path: pathlib.Path) -> None:
    junk = tmp_path / "profile-state.db.backup.1.db"
    junk.write_text("not sqlite", encoding="utf-8")
    assert read_settings(junk) is None
    assert read_settings_copy(tmp_path) is None


def test_newest_backup_ignores_live_db(tmp_path: pathlib.Path) -> None:
    (tmp_path / "profile-state.db").write_text("live", encoding="utf-8")
    older = tmp_path / "profile-state.db.backup.1.db"
    newer = tmp_path / "profile-state.db.backup.2.db"
    for path, mtime in ((older, 1_000), (newer, 2_000)):
        path.write_text("", encoding="utf-8")
        os.utime(path, (mtime, mtime))
    assert newest_backup(tmp_path) == newer
    assert newest_backup(tmp_path / "missing") is None


def test_live_repos_reads_all_repo_attributes() -> None:
    repo = _repo(
        "rig",
        "/code/rig",
        setup="pnpm install",
        archive="pnpm clean",
        visibility="show",
        base_ref="develop",
        color="#6366f1",
        icon=OrcaRepoIcon(type="lucide", name="terminal"),
    )
    assert repo.hook_settings is not None
    assert repo.hook_settings.scripts == Scripts(setup="pnpm install", archive="pnpm clean")
    assert repo.external_worktree_visibility == "show"
    assert repo.base_ref == "develop"
    assert repo.color == "#6366f1"
    assert repo.icon_identifier == "terminal"


def test_live_repos_cli_failure_is_none() -> None:
    assert live_repos(lambda _args: None) is None
    assert live_repos(lambda _args: "not json") is None


def test_agent_args_mismatch_fails_with_fix() -> None:
    policy = OrcaPolicy(agent_args={"claude": "", "opencode": "--auto", "antigravity": "--skip"})
    snapshot = OrcaSnapshot(
        settings=_settings_copy({"claude": "--dangerously-skip-permissions", "antigravity": "--skip"}), repos=()
    )
    findings = diagnose(policy, snapshot, estate_root=pathlib.Path("/code"), now=NOW)
    by_subject = {f.subject: f for f in findings if f.check == "custom args"}
    assert by_subject["agent claude"].status is Status.FAIL
    assert by_subject["agent claude"].fix == 'Orca Settings → Agents → claude: set custom arguments to ""'
    assert by_subject["agent opencode"].status is Status.FAIL
    assert '"--auto"' in by_subject["agent opencode"].detail
    assert by_subject["agent antigravity"].status is Status.OK


def test_stale_backup_warns() -> None:
    snapshot = OrcaSnapshot(settings=_settings_copy({}, age_hours=48), repos=())
    findings = diagnose(OrcaPolicy(), snapshot, estate_root=pathlib.Path("/code"), now=NOW)
    assert [f.status for f in findings if f.check == "source"] == [Status.WARN]


def test_missing_sources_skip() -> None:
    findings = diagnose(OrcaPolicy(), OrcaSnapshot(settings=None, repos=None), estate_root=pathlib.Path("/c"), now=NOW)
    assert [f.status for f in findings] == [Status.SKIP, Status.SKIP]


def test_workspace_dir_expands_home() -> None:
    policy = OrcaPolicy(workspace_dir="~/orca/workspaces")
    home_dir = (pathlib.Path.home() / "orca" / "workspaces").as_posix()
    ok = diagnose(policy, OrcaSnapshot(_settings_copy({}, workspace_dir=home_dir), ()), estate_root=pathlib.Path("/c"))
    bad = diagnose(
        policy, OrcaSnapshot(_settings_copy({}, workspace_dir="/elsewhere"), ()), estate_root=pathlib.Path("/c")
    )
    assert [f.status for f in ok if f.check == "workspace dir"] == [Status.OK]
    assert [f.status for f in bad if f.check == "workspace dir"] == [Status.FAIL]


def _sleep_snapshot(enabled: bool | None, idle_ms: int | None = None) -> OrcaSnapshot:
    copy = _settings_copy({})
    settings = replace(copy.settings, agent_sleep=enabled, agent_sleep_idle_ms=idle_ms)
    return OrcaSnapshot(replace(copy, settings=settings), ())


def _sleep_findings(policy: OrcaPolicy, snapshot: OrcaSnapshot) -> list[tuple[Status, str]]:
    findings = diagnose(policy, snapshot, estate_root=pathlib.Path("/c"), now=NOW)
    return [(f.status, f.detail) for f in findings if f.check == "agent sleep"]


def test_agent_sleep_is_checked_against_the_policy() -> None:
    on = OrcaPolicy(agent_sleep=AgentSleepPolicy(enabled=True, idle_minutes=30))

    assert _sleep_findings(on, _sleep_snapshot(True, 1_800_000)) == [(Status.OK, "on, 30m idle")]
    # Orca reads a missing or out-of-range window as its 30-minute default.
    assert _sleep_findings(on, _sleep_snapshot(True, None)) == [(Status.OK, "on, 30m idle")]
    assert _sleep_findings(on, _sleep_snapshot(True, 5)) == [(Status.OK, "on, 30m idle")]
    assert _sleep_findings(on, _sleep_snapshot(False)) == [(Status.FAIL, "off, want on, 30m idle")]
    assert _sleep_findings(on, _sleep_snapshot(True, 600_000)) == [(Status.FAIL, "on, 10m idle, want on, 30m idle")]
    assert _sleep_findings(OrcaPolicy(agent_sleep=AgentSleepPolicy(enabled=False)), _sleep_snapshot(None)) == [
        (Status.OK, "off")
    ]
    assert _sleep_findings(OrcaPolicy(), _sleep_snapshot(False)) == []


def test_sync_reports_agent_sleep_as_manual() -> None:
    policy = OrcaPolicy(agent_sleep=AgentSleepPolicy(enabled=True, idle_minutes=30))

    (action,) = settings_manual_actions(policy, _sleep_snapshot(False))
    assert (action.action, action.old_value, action.new_value, action.applied) == (
        "manual",
        "off",
        "on, 30m idle",
        False,
    )
    assert "Settings → Experimental" in action.detail
    assert settings_manual_actions(policy, _sleep_snapshot(True, 1_800_000)) == []
    assert settings_manual_actions(policy, OrcaSnapshot(None, ())) == []


def test_launch_args_come_from_the_running_orca_and_custom_args_from_the_backup(tmp_path: pathlib.Path) -> None:
    db = tmp_path / "profile-state.db.backup.1.db"
    settings = {"agentDefaultArgs": {"claude": ""}, "agentPermissionDefault": "full-access"}
    _write_profile_db(db, json.dumps(settings))
    composed = {"claude": "--dangerously-skip-permissions"}

    snapshot = take_snapshot(tmp_path, run=lambda _args: None, launch=lambda: composed)

    assert snapshot.settings is not None
    assert snapshot.settings.settings.agent_default_args == {"claude": ""}
    assert snapshot.settings.settings.permission_default == "full-access"
    assert snapshot.launch_args == composed


def test_repo_policy_checks_all_attributes() -> None:
    policy = OrcaPolicy(
        repos={
            "rig": RepoPolicy(
                setup="uv sync && pnpm install",
                archive="uv cache prune",
                color="#6366f1",
                base_ref="main",
                icon="SquareTerminal",
            ),
            "design-system": RepoPolicy(
                external_worktree_visibility="show",
                color="#ec4899",
                icon="Palette",
            ),
            "absent": RepoPolicy(setup="x"),
        }
    )
    repos = (
        _repo(
            "rig",
            "/code/rig",
            setup="pnpm install",
            archive="",
            color="#737373",
            base_ref="main",
            icon=OrcaRepoIcon(type="lucide", name="Code2"),
        ),
        _repo(
            "design-system",
            "/code/design-system",
            visibility="hide",
            color="#ec4899",
            icon=OrcaRepoIcon(type="lucide", name="Palette"),
        ),
    )
    findings = diagnose(policy, OrcaSnapshot(settings=None, repos=repos), estate_root=pathlib.Path("/code"), now=NOW)
    by_key = {(f.subject, f.check): f for f in findings}

    assert by_key["repo rig", "setup script"].status is Status.FAIL
    assert by_key["repo rig", "archive script"].status is Status.FAIL
    assert by_key["repo rig", "badge color"].status is Status.FAIL
    assert by_key["repo rig", "base ref"].status is Status.OK
    assert by_key["repo rig", "icon"].status is Status.FAIL

    assert by_key["repo design-system", "external worktrees"].status is Status.FAIL
    assert by_key["repo design-system", "badge color"].status is Status.OK
    assert by_key["repo design-system", "icon"].status is Status.OK

    assert by_key["repo absent", "registered"].status is Status.FAIL


def test_icon_payload_and_matching() -> None:
    lucide_payload = build_repo_icon_payload("terminal")
    assert lucide_payload.type == "lucide"
    assert lucide_payload.name == "terminal"

    emoji_payload = build_repo_icon_payload("emoji:🚀")
    assert emoji_payload.type == "emoji"
    assert emoji_payload.emoji == "🚀"

    url_payload = build_repo_icon_payload("https://github.com/rtkelly13.png")
    assert url_payload.type == "image"
    assert url_payload.src == "https://github.com/rtkelly13.png"

    repo = _repo("test", "/path", icon=OrcaRepoIcon(type="lucide", name="palette"))
    assert icon_matches("palette", repo)
    assert icon_matches("lucide:palette", repo)
    assert not icon_matches("terminal", repo)


def _hooked_repo(path: pathlib.Path, hooks: dict[str, object], color: str = "#6366f1") -> OrcaRepo:
    return decode_repo(
        {
            "path": str(path),
            "displayName": "rig",
            "hookSettings": hooks,
            "externalWorktreeVisibility": "hide",
            "worktreeBaseRef": "main",
            "badgeColor": color,
            "repoIcon": {"type": "lucide", "name": "terminal"},
        }
    )


def _write_orca_yaml(path: pathlib.Path, setup: str) -> None:
    path.mkdir(parents=True, exist_ok=True)
    (path / "orca.yaml").write_text(f"scripts:\n  setup: |\n    {setup}\n", encoding="utf-8")


def _recording_rpc(requests: list[RpcRequest], *, ok: bool = True) -> Callable[[RpcRequest], bool]:
    def rpc(req: RpcRequest) -> bool:
        requests.append(req)
        return ok

    return rpc


def _no_cli(args: list[str]) -> str | None:
    raise AssertionError(f"unexpected orca {args}")


@pytest.mark.parametrize(
    ("yaml_setup", "hooks", "status", "detail"),
    [
        ("uv sync", {}, Status.OK, "from orca.yaml"),
        ("uv sync", {"scripts": {"setup": "uv sync"}}, Status.WARN, "overrides orca.yaml"),
        ("uv sync", {"scripts": {"setup": "old"}, "commandSourcePolicy": "local-only"}, Status.FAIL, "overrides"),
        ("uv sync", {"scripts": {"setup": "old"}, "commandSourcePolicy": "shared-only"}, Status.OK, "from orca.yaml"),
        (None, {"scripts": {"setup": "uv sync"}}, Status.WARN, "only in Orca's local settings"),
        (None, {}, Status.FAIL, "not in orca.yaml"),
        ("pnpm i", {}, Status.FAIL, 'orca.yaml has "pnpm i"'),
    ],
)
def test_setup_check_follows_orca_source_resolution(
    tmp_path: pathlib.Path, yaml_setup: str | None, hooks: dict[str, object], status: Status, detail: str
) -> None:
    repo_path = tmp_path / "rig"
    repo_path.mkdir()
    if yaml_setup is not None:
        _write_orca_yaml(repo_path, yaml_setup)
    policy = OrcaPolicy(repos={"rig": RepoPolicy(setup="uv sync")})
    snapshot = OrcaSnapshot(settings=None, repos=(_hooked_repo(repo_path, hooks),))
    findings = diagnose(policy, snapshot, estate_root=tmp_path, now=NOW)
    (finding,) = [f for f in findings if f.check == "setup script"]
    assert finding.status is status
    assert detail in finding.detail


def test_sync_dry_run_plans_without_calling_orca(tmp_path: pathlib.Path) -> None:
    repo_path = tmp_path / "rig"
    repo_path.mkdir()
    policy = OrcaPolicy(repos={"rig": RepoPolicy(setup="uv sync", color="#ec4899", base_ref="develop")})
    snapshot = OrcaSnapshot(settings=None, repos=(_hooked_repo(repo_path, {}),))
    actions = sync_repos(policy, snapshot, estate_root=tmp_path, run=_no_cli, rpc=_recording_rpc([], ok=False))
    by_field = {a.field: a for a in actions}
    assert by_field["setup script"].action == "manual"
    assert by_field["badge color"].action == "update"
    assert by_field["base ref"].action == "update"
    assert not any(a.applied for a in actions)


def test_sync_never_writes_scripts_from_policy(tmp_path: pathlib.Path) -> None:
    repo_path = tmp_path / "rig"
    repo_path.mkdir()
    policy = OrcaPolicy(repos={"rig": RepoPolicy(setup="uv sync", color="#ec4899", icon="terminal")})
    snapshot = OrcaSnapshot(settings=None, repos=(_hooked_repo(repo_path, {"scripts": {"setup": "old"}}),))
    requests: list[RpcRequest] = []
    actions = sync_repos(policy, snapshot, estate_root=tmp_path, apply=True, run=_no_cli, rpc=_recording_rpc(requests))

    (request,) = requests
    assert request.params.updates.hook_settings is None
    assert request.params.updates.badge_color == "#ec4899"
    assert request.params.updates.repo_icon is None  # icon already matches
    by_field = {a.field: a for a in actions}
    assert by_field["badge color"].applied
    assert by_field["setup script"].action == "manual"
    assert not by_field["setup script"].applied


def test_sync_clears_shadowing_local_script_and_keeps_other_hook_settings(tmp_path: pathlib.Path) -> None:
    repo_path = tmp_path / "rig"
    _write_orca_yaml(repo_path, "uv sync")
    hooks: dict[str, object] = {
        "mode": "auto",
        "setupRunPolicy": "ask",
        "setupAgentStartupPolicy": "wait-for-setup",
        "scripts": {"setup": "old", "archive": ""},
        "commandSourcePolicy": "local-only",
    }
    policy = OrcaPolicy(repos={"rig": RepoPolicy(setup="uv sync")})
    snapshot = OrcaSnapshot(settings=None, repos=(_hooked_repo(repo_path, hooks),))
    requests: list[RpcRequest] = []
    actions = sync_repos(policy, snapshot, estate_root=tmp_path, apply=True, run=_no_cli, rpc=_recording_rpc(requests))

    (request,) = requests
    sent = request.params.updates.hook_settings
    assert sent is not None
    assert sent.scripts is not None
    assert (sent.scripts.setup, sent.scripts.archive) == ("", "")
    assert sent.command_source_policy == "shared-only"
    assert (sent.mode, sent.setup_run_policy, sent.setup_agent_startup_policy) == ("auto", "ask", "wait-for-setup")
    assert request.params.updates.badge_color is None
    assert [a.applied for a in actions] == [True]


def test_sync_will_not_clear_a_local_script_orca_yaml_lacks(tmp_path: pathlib.Path) -> None:
    repo_path = tmp_path / "rig"
    _write_orca_yaml(repo_path, "uv sync")
    hooks: dict[str, object] = {"scripts": {"setup": "old", "archive": "uv cache prune"}}
    policy = OrcaPolicy(repos={"rig": RepoPolicy(setup="uv sync")})
    snapshot = OrcaSnapshot(settings=None, repos=(_hooked_repo(repo_path, hooks),))
    requests: list[RpcRequest] = []
    actions = sync_repos(policy, snapshot, estate_root=tmp_path, apply=True, run=_no_cli, rpc=_recording_rpc(requests))
    assert requests == []
    (action,) = actions
    assert action.action == "manual"
    assert "archive" in action.detail


def test_sync_reports_unreachable_orca_as_error(tmp_path: pathlib.Path) -> None:
    repo_path = tmp_path / "rig"
    repo_path.mkdir()
    policy = OrcaPolicy(repos={"rig": RepoPolicy(color="#ec4899", icon="palette")})
    snapshot = OrcaSnapshot(settings=None, repos=(_hooked_repo(repo_path, {}),))
    actions = sync_repos(
        policy, snapshot, estate_root=tmp_path, apply=True, run=_no_cli, rpc=_recording_rpc([], ok=False)
    )
    assert [(a.field, a.action, a.applied) for a in actions] == [
        ("badge color", "error", False),
        ("icon", "error", False),
    ]


def test_sync_sets_visibility_and_base_ref_through_the_cli(tmp_path: pathlib.Path) -> None:
    repo_path = tmp_path / "rig"
    repo_path.mkdir()
    policy = OrcaPolicy(repos={"rig": RepoPolicy(external_worktree_visibility="show", base_ref="develop")})
    snapshot = OrcaSnapshot(settings=None, repos=(_hooked_repo(repo_path, {}),))
    commands: list[list[str]] = []

    def run(args: list[str]) -> str | None:
        commands.append(args)
        return None if args[1] == "set-base-ref" else "{}"

    requests: list[RpcRequest] = []
    actions = sync_repos(policy, snapshot, estate_root=tmp_path, apply=True, run=run, rpc=_recording_rpc(requests))
    selector = f"path:{repo_path}"
    assert commands == [
        ["repo", "set", "--repo", selector, "--external-worktree-visibility", "show"],
        ["repo", "set-base-ref", "--repo", selector, "--ref", "develop"],
    ]
    assert requests == []
    assert [(a.field, a.applied) for a in actions] == [("external worktrees", True), ("base ref", False)]


def test_sync_registration_failure_is_an_error(tmp_path: pathlib.Path) -> None:
    (tmp_path / "blog").mkdir()
    policy = OrcaPolicy(repos={"blog": RepoPolicy(color="#14b8a6")})
    actions = sync_repos(
        policy, OrcaSnapshot(settings=None, repos=()), estate_root=tmp_path, apply=True, run=lambda _args: None
    )
    assert [(a.action, a.applied) for a in actions] == [("error", False)]


def test_sync_never_registers_a_retired_project_the_policy_still_names(tmp_path: pathlib.Path) -> None:
    (tmp_path / "the-vault-system").mkdir()
    policy = OrcaPolicy(repos={"the-vault-system": RepoPolicy(color="#14b8a6")})
    actions = sync_repos(
        policy,
        OrcaSnapshot(settings=None, repos=()),
        estate_root=tmp_path,
        apply=True,
        run=_no_cli,
        retired=frozenset({"the-vault-system"}),
    )
    assert [(a.repo, a.action, a.applied) for a in actions] == [("the-vault-system", "manual", False)]


def test_unregistered_managed_project_warns(tmp_path: pathlib.Path) -> None:
    (tmp_path / "blog" / ".git").mkdir(parents=True)
    (tmp_path / "rig" / ".git").mkdir(parents=True)
    repos = (_repo("rig", str(tmp_path / "rig")),)
    findings = diagnose(
        OrcaPolicy(),
        OrcaSnapshot(settings=None, repos=repos),
        estate_root=tmp_path,
        managed_projects=("blog", "rig", "not-cloned"),
        now=NOW,
    )
    warned = [f.subject for f in findings if f.status is Status.WARN]
    assert warned == ["repo blog"]


def _fixed(snapshot: OrcaSnapshot) -> Callable[[pathlib.Path | None], OrcaSnapshot]:
    def take(user_data: pathlib.Path | None) -> OrcaSnapshot:
        del user_data
        return snapshot

    return take


def test_doctor_cli_exits_non_zero_on_failure(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    config = tmp_path / "orca.json"
    config.write_text(json.dumps({"agent_args": {"claude": ""}}), encoding="utf-8")
    snapshot = OrcaSnapshot(settings=_settings_copy({"claude": "--dangerously-skip-permissions"}), repos=())
    monkeypatch.setattr(orca_cli, "_snapshot", _fixed(snapshot))
    result = CliRunner().invoke(app, ["orca", "doctor", "--config", str(config), "--json"])
    assert result.exit_code == 1
    rows = decode_json(list[dict[str, str]], result.stdout)
    assert ("agent claude", "fail") in {(row["subject"], row["status"]) for row in rows}


def test_sync_cli_dry_run(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    config = tmp_path / "orca.json"
    config.write_text(
        json.dumps(
            {
                "repos": {
                    "rig": {"path": str(tmp_path / "rig"), "setup": "uv sync"},
                }
            }
        ),
        encoding="utf-8",
    )
    (tmp_path / "rig" / ".git").mkdir(parents=True)
    repos = (_repo("rig", str(tmp_path / "rig"), setup="old"),)
    snapshot = OrcaSnapshot(settings=None, repos=repos, launch_args={})
    monkeypatch.setattr(orca_cli, "_snapshot", _fixed(snapshot))

    result = CliRunner().invoke(app, ["orca", "sync", "--config", str(config), "--json"])
    assert result.exit_code == 0
    actions = decode_json(list[SyncAction], result.stdout)
    assert len(actions) == 1
    assert actions[0].repo == "rig"
    assert actions[0].field == "setup script"
    assert not actions[0].applied


def test_sync_cli_exits_for_retry_when_orca_is_not_running(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    config = tmp_path / "orca.json"
    _ = config.write_text(json.dumps({"agent_args": {"claude": ""}}), encoding="utf-8")
    monkeypatch.setattr(orca_cli, "_snapshot", _fixed(OrcaSnapshot(settings=None, repos=())))

    result = CliRunner().invoke(app, ["orca", "sync", "--config", str(config)])

    assert result.exit_code == ORCA_UNAVAILABLE_EXIT
    assert "not running" in result.stderr


def test_worktree_location_is_checked_and_synced(tmp_path: pathlib.Path) -> None:
    repo_path = tmp_path / "rig"
    repo_path.mkdir()
    policy = OrcaPolicy(repos={"rig": RepoPolicy(worktree_base_path="temp")})
    snapshot = OrcaSnapshot(settings=None, repos=(_hooked_repo(repo_path, {}),))

    (finding,) = [
        f for f in diagnose(policy, snapshot, estate_root=tmp_path, now=NOW) if f.check == "worktree location"
    ]
    assert finding.status is Status.FAIL

    requests: list[RpcRequest] = []
    actions = sync_repos(policy, snapshot, estate_root=tmp_path, apply=True, run=_no_cli, rpc=_recording_rpc(requests))
    (request,) = requests
    assert request.params.updates.worktree_base_path == "temp"
    assert (request.params.updates.badge_color, request.params.updates.hook_settings) == (None, None)
    assert [(a.field, a.applied) for a in actions] == [("worktree location", True)]


def test_matching_worktree_location_needs_no_update(tmp_path: pathlib.Path) -> None:
    repo_path = tmp_path / "rig"
    repo_path.mkdir()
    raw: dict[str, object] = {"path": str(repo_path), "displayName": "rig", "worktreeBasePath": "temp"}
    policy = OrcaPolicy(repos={"rig": RepoPolicy(worktree_base_path="temp")})
    snapshot = OrcaSnapshot(settings=None, repos=(decode_repo(raw),))
    assert sync_repos(policy, snapshot, estate_root=tmp_path, run=_no_cli) == []

"""Agent default-argument sync, live settings in the doctor, and instance selection."""

from __future__ import annotations

import json
import pathlib
import sqlite3
from contextlib import closing
from datetime import UTC, datetime, timedelta
from typing import final

import pytest
from typer.testing import CliRunner

from rig.estate.orca_runtime import ORCA_UNAVAILABLE_EXIT, OrcaUnavailableError
from rig.workspace import orca_cli
from rig.workspace.cli import app
from rig.workspace.orca import (
    OrcaPolicy,
    OrcaSettings,
    OrcaSnapshot,
    SettingsCopy,
    diagnose,
    policy_repo_path,
    take_snapshot,
)
from rig.workspace.orca_agent_args import sync_agent_args
from rig.workspace.orca_rpc import USER_DATA_ENV
from rig.workspace.packages import Status

pytestmark = pytest.mark.ci

NOW = datetime(2026, 10, 24, 12, 0, tzinfo=UTC)
POLICY = OrcaPolicy(agent_args={"claude": "", "opencode": "--auto", "antigravity": "--skip"})


@final
class _FakeOrca:
    def __init__(self, have: dict[str, str], *, strips: frozenset[str] = frozenset(), down: bool = False) -> None:
        self.have: dict[str, str] = have
        self.strips: frozenset[str] = strips
        self.down: bool = down
        self.sent: list[dict[str, str]] = []

    def agent_default_args(self) -> dict[str, str]:
        if self.down:
            raise OrcaUnavailableError("Orca is not running")
        return dict(self.have)

    def set_agent_default_args(self, args: dict[str, str]) -> dict[str, str]:
        self.sent.append(args)
        self.have = {agent: "" if agent in self.strips else value for agent, value in args.items()}
        return dict(self.have)


def _write_backup(folder: pathlib.Path, settings: dict[str, str | dict[str, str]]) -> None:
    with closing(sqlite3.connect(folder / "profile-state.db.backup.1.db")) as db:
        db.execute("create table profile_state_documents (domain text primary key, payload text)")
        db.execute("insert into profile_state_documents values ('settings', ?)", (json.dumps(settings),))
        db.commit()


def _live(args: dict[str, str]) -> SettingsCopy:
    return SettingsCopy(pathlib.Path("settings.get"), NOW, OrcaSettings(agent_default_args=args), live=True)


def test_dry_run_plans_without_writing() -> None:
    client = _FakeOrca({"claude": "--yolo", "antigravity": "--skip"})
    actions = sync_agent_args(POLICY, client)
    assert [(a.repo, a.old_value, a.new_value, a.applied) for a in actions] == [
        ("agent claude", "--yolo", "", False),
        ("agent opencode", "", "--auto", False),
    ]
    assert client.sent == []


def test_apply_keeps_agents_the_policy_leaves_out() -> None:
    client = _FakeOrca({"claude": "--yolo", "codex": "--full-auto"})
    actions = sync_agent_args(POLICY, client, apply=True)
    assert all(a.applied for a in actions)
    assert client.sent == [{"claude": "", "codex": "--full-auto", "opencode": "--auto", "antigravity": "--skip"}]


def test_in_sync_sends_nothing() -> None:
    client = _FakeOrca({"claude": " ", "opencode": "--auto", "antigravity": "--skip"})
    assert sync_agent_args(POLICY, client, apply=True) == []
    assert client.sent == []


def test_a_flag_orca_strips_is_an_error() -> None:
    client = _FakeOrca({}, strips=frozenset({"opencode"}))
    actions = {a.repo: a for a in sync_agent_args(POLICY, client, apply=True)}
    assert actions["agent opencode"].action == "error"
    assert "strips" in actions["agent opencode"].detail
    assert actions["agent antigravity"].applied


def test_unreachable_orca_raises() -> None:
    with pytest.raises(OrcaUnavailableError):
        _ = sync_agent_args(POLICY, _FakeOrca({}, down=True), apply=True)


def test_doctor_reads_live_args_and_keeps_the_backup_workspace_dir(tmp_path: pathlib.Path) -> None:
    _write_backup(tmp_path, {"agentDefaultArgs": {"opencode": ""}, "workspaceDir": "~/orca/workspaces"})
    snapshot = take_snapshot(tmp_path, run=lambda _args: None, live=lambda: _live({"opencode": "--auto"}))
    assert snapshot.settings is not None
    assert snapshot.settings.live
    assert snapshot.settings.settings.workspace_dir == "~/orca/workspaces"
    findings = diagnose(OrcaPolicy(agent_args={"opencode": "--auto"}), snapshot, estate_root=tmp_path, now=NOW)
    by_check = {(f.subject, f.check): f.status for f in findings}
    assert by_check[("settings", "source")] is Status.OK
    assert by_check[("agent opencode", "default args")] is Status.OK


def test_live_settings_are_never_stale(tmp_path: pathlib.Path) -> None:
    snapshot = take_snapshot(tmp_path, run=lambda _args: None, live=lambda: _live({}))
    findings = diagnose(OrcaPolicy(), snapshot, estate_root=tmp_path, now=NOW + timedelta(days=3))
    assert [f.status for f in findings if (f.subject, f.check) == ("settings", "source")] == [Status.OK]


def test_doctor_falls_back_to_the_backup_when_orca_is_down(tmp_path: pathlib.Path) -> None:
    _write_backup(tmp_path, {"agentDefaultArgs": {"claude": ""}})
    snapshot = take_snapshot(tmp_path, run=lambda _args: None, live=lambda: None)
    assert snapshot.settings is not None
    assert not snapshot.settings.live


def test_instance_defaults_to_the_orca_that_launched_the_terminal(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv(USER_DATA_ENV, str(tmp_path / "orca-personal"))
    assert orca_cli._user_data(None) == tmp_path / "orca-personal"  # pyright: ignore[reportPrivateUsage]
    assert orca_cli._user_data(str(tmp_path / "other")) == tmp_path / "other"  # pyright: ignore[reportPrivateUsage]
    monkeypatch.delenv(USER_DATA_ENV)
    assert orca_cli._user_data(None) is None  # pyright: ignore[reportPrivateUsage]


def test_agent_sync_honours_the_runtime_metadata_override(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    override = tmp_path / "elsewhere" / "orca-runtime.json"
    monkeypatch.setenv("ORCA_RUNTIME_JSON", str(override))
    monkeypatch.delenv(USER_DATA_ENV, raising=False)
    config = tmp_path / "orca.json"
    _ = config.write_text(json.dumps({"agent_args": {"claude": ""}}), encoding="utf-8")
    monkeypatch.setattr(orca_cli, "_snapshot", lambda _user_data: OrcaSnapshot(settings=None, repos=()))

    result = CliRunner().invoke(app, ["orca", "sync", "--config", str(config)])

    assert result.exit_code == ORCA_UNAVAILABLE_EXIT
    assert str(override) in result.stderr


def test_policy_repo_paths_follow_the_machines_code_root(tmp_path: pathlib.Path) -> None:
    assert policy_repo_path("~/code/personal/blog", tmp_path) == tmp_path / "personal" / "blog"
    assert policy_repo_path("~/elsewhere/blog", tmp_path) == pathlib.Path.home() / "elsewhere" / "blog"

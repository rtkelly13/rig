"""Tests for addressing an Orca instance: its user-data folder, named-pipe transport and CLI environment."""

from __future__ import annotations

import json
import pathlib
import subprocess
from collections.abc import Iterator

import pytest

from rig.typed_json import decode_json
from rig.workspace import orca_rpc
from rig.workspace.orca import (
    CONFIG_RELATIVE_PATH,
    OrcaPolicy,
    OrcaRepo,
    OrcaSnapshot,
    RepoPolicy,
    RpcRequest,
    icon_renders,
    live_repos,
    load_policy,
    orca_profile_dir,
    sync_repos,
)
from rig.workspace.orca_rpc import (
    USER_DATA_ENV,
    OrcaRuntimeMetadata,
    RepoRpcUpdates,
    orca_runtime_metadata_path,
    run_orca,
    send_orca_rpc,
    update_repo,
)

pytestmark = pytest.mark.ci


def _metadata(*transports: tuple[str, str], token: str = "secret") -> OrcaRuntimeMetadata:
    raw = {"authToken": token, "transports": [{"kind": kind, "endpoint": endpoint} for kind, endpoint in transports]}
    return decode_json(OrcaRuntimeMetadata, json.dumps(raw))


def _send(meta: OrcaRuntimeMetadata) -> bool:
    return update_repo(
        "C:/code/personal/rig",
        RepoRpcUpdates(badge_color="#6366f1"),
        auth_token=meta.auth_token,
        rpc=lambda req: send_orca_rpc(req, metadata=meta),
    )


def test_instance_paths_come_from_its_user_data_folder(tmp_path: pathlib.Path) -> None:
    assert orca_runtime_metadata_path(tmp_path) == tmp_path / "orca-runtime.json"
    assert orca_profile_dir(tmp_path) == tmp_path / "profiles" / "local-default"


def test_repo_update_goes_over_a_named_pipe_and_skips_keepalives(monkeypatch: pytest.MonkeyPatch) -> None:
    sent: list[dict[str, object]] = []

    def fake_pipe(endpoint: str, request: bytes, timeout_s: float) -> Iterator[bytes]:
        del timeout_s
        assert endpoint == r"\\.\pipe\orca-1"
        body = decode_json(dict[str, object], request.decode())
        sent.append(body)
        yield b'{"_keepalive": true}\n'
        yield b'{"id": "someone-else", "ok": false}\n'
        yield json.dumps({"id": body["id"], "ok": True}).encode() + b"\n"

    monkeypatch.setattr(orca_rpc, "_pipe_exchange", fake_pipe)
    meta = _metadata(("websocket", "ws://127.0.0.1:6768"), ("named-pipe", r"\\.\pipe\orca-1"))
    assert _send(meta)
    (body,) = sent
    assert body["method"] == "repo.update"
    assert body["authToken"] == "secret"
    assert body["params"] == {"repo": "path:C:/code/personal/rig", "updates": {"badgeColor": "#6366f1"}}


def test_repo_update_fails_without_a_socket_or_pipe() -> None:
    assert not _send(_metadata(("websocket", "ws://127.0.0.1:6768")))


def test_repo_update_fails_when_the_pipe_closes_without_an_answer(monkeypatch: pytest.MonkeyPatch) -> None:
    def silent_pipe(endpoint: str, request: bytes, timeout_s: float) -> Iterator[bytes]:
        del endpoint, request, timeout_s
        yield b'{"_keepalive": true}\n'

    monkeypatch.setattr(orca_rpc, "_pipe_exchange", silent_pipe)
    assert not _send(_metadata(("named-pipe", r"\\.\pipe\orca-1")))


def test_run_orca_targets_the_instance_through_its_user_data_env(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    seen: list[dict[str, str] | None] = []

    def fake_run(args: list[str], **kwargs: object) -> subprocess.CompletedProcess[str]:
        env = kwargs.get("env")
        seen.append(env if isinstance(env, dict) else None)  # pyright: ignore[reportUnknownArgumentType]
        return subprocess.CompletedProcess(args, 0, stdout="ok", stderr="")

    monkeypatch.setattr(subprocess, "run", fake_run)
    assert run_orca(["repo", "list"]) == "ok"
    assert run_orca(["repo", "list"], user_data=tmp_path) == "ok"
    default_env, instance_env = seen
    assert default_env is None
    assert instance_env is not None
    assert instance_env[USER_DATA_ENV] == str(tmp_path)


def _registered(path: pathlib.Path) -> OrcaRepo:
    raw = {"path": str(path), "displayName": "rig", "badgeColor": "#737373"}
    repos = live_repos(lambda _args: json.dumps({"ok": True, "result": {"repos": [raw]}}))
    assert repos is not None
    (repo,) = repos
    return repo


def test_sync_authenticates_with_the_chosen_instances_token(tmp_path: pathlib.Path) -> None:
    user_data = tmp_path / "orca-personal"
    user_data.mkdir()
    runtime = {"authToken": "personal-token", "transports": [{"kind": "named-pipe", "endpoint": r"\\.\pipe\p"}]}
    _ = (user_data / "orca-runtime.json").write_text(json.dumps(runtime), encoding="utf-8")
    repo_path = tmp_path / "rig"
    repo_path.mkdir()
    requests: list[RpcRequest] = []

    def record(req: RpcRequest) -> bool:
        requests.append(req)
        return True

    actions = sync_repos(
        OrcaPolicy(repos={"rig": RepoPolicy(color="#6366f1")}),
        OrcaSnapshot(settings=None, repos=(_registered(repo_path),)),
        estate_root=tmp_path,
        apply=True,
        run=lambda _args: None,
        rpc=record,
        user_data=user_data,
    )
    assert [(a.field, a.applied) for a in actions] == [("badge color", True)]
    (request,) = requests
    assert request.auth_token == "personal-token"


def test_checked_in_icons_are_ones_orca_draws() -> None:
    root = pathlib.Path(__file__).resolve().parents[1]
    policy = load_policy(root / CONFIG_RELATIVE_PATH)
    bad = {name: repo.icon for name, repo in policy.repos.items() if repo.icon and not icon_renders(repo.icon)}
    assert bad == {}


def test_icon_renders_only_for_the_pickers_names_emoji_and_images() -> None:
    assert icon_renders("SquareTerminal")
    assert icon_renders("emoji:📚")
    assert icon_renders("https://github.com/x.png")
    assert not icon_renders("terminal")
    assert not icon_renders("git-compare")

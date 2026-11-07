"""Changing Orca's per-repo settings.

Visibility and base ref go through the documented `orca repo` CLI. Badge colour, icon and worktree
location have no CLI command, so they go through `repo.update`, the call Orca's own CLI makes over the
running app's local socket, authenticated with the token in `orca-runtime.json`. That call is
not a documented interface and only works while Orca is running; callers report it as failed
rather than falling back.

Each Orca instance has its own user-data folder (Electron's `userData`) holding its
`orca-runtime.json` and profiles. A second instance, such as `%APPDATA%/orca-personal`, is
addressed by passing that folder: the CLI is pointed at it through `ORCA_USER_DATA_PATH`, and
the socket or named pipe and its token come from that folder's `orca-runtime.json`.
"""

from __future__ import annotations

import os
import socket
import subprocess
import sys
import uuid
from collections.abc import Callable, Iterator
from dataclasses import dataclass
from pathlib import Path
from typing import Annotated, Literal

from pydantic import Field, TypeAdapter

from rig.typed_json import decode_json, try_decode_json
from rig.workspace.orca_hooks import HookSettings

type Visibility = Literal["show", "hide", "inherit"]
type CommandRunner = Callable[[list[str]], str | None]


USER_DATA_ENV = "ORCA_USER_DATA_PATH"


def default_user_data_dir() -> Path:
    """The default Orca instance's user-data folder on this platform."""
    if sys.platform == "darwin":
        base = Path.home() / "Library" / "Application Support"
    elif sys.platform == "win32":
        base = Path(os.environ.get("APPDATA") or Path.home() / "AppData" / "Roaming")
    else:
        base = Path(os.environ.get("XDG_CONFIG_HOME") or Path.home() / ".config")
    return base / "orca"


def orca_runtime_metadata_path(user_data: Path | None = None) -> Path:
    """An Orca instance's `orca-runtime.json`; for the default instance ``ORCA_RUNTIME_JSON`` overrides it."""
    if user_data is not None:
        return user_data / "orca-runtime.json"
    configured = os.environ.get("ORCA_RUNTIME_JSON")
    if configured:
        return Path(configured).expanduser()
    return default_user_data_dir() / "orca-runtime.json"


@dataclass(slots=True, frozen=True)
class _Transport:
    kind: str = ""
    endpoint: str = ""


@dataclass(slots=True, frozen=True)
class OrcaRuntimeMetadata:
    runtime_id: Annotated[str, Field(alias="runtimeId")] = ""
    pid: int = 0
    auth_token: Annotated[str, Field(alias="authToken")] = ""
    transports: tuple[_Transport, ...] = ()


def read_orca_runtime_metadata(path: Path | None = None) -> OrcaRuntimeMetadata | None:
    meta_path = path or orca_runtime_metadata_path()
    if not meta_path.is_file():
        return None
    try:
        return decode_json(OrcaRuntimeMetadata, meta_path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


@dataclass(slots=True, frozen=True)
class _RepoIconPayload:
    type: str = ""
    name: str | None = None
    emoji: str | None = None
    src: str | None = None
    source: str | None = None
    label: str | None = None


def build_repo_icon_payload(icon: str) -> _RepoIconPayload:
    trimmed = icon.strip()
    if trimmed.startswith("emoji:"):
        return _RepoIconPayload(type="emoji", emoji=trimmed.removeprefix("emoji:").strip())
    if trimmed.startswith("http://") or trimmed.startswith("https://"):
        source = "github" if "github.com" in trimmed else "upload"
        return _RepoIconPayload(type="image", src=trimmed, source=source)
    name = trimmed.removeprefix("lucide:").strip()
    return _RepoIconPayload(type="lucide", name=name)


@dataclass(slots=True, frozen=True)
class RepoRpcUpdates:
    """The `repo.update` fields `sync` sends. Orca merges top-level fields but replaces ``hookSettings`` whole."""

    badge_color: Annotated[str | None, Field(alias="badgeColor")] = None
    repo_icon: Annotated[_RepoIconPayload | None, Field(alias="repoIcon")] = None
    hook_settings: Annotated[HookSettings | None, Field(alias="hookSettings")] = None
    # Relative paths resolve from the repo root. "" is sent as-is and Orca stores it as unset.
    worktree_base_path: Annotated[str | None, Field(alias="worktreeBasePath")] = None


@dataclass(slots=True, frozen=True)
class RepoUpdateParams:
    repo: str
    updates: RepoRpcUpdates


@dataclass(slots=True, frozen=True)
class RpcRequest:
    id: str
    auth_token: Annotated[str, Field(alias="authToken")]
    method: str
    params: RepoUpdateParams


@dataclass(slots=True, frozen=True)
class _RpcResponse:
    id: str = ""
    ok: bool = False


_RPC_REQUEST_ADAPTER: TypeAdapter[RpcRequest] = TypeAdapter(RpcRequest)

type RpcCaller = Callable[[RpcRequest], bool]


def send_orca_rpc(
    req: RpcRequest,
    *,
    metadata: OrcaRuntimeMetadata | None = None,
    timeout_s: float = 3.0,
) -> bool:
    """Send an RPC request to the running Orca app over its Unix socket or Windows named pipe."""
    meta = metadata or read_orca_runtime_metadata()
    if meta is None:
        return False
    chosen = next(((t, ex) for t in meta.transports if (ex := _exchange_for(t)) is not None), None)
    if chosen is None:
        return False
    transport, exchange = chosen
    payload = _RPC_REQUEST_ADAPTER.dump_json(req, by_alias=True, exclude_none=True) + b"\n"
    try:
        for line in exchange(transport.endpoint, payload, timeout_s):
            resp = try_decode_json(_RpcResponse, line)
            # Keepalive frames, and answers to other requests, carry no matching id.
            if resp is not None and resp.id == req.id:
                return resp.ok
    except (OSError, ValueError):
        return False
    return False


type Exchange = Callable[[str, bytes, float], Iterator[bytes]]
"""Send one request line to an endpoint and yield the response lines as they arrive."""


def _unix_exchange(endpoint: str, request: bytes, timeout_s: float) -> Iterator[bytes]:
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as sock:
        sock.settimeout(timeout_s)
        sock.connect(endpoint)
        sock.sendall(request)
        with sock.makefile("rb") as reader:
            yield from reader


def _pipe_exchange(endpoint: str, request: bytes, timeout_s: float) -> Iterator[bytes]:
    """A Windows named pipe, which is what Orca serves on Windows. It has no read timeout here."""
    del timeout_s
    with Path(endpoint).open("r+b", buffering=0) as pipe:
        _ = pipe.write(request)
        yield from pipe


def _exchange_for(transport: _Transport) -> Exchange | None:
    if transport.kind == "unix" and Path(transport.endpoint).exists():
        return _unix_exchange
    if transport.kind == "named-pipe" and transport.endpoint:
        return _pipe_exchange
    return None


def bound_rpc(metadata: OrcaRuntimeMetadata | None) -> RpcCaller:
    """An `RpcCaller` for the Orca instance `metadata` describes; it fails when there is none."""

    def call(req: RpcRequest) -> bool:
        return metadata is not None and send_orca_rpc(req, metadata=metadata)

    return call


def run_orca(args: list[str], *, user_data: Path | None = None) -> str | None:
    """Stdout of ``orca <args>``, or None when the CLI is missing or fails.

    With ``user_data`` the CLI targets that Orca instance, through ``ORCA_USER_DATA_PATH``.
    """
    flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
    env = {**os.environ, USER_DATA_ENV: str(user_data)} if user_data is not None else None
    try:
        res = subprocess.run(
            ["orca", *args],
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
            creationflags=flags,
            env=env,
        )
    except OSError:
        return None
    return res.stdout if res.returncode == 0 else None


def orca_runner(user_data: Path | None = None) -> CommandRunner:
    """A `CommandRunner` for the Orca instance whose user-data folder is ``user_data``."""

    def run(args: list[str]) -> str | None:
        return run_orca(args, user_data=user_data)

    return run


def update_repo(
    repo_path: str,
    updates: RepoRpcUpdates,
    *,
    auth_token: str,
    rpc: RpcCaller | None = None,
) -> bool:
    """Send one `repo.update` to the running Orca. False when Orca is unreachable or refuses it."""
    req = RpcRequest(
        id=str(uuid.uuid4()),
        auth_token=auth_token,
        method="repo.update",
        params=RepoUpdateParams(repo=f"path:{repo_path}", updates=updates),
    )
    return (rpc or send_orca_rpc)(req)


def set_visibility(repo_path: str, visibility: Visibility, run: CommandRunner = run_orca) -> bool:
    """`orca repo set --external-worktree-visibility`; ``inherit`` clears the override."""
    args = ["repo", "set", "--repo", f"path:{repo_path}", "--external-worktree-visibility", visibility]
    return run(args) is not None


def set_base_ref(repo_path: str, base_ref: str, run: CommandRunner = run_orca) -> bool:
    """`orca repo set-base-ref`."""
    return run(["repo", "set-base-ref", "--repo", f"path:{repo_path}", "--ref", base_ref]) is not None

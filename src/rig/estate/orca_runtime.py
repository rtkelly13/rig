"""A client for the running Orca app's runtime API, the one its own CLI talks to.

The `orca` CLI only wraps part of it: it can register a repo but cannot list,
create or move into project groups. The runtime serves all of that
(`projectGroup.list`, `projectGroup.create`, `projectGroup.moveProject`,
`projectGroup.delete`), and
Orca's mobile app drives the same methods. Changes go through Orca, which
saves them itself, so nothing here touches its profile database.

The protocol, from stablyai/orca `src/cli/runtime/transport.ts`:

- Orca writes `orca-runtime.json` into its user-data folder at start-up. It
  holds the transports (a Unix socket, or a named pipe on Windows) and an
  auth token.
- A request is one JSON line: `{id, authToken, method, params}`.
- The answer is one JSON line with the same `id`, either `{ok: true, result}`
  or `{ok: false, error: {code, message}}`. `{_keepalive: true}` lines may
  come first.

These methods are Orca internals, not a published API. A change in a later
release shows up as `OrcaUnavailableError`, never as a guess.
"""

from __future__ import annotations

import pathlib
import socket
import sys
import uuid
from collections.abc import Callable, Iterator
from dataclasses import dataclass
from typing import Annotated

from pydantic import Field, TypeAdapter

from rig.typed_json import Lenient, decode, try_decode_json

METADATA_FILE = "orca-runtime.json"
TIMEOUT_SECONDS = 30.0


class OrcaUnavailableError(RuntimeError):
    """Orca is not running, could not be reached, or refused the request."""


#: The exit code a command gives when Orca could not be reached; the job coordinator retries it.
ORCA_UNAVAILABLE_EXIT = 3


def default_user_data() -> pathlib.Path:
    """Orca's user-data folder on this platform (Electron's `userData` for the app)."""
    if sys.platform == "darwin":
        return pathlib.Path.home() / "Library" / "Application Support" / "orca"
    if sys.platform == "win32":
        return pathlib.Path.home() / "AppData" / "Roaming" / "orca"
    return pathlib.Path.home() / ".config" / "orca"


#: On Windows the personal estate runs its own Orca profile (`--user-data-dir`) beside the default one.
ESTATE_PROFILE_FOLDER = "orca-personal"


def estate_user_data() -> pathlib.Path:
    """The Orca profile that holds the personal estate: its own folder on Windows, the default elsewhere."""
    if sys.platform == "win32":
        return default_user_data().with_name(ESTATE_PROFILE_FOLDER)
    return default_user_data()


def work_user_data() -> pathlib.Path | None:
    """The default Orca profile when it is not the estate's (Windows): estate repos do not belong there."""
    default = default_user_data()
    return default if default != estate_user_data() else None


# ---------------------------------------------------------------- wire shapes


@dataclass(frozen=True, slots=True)
class _Transport:
    kind: str
    endpoint: str


@dataclass(frozen=True, slots=True)
class _Metadata:
    auth_token: Annotated[Lenient[str], Field(alias="authToken")] = None
    transports: Lenient[tuple[_Transport, ...]] = None
    #: Written by older Orca versions instead of `transports`.
    transport: Lenient[_Transport] = None


type Params = dict[str, str | dict[str, str] | None]
"""A request's params: flat string fields, or one level of string maps (`settings.update`)."""


@dataclass(frozen=True, slots=True)
class _Request:
    id: str
    auth_token: Annotated[str, Field(serialization_alias="authToken")]
    method: str
    params: Params | None


@dataclass(frozen=True, slots=True)
class _Error:
    code: str = ""
    message: str = ""


@dataclass(frozen=True, slots=True)
class _Frame:
    id: Lenient[str] = None
    ok: Lenient[bool] = None
    error: Lenient[_Error] = None
    keepalive: Annotated[Lenient[bool], Field(alias="_keepalive")] = None
    #: Still unchecked: each method decodes it into its own shape.
    result: object = None


@dataclass(frozen=True, slots=True)
class Remote:
    canonical_key: Annotated[Lenient[str], Field(alias="canonicalKey")] = None


@dataclass(frozen=True, slots=True)
class Repo:
    """A registered project, as `repo.list` reports it."""

    id: str
    path: str
    display_name: Annotated[str, Field(alias="displayName")] = ""
    group_id: Annotated[Lenient[str], Field(alias="projectGroupId")] = None
    remote: Annotated[Lenient[Remote], Field(alias="gitRemoteIdentity")] = None


@dataclass(frozen=True, slots=True)
class Group:
    """A project group, as `projectGroup.list` reports it."""

    id: str
    name: str
    parent_id: Annotated[Lenient[str], Field(alias="parentGroupId")] = None
    #: The folder a folder-scan group was made from; None for one made by hand.
    parent_path: Annotated[Lenient[str], Field(alias="parentPath")] = None


@dataclass(frozen=True, slots=True)
class _Repos:
    repos: tuple[Repo, ...] = ()


@dataclass(frozen=True, slots=True)
class _Groups:
    groups: tuple[Group, ...] = ()


@dataclass(frozen=True, slots=True)
class _Removed:
    removed: bool = False


@dataclass(frozen=True, slots=True)
class FolderWorkspace:
    """A folder workspace, as `folderWorkspace.list` reports it. It lives in a group, and goes with it."""

    id: str
    group_id: Annotated[Lenient[str], Field(alias="projectGroupId")] = None


@dataclass(frozen=True, slots=True)
class _FolderWorkspaces:
    folder_workspaces: Annotated[tuple[FolderWorkspace, ...], Field(alias="folderWorkspaces")] = ()


@dataclass(frozen=True, slots=True)
class _Deleted:
    deleted: bool = False


@dataclass(frozen=True, slots=True)
class _OneRepo:
    repo: Repo


@dataclass(frozen=True, slots=True)
class _OneGroup:
    group: Group


@dataclass(frozen=True, slots=True)
class OrcaLiveSettings:
    """The agent-launch part of Orca's settings; the runtime does not expose the rest.

    Since Orca 1.4.255 ``agentDefaultArgs`` here is each agent's whole launch line: its permission
    mode's flags joined to the custom arguments the profile stores. `settings.update` takes lines
    in the same form and splits them again (flags to the mode, the rest to custom arguments), so a
    line is safe to send back whole; see `set_agent_default_args`.
    """

    agent_default_args: Annotated[Lenient[dict[str, str]], Field(alias="agentDefaultArgs")] = None


@dataclass(frozen=True, slots=True)
class _OneSettings:
    settings: OrcaLiveSettings


@dataclass(frozen=True, slots=True)
class Worktree:
    """A workspace, as `worktree.list` reports it: a project's main checkout or a linked worktree."""

    id: str
    path: str
    repo_id: Annotated[str, Field(alias="repoId")] = ""
    #: `refs/heads/<name>`, or empty when HEAD is detached.
    branch: Lenient[str] = None
    is_main: Annotated[Lenient[bool], Field(alias="isMainWorktree")] = None
    display_name: Annotated[Lenient[str], Field(alias="displayName")] = None
    #: Milliseconds since the epoch.
    last_activity_ms: Annotated[Lenient[float], Field(alias="lastActivityAt")] = None


@dataclass(frozen=True, slots=True)
class Agent:
    """One agent pane. Orca's states are `working`, `blocked`, `waiting` and `done`."""

    state: Lenient[str] = None


@dataclass(frozen=True, slots=True)
class Activity:
    """What `worktree.ps` reports running in one worktree."""

    worktree_id: Annotated[str, Field(alias="worktreeId")]
    live_terminals: Annotated[Lenient[int], Field(alias="liveTerminalCount")] = None
    agents: Lenient[tuple[Agent, ...]] = None


@dataclass(frozen=True, slots=True)
class _Worktrees:
    worktrees: tuple[Worktree, ...] = ()


@dataclass(frozen=True, slots=True)
class _Activities:
    worktrees: tuple[Activity, ...] = ()
    truncated: Lenient[bool] = None


_REQUEST: TypeAdapter[_Request] = TypeAdapter(_Request)


# ---------------------------------------------------------------- transport


type Exchange = Callable[[str, bytes], Iterator[bytes]]
"""Send one request line to an endpoint and yield the response lines as they arrive."""


def _unix_exchange(endpoint: str, request: bytes) -> Iterator[bytes]:
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
        connection.settimeout(TIMEOUT_SECONDS)
        connection.connect(endpoint)
        connection.sendall(request)
        with connection.makefile("rb") as reader:
            yield from reader


def _pipe_exchange(endpoint: str, request: bytes) -> Iterator[bytes]:
    with pathlib.Path(endpoint).open("r+b", buffering=0) as pipe:
        _ = pipe.write(request)
        yield from pipe


@dataclass(frozen=True, slots=True)
class OrcaRuntime:
    """One running Orca, addressed through its runtime metadata."""

    endpoint: str
    token: str
    exchange: Exchange

    @classmethod
    def connect(cls, user_data: pathlib.Path | None = None) -> OrcaRuntime:
        """Read the metadata Orca wrote at start-up. Raises when Orca is not running."""
        return cls.from_metadata((user_data or default_user_data()) / METADATA_FILE)

    @classmethod
    def from_metadata(cls, path: pathlib.Path) -> OrcaRuntime:
        """Connect through the runtime metadata file at ``path``. Raises when Orca is not running."""
        try:
            metadata = try_decode_json(_Metadata, path.read_bytes())
        except OSError as e:
            msg = f"could not read {path}; is Orca running?"
            raise OrcaUnavailableError(msg) from e
        transports = (metadata.transports or ()) if metadata else ()
        if metadata and metadata.transport:
            transports = (*transports, metadata.transport)
        transport = next((t for t in transports if t.kind in {"unix", "named-pipe"}), None)
        if metadata is None or not metadata.auth_token or transport is None:
            msg = f"{path} has no usable transport or token; restart Orca"
            raise OrcaUnavailableError(msg)
        exchange = _pipe_exchange if transport.kind == "named-pipe" else _unix_exchange
        return cls(transport.endpoint, metadata.auth_token, exchange)

    def call[T](self, kind: type[T], method: str, params: Params | None = None) -> T:
        """Send one request and decode its result as `kind`."""
        request_id = str(uuid.uuid4())
        line = _REQUEST.dump_json(_Request(request_id, self.token, method, params), by_alias=True) + b"\n"
        try:
            for frame_bytes in self.exchange(self.endpoint, line):
                frame = try_decode_json(_Frame, frame_bytes)
                if frame is None or frame.keepalive or frame.id != request_id:
                    continue
                if not frame.ok:
                    error = frame.error or _Error()
                    msg = f"Orca refused {method}: {error.code} {error.message}".strip()
                    raise OrcaUnavailableError(msg)
                return decode(kind, frame.result)
        except (OSError, ValueError) as e:
            msg = f"Orca's runtime did not answer {method}: {e}"
            raise OrcaUnavailableError(msg) from e
        msg = f"Orca's runtime closed the connection before answering {method}"
        raise OrcaUnavailableError(msg)

    # ------------------------------------------------------------ the methods used

    def repos(self) -> tuple[Repo, ...]:
        return self.call(_Repos, "repo.list").repos

    def groups(self) -> tuple[Group, ...]:
        return self.call(_Groups, "projectGroup.list").groups

    def folder_workspaces(self) -> tuple[FolderWorkspace, ...]:
        return self.call(_FolderWorkspaces, "folderWorkspace.list").folder_workspaces

    def add_repo(self, path: pathlib.Path) -> Repo:
        return self.call(_OneRepo, "repo.add", {"path": str(path)}).repo

    def create_group(self, name: str, parent_id: str | None) -> Group:
        return self.call(_OneGroup, "projectGroup.create", {"name": name, "parentGroupId": parent_id}).group

    def remove_repo(self, repo_id: str) -> None:
        """Unregister a project. Orca stops its terminals and deletes no files."""
        _ = self.call(_Removed, "repo.rm", {"repo": f"id:{repo_id}"})

    def delete_group(self, group_id: str) -> bool:
        """Delete a group and its subgroups.

        Orca moves their projects to the top level and drops their folder workspaces.
        """
        return self.call(_Deleted, "projectGroup.delete", {"groupId": group_id}).deleted

    def move_repo(self, repo_id: str, group_id: str | None) -> Repo:
        """Move a project into a group, or to the top level for None. It lands last in its new place."""
        return self.call(_OneRepo, "projectGroup.moveProject", {"repo": f"id:{repo_id}", "groupId": group_id}).repo

    def settings(self) -> OrcaLiveSettings:
        """Orca's settings as the running app holds them, not as last backed up."""
        return self.call(_OneSettings, "settings.get").settings

    def set_agent_default_args(self, lines: dict[str, str]) -> dict[str, str]:
        """Send every agent's whole launch line; returns the lines Orca then serves.

        Orca replaces the map whole, so pass every agent, not only the ones that change. Each line is
        split again: its permission flags set the agent's mode, the rest its custom arguments.
        """
        reply = self.call(_OneSettings, "settings.update", {"agentDefaultArgs": lines})
        return reply.settings.agent_default_args or {}

    def worktrees(self) -> tuple[Worktree, ...]:
        return self.call(_Worktrees, "worktree.list", {}).worktrees

    def activity(self) -> tuple[Activity, ...]:
        """Terminals and agents per worktree. Raises rather than answer from a truncated list."""
        answer = self.call(_Activities, "worktree.ps", {})
        if answer.truncated:
            msg = "Orca truncated worktree.ps; refusing to judge worktrees it did not report"
            raise OrcaUnavailableError(msg)
        return answer.worktrees

    def remove_worktree(self, worktree_id: str) -> bool:
        """Remove a worktree the way Orca's own delete does; True when Orca says it is gone.

        Orca stops its terminals and runs `git worktree remove`, which refuses a dirty tree
        because `force` is never sent. It deletes the branch too, unless git calls it unmerged.
        False means Orca accepted the request but has not finished it, so check `worktrees()`.
        """
        return self.call(_Removed, "worktree.rm", {"worktree": f"id:{worktree_id}"}).removed

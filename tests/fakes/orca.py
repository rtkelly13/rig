"""Orca's runtime for tests: `repo.list`, `worktree.list`, `worktree.ps` and `worktree.rm` over a fake pipe."""

from __future__ import annotations

import json
import pathlib
from collections.abc import Iterator
from dataclasses import dataclass, field

from pydantic import TypeAdapter

from rig.estate.orca_runtime import OrcaRuntime

type Json = dict[str, object]
DAY_MS = 86_400_000.0


@dataclass(frozen=True)
class Request:
    id: str
    method: str
    params: dict[str, str | None] | None


REQUEST = TypeAdapter(Request)


@dataclass
class FakeOrca:
    """`repo.list`, `worktree.list`, `worktree.ps` and `worktree.rm`, as Orca answers them."""

    repo: pathlib.Path
    worktrees: list[Json] = field(default_factory=list[Json])
    activity: list[Json] = field(default_factory=list[Json])
    removed: list[str] = field(default_factory=list[str])
    #: Worktree ids whose `worktree.rm` Orca accepts but does not finish: it answers
    #: `removed: false` and keeps listing them.
    stuck: set[str] = field(default_factory=set[str])
    now_ms: float = 100 * DAY_MS

    def runtime(self) -> OrcaRuntime:
        return OrcaRuntime("pipe", "token", self.exchange)

    def exchange(self, _endpoint: str, request: bytes) -> Iterator[bytes]:
        body = REQUEST.validate_json(request)
        method, params = body.method, body.params or {}
        if method == "repo.list":
            result: object = {"repos": [{"id": "r1", "path": self.repo.as_posix(), "displayName": "repo"}]}
        elif method == "worktree.list":
            result = {"worktrees": self.worktrees}
        elif method == "worktree.ps":
            result = {"worktrees": self.activity, "truncated": False}
        elif method == "worktree.rm":
            selector = str(params["worktree"]).removeprefix("id:")
            self.removed.append(selector)
            done = selector not in self.stuck
            if done:
                self.worktrees = [w for w in self.worktrees if w["id"] != selector]
            result = {"removed": done}
        else:
            raise AssertionError(method)
        yield (json.dumps({"id": body.id, "ok": True, "result": result}) + "\n").encode()

    def add(self, path: pathlib.Path, branch: str, *, idle_days: float = 10) -> str:
        worktree_id = f"r1::{path.as_posix()}"
        self.worktrees.append(
            {
                "id": worktree_id,
                "repoId": "r1",
                "path": path.as_posix(),
                "branch": f"refs/heads/{branch}" if branch else "",
                "isMainWorktree": False,
                "lastActivityAt": self.now_ms - idle_days * DAY_MS,
            }
        )
        return worktree_id

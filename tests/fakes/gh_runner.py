"""A fake of GitHub's GraphQL API for the `rig.proc.GhRunner` port, as the PR resolvers query it.

It answers the `repository(owner:, name:)` queries `rig.github_prs` builds: the paged `open:` connection
of open pull requests and `prN: pullRequest(number:)` aliases, failing per alias the way `gh` does.
`tests/contracts/test_gh_runner_contract.py` holds it to recorded answers from the real `run_gh`.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass, field

from rig.proc import GhResult, GhRunner


@dataclass
class FakeRepo:
    """One repository as GitHub would answer for it."""

    states: dict[int, str] = field(default_factory=dict[int, str])
    open_nodes: list[dict[str, object]] = field(default_factory=list[dict[str, object]])
    page_size: int = 100
    fail: str = ""


def _repo_error(message: str) -> tuple[int, str, str]:
    error = {"type": "NOT_FOUND", "path": ["repository"], "message": message}
    return 1, json.dumps({"data": {"repository": None}, "errors": [error]}), f"gh: {message}"


@dataclass
class FakeGh:
    repos: dict[str, FakeRepo]
    missing: bool = False
    queries: list[str] = field(default_factory=list[str])

    def __call__(self, query: str) -> GhResult:
        if self.missing:
            return GhResult(127, "", "gh is not on PATH")
        code, out, err = self.answer(query)
        return GhResult(code, out, err)

    def answer(self, query: str) -> tuple[int, str, str]:
        self.queries.append(query)
        match = re.search(r'repository\(owner: "([^"]+)", name: "([^"]+)"\)', query)
        assert match is not None
        repo = self.repos.get(f"{match[1]}/{match[2]}")
        if repo is None:
            return _repo_error(f"Could not resolve to a Repository with the name '{match[1]}/{match[2]}'.")
        if repo.fail:
            return _repo_error(repo.fail)
        data: dict[str, object] = {}
        errors: list[dict[str, object]] = []
        if "open: pullRequests" in query:
            after = re.search(r'after: "(\d+)"', query)
            start = int(after[1]) if after else 0
            end = start + repo.page_size
            info = {"hasNextPage": end < len(repo.open_nodes), "endCursor": str(end)}
            data["open"] = {"pageInfo": info, "nodes": repo.open_nodes[start:end]}
        for number in map(int, re.findall(r"pr(\d+): pullRequest", query)):
            state = repo.states.get(number)
            if state is None:
                data[f"pr{number}"] = None
                message = f"Could not resolve to a PullRequest with the number of {number}."
                errors.append({"type": "NOT_FOUND", "path": ["repository", f"pr{number}"], "message": message})
            else:
                data[f"pr{number}"] = {"number": number, "state": state, "merged": state == "MERGED"}
        body: dict[str, object] = {"data": {"repository": data}}
        if errors:
            body["errors"] = errors
        # Like gh: exit 1 when any alias failed, with the data still on stdout.
        stderr = f"gh: {errors[0]['message']}\n" if errors else ""
        return (1 if errors else 0), json.dumps(body), stderr


def as_port(fake: FakeGh) -> GhRunner:
    """The fake typed as the port, so a signature change to `GhRunner` breaks the fake visibly."""
    return fake

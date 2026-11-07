"""A fake of GitHub's REST API for the `rig.proc.GitHubApi` port.

Reads answer from canned JSON keyed by route; a route with nothing canned answers GitHub's 404.
Writes succeed and are logged in order, so a test asserts on exactly what would have been sent.
Distinct from `gh_graphql.FakeGitHub`, which fakes the GraphQL port.
"""

from __future__ import annotations

import json
from collections.abc import Mapping

from rig.proc import ApiRequest, Ran

NOT_FOUND = "gh: Not Found (HTTP 404)\n"


class FakeGitHubApi:
    """Answers `GitHubApi` requests: GETs from *reads*, writes into :attr:`writes`."""

    def __init__(self, reads: Mapping[str, object] | None = None) -> None:
        self.reads: dict[str, object] = dict(reads or {})
        #: ``(method, route)`` -> the stderr to fail that request with, as `gh` reports a refused call.
        self.failures: dict[tuple[str, str], str] = {}
        self.calls: list[ApiRequest] = []
        self.writes: list[ApiRequest] = []

    def __call__(self, request: ApiRequest, /) -> Ran:
        self.calls.append(request)
        failure = self.failures.get((request.method, request.route))
        if failure is not None:
            return Ran(1, "", failure)
        if request.method == "GET":
            if request.route not in self.reads:
                return Ran(1, "", NOT_FOUND)
            return Ran(0, json.dumps(self.reads[request.route]), "")
        self.writes.append(request)
        return Ran(0, "{}", "")

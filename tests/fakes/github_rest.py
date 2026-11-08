"""A fake of GitHub's REST API for the `rig.proc.GitHubApi` port.

Reads answer from JSON keyed by route; a route with nothing behind it answers GitHub's 404. Writes
succeed, are logged in order, and change what later reads return, the way GitHub would:

- ``PATCH``/``PUT`` *route* merges the body into the object at *route* (creating it), and into the
  item of the parent collection whose ``id`` or ``name`` is the route's last segment.
- ``POST`` *route* appends the body, with a fresh ``id``, to the collection at *route* and serves it
  at ``route/<id>``. With no collection there, the body becomes the object at *route* (Pages).

So a test can apply a plan and plan again: an idempotent resource plans nothing the second time.
Distinct from `gh_graphql.FakeGitHub`, which fakes the GraphQL port.
"""

from __future__ import annotations

import json
from collections.abc import Mapping
from urllib.parse import unquote

from rig.proc import ApiRequest, Ran
from rig.typed_json import is_list, is_object, load_json

NOT_FOUND = "gh: Not Found (HTTP 404)\n"
#: Collections are read a page at a time; a write names the collection without the query.
_PAGED = "?per_page=100"


class FakeGitHubApi:
    """Answers `GitHubApi` requests from :attr:`reads`, folding every write back into it."""

    def __init__(self, reads: Mapping[str, object] | None = None) -> None:
        self.reads: dict[str, object] = dict(reads or {})
        #: ``(method, route)`` -> the stderr to fail that request with, as `gh` reports a refused call.
        self.failures: dict[tuple[str, str], str] = {}
        self.calls: list[ApiRequest] = []
        self.writes: list[ApiRequest] = []
        self._next_id: int = 1000

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
        loaded = load_json(request.body or "{}")
        body: dict[object, object] = dict(loaded) if is_object(loaded) else {}
        if request.method == "POST":
            return Ran(0, json.dumps(self._create(request.route, body)), "")
        return Ran(0, json.dumps(self._merge(request.route, body)), "")

    def _collection(self, route: str) -> list[object] | None:
        for key in (route + _PAGED, route):
            found = self.reads.get(key)
            if is_list(found):
                return list(found)
        return None

    def _store_collection(self, route: str, items: list[object]) -> None:
        key = route + _PAGED if route + _PAGED in self.reads else route
        self.reads[key] = items

    def _create(self, route: str, body: dict[object, object]) -> dict[object, object]:
        items = self._collection(route)
        if items is None:
            self.reads[route] = body
            return body
        self._next_id += 1
        created: dict[object, object] = {**body, "id": self._next_id}
        self._store_collection(route, [*items, created])
        self.reads[f"{route}/{self._next_id}"] = created
        return created

    def _merge(self, route: str, body: dict[object, object]) -> dict[object, object]:
        current = self.reads.get(route)
        merged: dict[object, object] = {**(current if is_object(current) else {}), **body}
        self.reads[route] = merged
        parent, _, key = route.rpartition("/")
        items = self._collection(parent)
        if items is not None:
            self._store_collection(parent, [_merged_item(item, unquote(key), body) for item in items])
        return merged


def _merged_item(item: object, key: str, body: dict[object, object]) -> object:
    if is_object(item) and key in (str(item.get("id")), item.get("name")):
        return {**item, **body}
    return item

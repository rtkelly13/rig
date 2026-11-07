"""Which estate repos GitHub reports archived: query shape and answer parsing, with no network."""

from __future__ import annotations

import json

import pytest

from rig.estate.github_archived import archived_slugs, build_query

pytestmark = pytest.mark.ci


def test_one_aliased_lookup_per_repo_and_nothing_unsafe_is_sent() -> None:
    query = build_query(["rtkelly13/blog", "rtkelly-labs/Diffra", 'evil/x") { x } #'])
    assert query.count("repository(") == 2
    assert 'owner: "rtkelly-labs", name: "Diffra"' in query
    assert "evil" not in query


def test_archived_repos_come_back_lowercase_and_deleted_ones_are_absent() -> None:
    def graphql(query: str) -> str | None:
        assert "repository(" in query
        return json.dumps(
            {
                "data": {
                    "r0": {"nameWithOwner": "rtkelly13/The-Vault-System", "isArchived": True},
                    "r1": {"nameWithOwner": "rtkelly13/blog", "isArchived": False},
                    "r2": None,
                },
                "errors": [{"type": "NOT_FOUND"}],
            }
        )

    found = archived_slugs(["rtkelly13/the-vault-system", "rtkelly13/blog", "rtkelly13/lab"], graphql=graphql)
    assert found == frozenset({"rtkelly13/the-vault-system"})


def test_no_answer_from_github_is_none_not_an_empty_set() -> None:
    def offline(_query: str) -> str | None:
        return None

    assert archived_slugs(["rtkelly13/blog"], graphql=offline) is None
    assert archived_slugs([], graphql=offline) == frozenset()

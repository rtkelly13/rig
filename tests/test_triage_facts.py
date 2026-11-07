"""Snapshot completeness, explicit native stacks and independent GitHub action signals."""

from __future__ import annotations

import json
from dataclasses import asdict

import pytest

from rig.triage.github import Node, metadata_fields
from rig.triage.models import Item, rank
from rig.typed_json import decode_json

pytestmark = pytest.mark.ci


def test_pr_facts_keep_failures_reviews_and_native_stack_separate() -> None:
    node = decode_json(
        Node,
        json.dumps(
            {
                "number": 4,
                "title": "PR",
                "state": "OPEN",
                "createdAt": "2026-01-01T00:00:00Z",
                "updatedAt": "2026-01-01T00:00:00Z",
                "mergeStateStatus": "CLEAN",
                "author": {"login": "author"},
                "viewerDidAuthor": True,
                "labels": {"totalCount": 1, "nodes": [{"name": "bug"}]},
                "assignees": {"totalCount": 1, "nodes": [{"login": "owner"}]},
                "comments": {"totalCount": 7},
                "reviewDecision": "CHANGES_REQUESTED",
                "reviewRequests": {"totalCount": 1, "nodes": [{"requestedReviewer": {"name": "me", "isViewer": True}}]},
                "reviewThreads": {"totalCount": 2, "nodes": [{"isResolved": False}, {"isResolved": True}]},
                "commits": {
                    "nodes": [
                        {
                            "commit": {
                                "statusCheckRollup": {
                                    "state": "FAILURE",
                                    "contexts": {
                                        "totalCount": 2,
                                        "nodes": [
                                            {
                                                "name": "lint",
                                                "status": "COMPLETED",
                                                "conclusion": "FAILURE",
                                                "detailsUrl": "https://github.com/check/1",
                                            },
                                            {
                                                "context": "deploy",
                                                "state": "PENDING",
                                                "targetUrl": "https://example.com/job",
                                            },
                                        ],
                                    },
                                }
                            }
                        }
                    ]
                },
                "stack": {
                    "number": 20,
                    "size": 2,
                    "baseRefName": "main",
                    "entries": {
                        "totalCount": 2,
                        "nodes": [
                            {"position": 1, "pullRequest": {"number": 3, "title": "Base", "state": "OPEN"}},
                            {"position": 2, "pullRequest": {"number": 4, "title": "PR", "state": "OPEN"}},
                        ],
                    },
                },
                "stackEntry": {"position": 2},
            }
        ),
    )
    item = node.item("owner/repo", "pr")
    facts = item.facts
    assert facts and facts.checks_state == "FAILURE" and item.readiness == 0
    assert facts.checks[0].state == "FAILURE" and facts.checks[1].state == "PENDING"
    assert facts.review_decision == "CHANGES_REQUESTED" and facts.review_requested
    assert facts.unresolved_threads == 1 and facts.comments == 7
    assert facts.stack and facts.stack.position == 2 and facts.stack.members[0].number == 3
    assert facts.partial == []
    assert decode_json(Item, json.dumps(asdict(item))) == item
    assert rank(item) == rank(decode_json(Item, json.dumps({**asdict(item), "facts": None})))


def test_partial_metadata_never_claims_complete_lists_or_passing_checks() -> None:
    node = decode_json(
        Node,
        json.dumps(
            {
                "number": 1,
                "title": "PR",
                "state": "OPEN",
                "createdAt": "2026-01-01T00:00:00Z",
                "updatedAt": "2026-01-01T00:00:00Z",
                "reviewThreads": {"totalCount": 101, "nodes": [{"isResolved": False}]},
                "stack": {"number": 20, "size": 2, "baseRefName": "main"},
            }
        ),
    )
    facts = node.item("owner/repo", "pr").facts
    assert facts and facts.checks_state == "UNKNOWN" and facts.checks_total is None
    assert facts.partial == ["Review threads", "Stack members"]
    assert facts.unresolved_threads == 1
    assert "contexts(first:100)" not in metadata_fields("pr", detailed=False)
    assert "contexts(first:100)" in metadata_fields("pr", detailed=True)


def test_issue_facts_include_ownership_labels_and_linked_prs() -> None:
    node = decode_json(
        Node,
        json.dumps(
            {
                "number": 1,
                "title": "Bug",
                "state": "CLOSED",
                "createdAt": "2026-01-01T00:00:00Z",
                "updatedAt": "2026-01-01T00:00:00Z",
                "stateReason": "COMPLETED",
                "milestone": {"title": "v1"},
                "closedByPullRequestsReferences": {
                    "totalCount": 1,
                    "nodes": [
                        {
                            "__typename": "PullRequest",
                            "number": 2,
                            "title": "Fix",
                            "state": "MERGED",
                            "repository": {"nameWithOwner": "owner/repo"},
                        }
                    ],
                },
            }
        ),
    )
    facts = node.item("owner/repo", "issue").facts
    assert facts and facts.state_reason == "COMPLETED" and facts.milestone == "v1"
    assert facts.related[0].kind == "pr" and facts.related[0].state == "MERGED"

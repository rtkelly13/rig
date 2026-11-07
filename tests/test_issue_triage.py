"""Tests for cross-repo issue triage engine."""

import datetime

from rig.repo_governance.issue_triage import (
    IssueItem,
    TriageBucket,
    build_triage_dashboard,
    categorize_issue,
)


def test_categorize_issue():
    base_time = datetime.datetime(2026, 9, 11, 12, 0, 0, tzinfo=datetime.UTC)

    # 1. Stale needs-repro: updated > 14 days ago
    stale_issue = IssueItem(
        repo="rtkelly13/test-repo",
        number=1,
        title="Repro needed",
        url="https://github.com/rtkelly13/test-repo/issues/1",
        labels=["status: needs-repro"],
        updated_at=base_time - datetime.timedelta(days=15),
    )
    assert categorize_issue(stale_issue, now=base_time) == TriageBucket.STALE_REPRO

    # 1b. Fresh needs-repro (updated 5 days ago) - falls through to other/needs_triage
    fresh_repro = IssueItem(
        repo="rtkelly13/test-repo",
        number=2,
        title="Repro requested recently",
        url="https://github.com/rtkelly13/test-repo/issues/2",
        labels=["status: needs-repro"],
        comments_count=2,
        updated_at=base_time - datetime.timedelta(days=5),
    )
    assert categorize_issue(fresh_repro, now=base_time) == TriageBucket.OTHER

    # 2. Active bug
    bug_issue = IssueItem(
        repo="rtkelly13/test-repo",
        number=3,
        title="Crash on startup",
        url="https://github.com/rtkelly13/test-repo/issues/3",
        labels=["type: bug"],
        comments_count=1,
    )
    assert categorize_issue(bug_issue, now=base_time) == TriageBucket.ACTIVE_BUGS

    # 3. Community issue
    community_issue = IssueItem(
        repo="rtkelly13/test-repo",
        number=4,
        title="Improve docs for beginners",
        url="https://github.com/rtkelly13/test-repo/issues/4",
        labels=["good first issue"],
        comments_count=1,
    )
    assert categorize_issue(community_issue, now=base_time) == TriageBucket.COMMUNITY

    # 4. Needs triage: 0 comments, no type:* label
    unlabeled_issue = IssueItem(
        repo="rtkelly13/test-repo",
        number=5,
        title="Something feels weird",
        url="https://github.com/rtkelly13/test-repo/issues/5",
        labels=[],
        comments_count=0,
    )
    assert categorize_issue(unlabeled_issue, now=base_time) == TriageBucket.NEEDS_TRIAGE

    # 5. Non-triaged with comments or typed
    typed_feature = IssueItem(
        repo="rtkelly13/test-repo",
        number=6,
        title="Add feature X",
        url="https://github.com/rtkelly13/test-repo/issues/6",
        labels=["type: feature"],
        comments_count=0,
    )
    assert categorize_issue(typed_feature, now=base_time) == TriageBucket.OTHER


def test_build_triage_dashboard():
    base_time = datetime.datetime(2026, 9, 11, 12, 0, 0, tzinfo=datetime.UTC)
    issues = [
        IssueItem(
            repo="rtkelly13/repo-a",
            number=1,
            title="Uncategorized question",
            url="url1",
            comments_count=0,
            labels=[],
        ),
        IssueItem(
            repo="rtkelly13/repo-a",
            number=2,
            title="NullRef bug",
            url="url2",
            labels=["type: bug"],
        ),
        IssueItem(
            repo="rtkelly13/repo-b",
            number=1,
            title="Starter task",
            url="url3",
            labels=["help wanted"],
        ),
        IssueItem(
            repo="rtkelly13/repo-b",
            number=2,
            title="Waiting on repro forever",
            url="url4",
            labels=["status: needs-repro"],
            updated_at=base_time - datetime.timedelta(days=20),
        ),
    ]

    dashboard = build_triage_dashboard(issues, now=base_time)
    assert dashboard.total_issues == 4
    assert len(dashboard.needs_triage) == 1
    assert dashboard.needs_triage[0].number == 1
    assert len(dashboard.active_bugs) == 1
    assert dashboard.active_bugs[0].number == 2
    assert len(dashboard.community) == 1
    assert dashboard.community[0].number == 1
    assert len(dashboard.stale_repro) == 1
    assert dashboard.stale_repro[0].number == 2

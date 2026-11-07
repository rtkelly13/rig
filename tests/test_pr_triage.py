"""Tests for multi-repo PR triage and automation engine."""

import datetime

from rig.repo_governance.pr_triage import (
    PRBucket,
    PRItem,
    build_pr_triage_dashboard,
    categorize_pr,
)


def test_categorize_pr():
    base_time = datetime.datetime(2026, 9, 11, 12, 0, 0, tzinfo=datetime.UTC)

    # 1. Draft PR is blocked
    draft_pr = PRItem(
        repo="rtkelly13/shared-utilities",
        number=1,
        title="WIP changes",
        url="url1",
        head_branch="feature",
        is_draft=True,
    )
    assert categorize_pr(draft_pr, now=base_time) == PRBucket.BLOCKED

    # 2. Failing check is blocked
    failing_pr = PRItem(
        repo="rtkelly13/shared-utilities",
        number=2,
        title="Broken PR",
        url="url2",
        head_branch="fix",
        failed_checks=["ci"],
    )
    assert categorize_pr(failing_pr, now=base_time) == PRBucket.BLOCKED

    # 3. Missing required check is blocked
    missing_check_pr = PRItem(
        repo="rtkelly13/shared-utilities",
        number=3,
        title="Incomplete checks",
        url="url3",
        head_branch="chore",
        missing_required_checks=["ci"],
    )
    assert categorize_pr(missing_check_pr, now=base_time) == PRBucket.BLOCKED

    # 4. Conflicting PR is blocked
    conflict_pr = PRItem(
        repo="rtkelly13/shared-utilities",
        number=4,
        title="Merge conflict",
        url="url4",
        head_branch="conflict",
        mergeable="CONFLICTING",
    )
    assert categorize_pr(conflict_pr, now=base_time) == PRBucket.BLOCKED

    # 5. Pending checks is pending
    pending_pr = PRItem(
        repo="rtkelly13/shared-utilities",
        number=5,
        title="In-flight CI",
        url="url5",
        head_branch="feature",
        pending_checks=["ci"],
    )
    assert categorize_pr(pending_pr, now=base_time) == PRBucket.PENDING

    # 6. Behind main is behind
    behind_pr = PRItem(
        repo="rtkelly13/shared-utilities",
        number=6,
        title="Needs rebase/sync",
        url="url6",
        head_branch="feature",
        merge_state_status="BEHIND",
        passed_checks=["ci"],
    )
    assert categorize_pr(behind_pr, now=base_time) == PRBucket.BEHIND

    # 7. Clean and passed is ready
    ready_pr = PRItem(
        repo="rtkelly13/shared-utilities",
        number=7,
        title="Ready to merge",
        url="url7",
        head_branch="feature",
        merge_state_status="CLEAN",
        passed_checks=["ci"],
    )
    assert categorize_pr(ready_pr, now=base_time) == PRBucket.READY

    # 8. Stale (>14 days)
    stale_pr = PRItem(
        repo="rtkelly13/shared-utilities",
        number=8,
        title="Old abandoned PR",
        url="url8",
        head_branch="old",
        merge_state_status="UNKNOWN",
        updated_at=base_time - datetime.timedelta(days=20),
    )
    assert categorize_pr(stale_pr, now=base_time) == PRBucket.STALE


def test_build_pr_triage_dashboard():
    base_time = datetime.datetime(2026, 9, 11, 12, 0, 0, tzinfo=datetime.UTC)
    prs = [
        PRItem(
            repo="rtkelly13/repo-a",
            number=1,
            title="PR 1 Ready",
            url="url1",
            head_branch="b1",
            merge_state_status="CLEAN",
            passed_checks=["ci"],
        ),
        PRItem(
            repo="rtkelly13/repo-a",
            number=2,
            title="PR 2 Behind",
            url="url2",
            head_branch="b2",
            merge_state_status="BEHIND",
        ),
        PRItem(
            repo="rtkelly13/repo-b",
            number=3,
            title="PR 3 Blocked",
            url="url3",
            head_branch="b3",
            failed_checks=["test"],
        ),
        PRItem(
            repo="rtkelly13/repo-b",
            number=4,
            title="PR 4 Pending",
            url="url4",
            head_branch="b4",
            pending_checks=["build"],
        ),
        PRItem(
            repo="rtkelly13/repo-b",
            number=5,
            title="PR 5 Stale",
            url="url5",
            head_branch="b5",
            merge_state_status="UNKNOWN",
            updated_at=base_time - datetime.timedelta(days=30),
        ),
    ]

    dashboard = build_pr_triage_dashboard(prs, now=base_time)
    assert dashboard.total_prs == 5
    assert len(dashboard.ready) == 1
    assert len(dashboard.behind) == 1
    assert len(dashboard.blocked) == 1
    assert len(dashboard.pending) == 1
    assert len(dashboard.stale) == 1

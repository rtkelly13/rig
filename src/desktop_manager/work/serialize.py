r"""The `work list --json` schema. Consumers depend on it: add fields freely, rename or remove only with a version bump.

Times are ISO 8601 with an offset, and `null` means unknown or not applicable. An annotated payload
(jsonc; one item, every optional part present):

    {
      "schema_version": 1,
      "generated_at": "2026-10-10T12:00:00+00:00",
      "code_root": "C:\\code",
      "warnings": ["owner/name: gh lookup failed (offline, unauthenticated or no access)"],
      "items": [
        {
          "key": "acme/data-platform:proj-3583-hub-fixes",  // "<repo>:<branch>", or "unattached"
          "repo": "acme/data-platform",       // clone path relative to code_root; null for unattached
          "repo_path": "C:\\code\\acme\\data-platform",
          "branch": "proj-3583-hub-fixes",       // "(detached)" for a detached HEAD; null for unattached
          "ticket": "PROJ-3583",                 // null when the branch names none
          "is_trunk": false,
          "state": "active",                     // active | merged | closed | stale | trunk | unattached
          "reasons": ["draft PR #812", "1 running session"],
          "last_activity": "2026-10-10T11:58:00+00:00",
          "dirty": false,                        // any checkout dirty
          "unpushed": 4,                         // = branch_ref.unpushed, 0 without a branch_ref
          "checkouts": [
            {"path": "C:\\code\\acme\\data-platform\\.worktrees\\hub", "is_main_checkout": false, "dirty": false}
          ],                                     // dirty is null when status was skipped or failed
          "branch_ref": {                        // null when the branch has no local ref (or detached)
            "upstream": "origin/proj-3583-hub-fixes",  // null without an upstream
            "upstream_gone": false,
            "ahead": 0,                          // null without an upstream, or when it is gone
            "behind": 2,
            "unpushed": 4,                       // commits reachable from the tip that are on no remote
            "last_commit": "2026-10-10T10:00:00+01:00"
          },
          "pr": {"number": 812, "state": "draft", "url": "https://github.com/...", "updated_at": "..."},
                                                 // state: open | draft | merged | closed; pr is null when none
          "pr_lookup": "ok",                     // ok | skipped (trunk, detached, --offline, not asked) | unavailable
          "sessions": [
            {
              "session_id": "0b1c...",
              "title": "Fix hub partitions",
              // state: live | waiting-on-user | died-mid-task | died-idle | ended-clean | cleared
              "state": "waiting-on-user",
              "last_activity": "2026-10-10T11:58:00+00:00",
              "last_cwd": "C:\\code\\acme\\data-platform\\.worktrees\\hub",
              "start_cwd": "C:\\code\\acme\\data-platform\\.worktrees\\hub",
              "prompt_count": 14,
              "pid": 12345                       // null unless running
            }
          ]                                      // newest first
        }
      ]
    }

docs/desktop-manager/work-items.md explains the states, tickets and filtering.
"""

from __future__ import annotations

import datetime as dt
from collections.abc import Sequence

from desktop_manager.work.model import BranchRef, Checkout, PrInfo, SessionFacts, WorkItem, WorkReport
from rig.typed_json import JsonObject, JsonValue

SCHEMA_VERSION = 1


def _when(moment: dt.datetime | None) -> str | None:
    return moment.isoformat() if moment else None


def _checkout(checkout: Checkout) -> JsonObject:
    return {
        "path": str(checkout.path),
        "is_main_checkout": checkout.is_main_checkout,
        "dirty": checkout.dirty,
    }


def _branch(ref: BranchRef | None) -> JsonObject | None:
    if ref is None:
        return None
    return {
        "upstream": ref.upstream or None,
        "upstream_gone": ref.upstream_gone,
        "ahead": ref.ahead,
        "behind": ref.behind,
        "unpushed": ref.unpushed,
        "last_commit": _when(ref.last_commit),
    }


def _pr(pr: PrInfo | None) -> JsonObject | None:
    if pr is None:
        return None
    return {"number": pr.number, "state": pr.status.value, "url": pr.url, "updated_at": _when(pr.updated_at)}


def _session(session: SessionFacts) -> JsonObject:
    return {
        "session_id": session.session_id,
        "title": session.title,
        "state": session.state.value,
        "last_activity": _when(session.last_activity),
        "last_cwd": session.last_cwd,
        "start_cwd": session.start_cwd,
        "prompt_count": session.prompt_count,
        "pid": session.pid,
    }


def item_json(item: WorkItem) -> JsonObject:
    checkouts: list[JsonValue] = [_checkout(c) for c in item.checkouts]
    sessions: list[JsonValue] = [_session(s) for s in item.sessions]
    reasons: list[JsonValue] = list(item.reasons)
    return {
        "key": item.key,
        "repo": item.repo,
        "repo_path": str(item.repo_path) if item.repo_path else None,
        "branch": item.branch,
        "ticket": item.ticket,
        "is_trunk": item.is_trunk,
        "state": item.state.value,
        "reasons": reasons,
        "last_activity": _when(item.last_activity),
        "dirty": item.dirty,
        "unpushed": item.unpushed,
        "checkouts": checkouts,
        "branch_ref": _branch(item.branch_ref),
        "pr": _pr(item.pr),
        "pr_lookup": item.pr_lookup.value,
        "sessions": sessions,
    }


def report_json(report: WorkReport, items: Sequence[WorkItem]) -> JsonObject:
    """The whole payload. *items* is the filtered view of `report.items`."""
    rendered: list[JsonValue] = [item_json(i) for i in items]
    warnings: list[JsonValue] = list(report.warnings)
    return {
        "schema_version": SCHEMA_VERSION,
        "generated_at": report.generated_at.isoformat(),
        "code_root": str(report.code_root),
        "warnings": warnings,
        "items": rendered,
    }

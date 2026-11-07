"""Whether governance touches a repo at all: the selection rules and the pure decision.

A leaf module, so `policy_config` can re-export it without an import cycle.
"""

from __future__ import annotations

from dataclasses import dataclass

__all__ = ("ManageDecision", "RepoSelectionRules", "should_manage_repo")


@dataclass(frozen=True)
class RepoSelectionRules:
    """Whether governance should touch a repo at all (opt out of forks/unowned, or a whole profile via `exempt`)."""

    manage_forks: bool = False
    require_push_access: bool = True
    exempt: bool = False


@dataclass(frozen=True)
class ManageDecision:
    """Whether a repo is in scope, and why not when it is not."""

    managed: bool
    reason: str = ""


def should_manage_repo(
    *,
    rules: RepoSelectionRules,
    registered: bool,
    is_fork: bool,
    permission: str,
    push_permissions: frozenset[str] = frozenset({"ADMIN", "MAINTAIN", "WRITE"}),
) -> ManageDecision:
    """Decide whether governance applies. Pure — all facts are supplied.

    An explicit profile entry wins over every other rule, including one marking its repos exempt:
    listing a repo is a deliberate act, and second-guessing it would make the config a suggestion.
    """
    if registered:
        reason = "exempt (its profile opts out of governance)" if rules.exempt else ""
        return ManageDecision(not rules.exempt, reason)
    if is_fork and not rules.manage_forks:
        return ManageDecision(False, "fork (add it to a profile to manage it)")
    if rules.require_push_access and permission.upper() not in push_permissions:
        shown = permission or "unknown"
        return ManageDecision(False, f"no push access ({shown.lower()})")
    return ManageDecision(True)

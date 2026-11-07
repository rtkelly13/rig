"""The policy type for managed forks. See docs/forks.md.

Kept apart from `policy_config` so that module stays under the ratchet's length limit; it imports
and re-exports it.
"""

from __future__ import annotations

from dataclasses import dataclass

__all__ = ("ForkSpec",)


@dataclass(frozen=True)
class ForkSpec:
    """A repo that tracks an upstream it does not own. See docs/forks.md.

    The fork is upstream plus a linear stack of recorded changes, run by Oxbow
    (rtkelly-labs/oxbow): it rebaselines the stack, keeps the change records and promotes releases.
    Rig only governs the repository around it, so all it needs to know is what the fork follows.

    Because the stack tool rewrites the fork's default branch on every rebaseline, a fork's ruleset
    keeps deletion protection, linear history and required checks, but neither forbids
    non-fast-forward pushes nor requires a pull request there (`github_rulesets`).
    """

    upstream: str
    upstream_branch: str = "main"

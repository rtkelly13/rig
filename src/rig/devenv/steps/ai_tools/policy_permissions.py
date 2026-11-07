"""Project the estate command policy into an agent's native permission file.

devenv used to carry its own permission baselines, which drifted from
``config/cli-allowlists.json`` (ADR 0015). It now runs the same projection as
``workspace policy sync`` so the policy file is the only source of command rules.
"""

from __future__ import annotations

from pathlib import Path

from rich.console import Console

from rig.policy.engine import load_policy
from rig.policy.models import ProjectionResult, ProjectionStatus
from rig.policy.projections import get_native_projection

console = Console()


def project_policy_permissions(system: str, home: Path | None = None, dry_run: bool = False) -> ProjectionResult:
    """Apply the checked-in policy to one agent surface and report the outcome."""
    result = get_native_projection(system, home).apply(load_policy(), dry_run=dry_run)
    ok = result.status in {ProjectionStatus.APPLIED, ProjectionStatus.UNCHANGED, ProjectionStatus.DRY_RUN}
    style = "green" if ok else "yellow"
    console.print(f"[{style}]{system} policy permissions: {result.status.value}[/{style}] — {result.detail}")
    return result

"""Project the estate command policy into an agent's native permission file.

devenv used to carry its own permission baselines, which drifted from
``config/cli-allowlists.json`` (ADR 0015). It now runs ``rig policy sync`` for the one
surface, so the policy file is the only source of command rules and C# the only projector.
"""

from __future__ import annotations

from pathlib import Path

from rig import csharp_cli


def project_policy_permissions(system: str, home: Path | None = None, dry_run: bool = False) -> int:
    """Apply the checked-in policy to one agent surface under *home* (default: yours); return the exit code."""
    target = home or Path.home()
    return csharp_cli.run(
        ["policy", "sync", "--system", system, "--home", str(target), *(["--dry-run"] if dry_run else [])]
    )

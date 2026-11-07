"""GitHub Pages and custom domain governance manager.

Audits and enforces GitHub Pages configuration, build types (workflow vs legacy),
custom domain (CNAME) assignments, and HTTPS enforcement via the GitHub API.
"""

from __future__ import annotations

from dataclasses import dataclass

from pydantic import ValidationError

from rig import proc
from rig.proc import ProcError
from rig.repo_governance.policy_config import PagesPolicy
from rig.typed_json import decode_json

__all__ = [
    "align_pages",
    "inspect_pages",
]


@dataclass(frozen=True, slots=True)
class PagesInspection:
    """A repo's GitHub Pages configuration against its policy."""

    enabled: bool
    cname: str | None
    build_type: str | None
    https_enforced: bool
    status: str | None
    html_url: str | None
    is_aligned: bool
    drift_reasons: tuple[str, ...]
    error: str | None = None


@dataclass(frozen=True, slots=True)
class PagesAlignResult:
    """Outcome of aligning a repo's GitHub Pages with its policy."""

    status: str
    repo: str
    details: str | None = None
    action: str | None = None
    error: str | None = None
    actions: tuple[str, ...] = ()
    actions_taken: tuple[str, ...] = ()
    drift_reasons: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class _Pages:
    cname: str | None = None
    build_type: str | None = None
    https_enforced: bool = False
    status: str | None = None
    html_url: str | None = None


def inspect_pages(repo_name: str, expected: PagesPolicy | None = None) -> PagesInspection:
    """Inspects the current GitHub Pages configuration for *repo_name* via gh API.

    Returns a status dict detailing whether Pages is enabled, the build type,
    assigned custom domain, and whether HTTPS is enforced.
    """
    try:
        res = proc.gh("api", f"repos/{repo_name}/pages", check=True)
        pages = decode_json(_Pages, res.out)

        cname = pages.cname
        build_type = pages.build_type
        https_enforced = pages.https_enforced
        status = pages.status
        html_url = pages.html_url

        is_aligned = True
        drift_reasons: list[str] = []

        if expected and expected.enabled:
            if build_type != expected.build_type:
                is_aligned = False
                drift_reasons.append(f"build_type is '{build_type}', expected '{expected.build_type}'")
            if expected.cname and cname != expected.cname:
                is_aligned = False
                drift_reasons.append(f"cname is '{cname}', expected '{expected.cname}'")
            if expected.https_enforced and not https_enforced:
                is_aligned = False
                drift_reasons.append("https_enforced is False, expected True")
        elif expected and not expected.enabled:
            is_aligned = False
            drift_reasons.append("Pages is enabled, expected disabled")

        return PagesInspection(
            enabled=True,
            cname=cname,
            build_type=build_type,
            https_enforced=https_enforced,
            status=status,
            html_url=html_url,
            is_aligned=is_aligned,
            drift_reasons=tuple(drift_reasons),
        )

    except ProcError as e:
        # 404 indicates Pages is disabled on this repo
        is_disabled = "404" in e.ran.err or "Not Found" in e.ran.err
        is_aligned = expected is not None and not expected.enabled if is_disabled else False
        drift = ["Pages is disabled"] if expected and expected.enabled else []

        return PagesInspection(
            enabled=False,
            cname=None,
            build_type=None,
            https_enforced=False,
            status="disabled",
            html_url=None,
            is_aligned=is_aligned,
            drift_reasons=tuple(drift),
            error=None if is_disabled else e.ran.err.strip(),
        )
    except ValidationError as e:
        return PagesInspection(
            enabled=False,
            cname=None,
            build_type=None,
            https_enforced=False,
            status="error",
            html_url=None,
            is_aligned=False,
            drift_reasons=(str(e),),
            error=str(e),
        )


def align_pages(repo_name: str, policy: PagesPolicy, dry_run: bool = False) -> PagesAlignResult:
    """Applies GitHub Pages configuration according to *policy*.

    - Enables Pages with build_type='workflow' if not currently enabled.
    - Configures custom domain CNAME and enforces HTTPS.
    """
    current = inspect_pages(repo_name, expected=policy)

    if current.is_aligned:
        return PagesAlignResult(status="aligned", repo=repo_name, details="Pages already aligned with policy")

    planned_actions: list[str] = []

    # 1. Enable Pages if disabled
    if policy.enabled and not current.enabled:
        enable_cmd = [
            "gh",
            "api",
            "--method",
            "POST",
            f"repos/{repo_name}/pages",
            "-H",
            "Accept: application/vnd.github+json",
            "-f",
            f"build_type={policy.build_type}",
        ]
        planned_actions.append(" ".join(enable_cmd))

        if not dry_run:
            enabled = proc.gh(*enable_cmd[1:])
            if not enabled.ok:
                return PagesAlignResult(
                    status="error", action="enable_pages", repo=repo_name, error=enabled.err.strip()
                )

    # 2. Update CNAME / HTTPS if needed
    if policy.enabled and (policy.cname or policy.https_enforced):
        put_cmd = [
            "gh",
            "api",
            "--method",
            "PUT",
            f"repos/{repo_name}/pages",
            "-H",
            "Accept: application/vnd.github+json",
        ]
        if policy.cname:
            put_cmd.extend(["-f", f"cname={policy.cname}"])
        if policy.https_enforced:
            put_cmd.extend(["-F", "https_enforced=true"])

        planned_actions.append(" ".join(put_cmd))

        if not dry_run:
            configured = proc.gh(*put_cmd[1:])
            if not configured.ok:
                return PagesAlignResult(
                    status="error", action="configure_pages_cname", repo=repo_name, error=configured.err.strip()
                )

    if dry_run:
        return PagesAlignResult(
            status="dry_run", repo=repo_name, actions=tuple(planned_actions), drift_reasons=current.drift_reasons
        )

    return PagesAlignResult(status="success", repo=repo_name, actions_taken=tuple(planned_actions))

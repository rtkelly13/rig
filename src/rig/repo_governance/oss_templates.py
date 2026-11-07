"""Community and open source template synchronization engine.

Deploys and audits GitHub Issue Forms (.github/ISSUE_TEMPLATE/*.yml), Pull Request templates,
CONTRIBUTING.md, and CODE_OF_CONDUCT.md (Contributor Covenant v2.1).
"""

from __future__ import annotations

from pathlib import Path
from typing import TypedDict

from jinja2 import Environment, FileSystemLoader, select_autoescape
from pydantic import BaseModel, Field

from rig.repo_governance.policy_config import load_policy_config
from rig.repo_governance.policy_key import policy_key
from rig.repo_governance.sync_outcome import SyncOutcome
from rig.repo_slug import RepoSlug

TEMPLATES_DIR = Path(__file__).parent / "templates" / "oss"

#: Template file -> path it renders to in the target repo.
TEMPLATE_MAPPINGS: dict[str, str] = {
    "bug_report.yml.jinja": ".github/ISSUE_TEMPLATE/bug_report.yml",
    "feature_request.yml.jinja": ".github/ISSUE_TEMPLATE/feature_request.yml",
    "config.yml.jinja": ".github/ISSUE_TEMPLATE/config.yml",
    "PULL_REQUEST_TEMPLATE.md.jinja": ".github/PULL_REQUEST_TEMPLATE.md",
    "CONTRIBUTING.md.jinja": "CONTRIBUTING.md",
    "CODE_OF_CONDUCT.md.jinja": "CODE_OF_CONDUCT.md",
}


class OSSTemplateAudit(BaseModel):
    repo_name: str
    target_rel_path: str
    exists: bool
    is_aligned: bool
    details: str | None = None


class OSSTemplatesAuditResult(BaseModel):
    repo_name: str
    total_templates: int
    missing: list[str] = Field(default_factory=list[str])
    aligned: list[str] = Field(default_factory=list[str])
    audits: list[OSSTemplateAudit] = Field(default_factory=list[OSSTemplateAudit])

    @property
    def is_fully_aligned(self) -> bool:
        return len(self.missing) == 0


def get_jinja_env() -> Environment:
    """Creates a Jinja2 environment configured for OSS templates."""
    return Environment(
        loader=FileSystemLoader(str(TEMPLATES_DIR)),
        autoescape=select_autoescape(),
        trim_blocks=True,
        lstrip_blocks=True,
    )


def inspect_oss_templates(repo_path: Path) -> OSSTemplatesAuditResult:
    """Audits a repository for presence of standard community and contribution templates."""
    repo_name = repo_path.name
    audits: list[OSSTemplateAudit] = []
    missing: list[str] = []
    aligned: list[str] = []

    for target_rel in TEMPLATE_MAPPINGS.values():
        file_path = repo_path / target_rel
        exists = file_path.exists()
        if exists:
            aligned.append(target_rel)
            audits.append(
                OSSTemplateAudit(
                    repo_name=repo_name,
                    target_rel_path=target_rel,
                    exists=True,
                    is_aligned=True,
                )
            )
        else:
            missing.append(target_rel)
            audits.append(
                OSSTemplateAudit(
                    repo_name=repo_name,
                    target_rel_path=target_rel,
                    exists=False,
                    is_aligned=False,
                    details=f"Missing template: {target_rel}",
                )
            )

    return OSSTemplatesAuditResult(
        repo_name=repo_name,
        total_templates=len(TEMPLATE_MAPPINGS),
        missing=missing,
        aligned=aligned,
        audits=audits,
    )


class OssTemplateContext(TypedDict, total=False):
    """The values the OSS templates render from; any left out are filled from policy."""

    repo_name: str
    repo_full_name: str
    contact_email: str


def sync_oss_templates(
    repo_path: Path,
    context: OssTemplateContext | None = None,
    dry_run: bool = False,
) -> SyncOutcome:
    """Renders and writes all standard OSS templates to the target repository."""
    cfg = load_policy_config()
    ctx: OssTemplateContext = context or {}
    key = policy_key(repo_path)
    slug = RepoSlug.from_path(key)
    name = slug.name if slug is not None else repo_path.name
    owner = slug.owner if slug is not None else cfg.expected_owner(key)
    ctx.setdefault("repo_name", name)
    ctx.setdefault("repo_full_name", f"{owner}/{name}" if owner else name)
    ctx.setdefault("contact_email", cfg.contact_email(key))

    env = get_jinja_env()
    changes: list[str] = []
    any_changed = False

    for tmpl_file, target_rel in TEMPLATE_MAPPINGS.items():
        template = env.get_template(tmpl_file)
        rendered = template.render(**ctx)
        target_path = repo_path / target_rel

        # Check if identical content exists
        if target_path.exists():
            existing_content = target_path.read_text(encoding="utf-8", errors="replace")
            if existing_content.strip() == rendered.strip():
                continue

        any_changed = True
        if dry_run:
            changes.append(f"[Dry Run] Would write {target_rel}")
        else:
            target_path.parent.mkdir(parents=True, exist_ok=True)
            target_path.write_text(rendered.strip() + "\n", encoding="utf-8")
            changes.append(f"Wrote {target_rel}")

    return SyncOutcome(changed=any_changed, messages=changes)

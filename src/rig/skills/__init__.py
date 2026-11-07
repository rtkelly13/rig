"""Cross-agent skill symlink synchronization and auditing machinery."""

from rig.skills.cli import skills_cli
from rig.skills.engine import (
    activate_skills,
    audit_skill_symlinks,
    deactivate_skills,
    discover_all_skills,
    get_canonical_repo_skills_dir,
    get_system_directory,
    load_skill_activation_state,
    prune_broken_skill_symlinks,
    sync_skill_symlinks,
)
from rig.skills.models import (
    CLI_SYSTEM_DISPLAY_NAMES,
    DEFAULT_PRIMARY_SYSTEMS,
    CliSystem,
    SkillManifest,
    SkillsAuditReport,
    SystemSkillStatus,
)
from rig.skills.vendor import (
    DEFAULT_UPSTREAM_VENDORS,
    UpstreamVendor,
    clone_or_update_vendor,
    extract_vendor_skills,
    get_default_external_clones_dir,
    get_default_vendored_skills_dir,
    get_default_vendors_config_file,
    load_upstream_vendors,
    update_all_vendors,
)

__all__ = [
    "CLI_SYSTEM_DISPLAY_NAMES",
    "DEFAULT_PRIMARY_SYSTEMS",
    "DEFAULT_UPSTREAM_VENDORS",
    "UpstreamVendor",
    "clone_or_update_vendor",
    "extract_vendor_skills",
    "get_default_external_clones_dir",
    "get_default_vendored_skills_dir",
    "get_default_vendors_config_file",
    "load_upstream_vendors",
    "update_all_vendors",
    "CliSystem",
    "SkillManifest",
    "SkillsAuditReport",
    "SystemSkillStatus",
    "audit_skill_symlinks",
    "activate_skills",
    "deactivate_skills",
    "discover_all_skills",
    "get_canonical_repo_skills_dir",
    "get_system_directory",
    "load_skill_activation_state",
    "prune_broken_skill_symlinks",
    "skills_cli",
    "sync_skill_symlinks",
]

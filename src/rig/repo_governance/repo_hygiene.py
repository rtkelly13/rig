from __future__ import annotations

import pathlib
import re
from dataclasses import dataclass
from typing import TYPE_CHECKING

from pydantic import Field

from rig.repo_governance.policy_key import policy_key
from rig.typed_yaml import try_decode_yaml

if TYPE_CHECKING:
    from rig.repo_governance.policy_config import PolicyConfig


EXPECTED_AGENTS_CONVENTIONS = """## 🛑 Repository Conventions & Workflow Policy

1. **Squash Merge Only**: All pull requests must be merged into `main` using **Squash and Merge** exclusively.
2. **Delete Branch on Merge**: Feature branches must be automatically deleted immediately upon merge into `main`.
3. **Linear History**: Maintain a strictly linear history. Rebase feature branches onto `main` before merging.
4. **Direct Push Protection**: Direct pushes to `main` are blocked; PR mechanism required (force push allowed).
5. **Local Temp & Worktree Directory**: All temporary files, databases, and worktrees go in `/temp/` (gitignored).
6. **Gitignored Local TODO File**: A root `TODO.md` file MUST exist for local task tracking and be gitignored.
7. **Auto-Merge Enabled**: PRs may enable auto-merge (squash) so they merge automatically once required checks pass.
8. **Pinned Action SHAs**: All GitHub Actions workflows MUST use 40-character commit SHAs instead of mutable tags.
9. **Script Language Policy**: Automation scripts in `scripts/` MUST use the repository's approved
   language stack (e.g., C# `.cs` for .NET, TypeScript/JavaScript for Node, Python `.py` for Python).
   Alien scripting runtimes (e.g., `.py` in C# repos, `.sh`/`.ps1` everywhere) are prohibited.
"""

#: The heading that marks the governance-owned conventions block.
AGENTS_CONVENTIONS_HEADING = "## 🛑 Repository Conventions & Workflow Policy"


@dataclass(frozen=True, slots=True)
class HygieneResult:
    """Outcome of one in-repo hygiene inspection or alignment."""

    status: str
    is_aligned: bool = False
    file: str | None = None
    files: tuple[str, ...] = ()
    reason: str | None = None
    unpinned_count: int = 0


def _conventions_present(content: str) -> bool:
    """Return True if the governance-owned conventions section already exists."""
    return AGENTS_CONVENTIONS_HEADING in content or "Repository Conventions & Workflow Policy" in content


def inspect_agents_md(repo_path: pathlib.Path) -> HygieneResult:
    """Inspects if AGENTS.md exists and contains expected governance directives."""
    agents_file = repo_path / "AGENTS.md"
    if not agents_file.exists():
        return HygieneResult(status="MISSING", is_aligned=False)

    content = agents_file.read_text(encoding="utf-8")
    if "Repository Conventions & Workflow Policy" in content and "TODO.md" in content:
        return HygieneResult(status="CONFIGURED", is_aligned=True)
    return HygieneResult(status="PARTIAL", is_aligned=False)


def align_agents_md(repo_path: pathlib.Path, dry_run: bool = False) -> HygieneResult:
    """Ensures AGENTS.md exists and declares the governance conventions section.

    Creates the file when missing and appends the expected block when the section
    is genuinely absent. When the section is present but does not match, this
    **reports and does not write** — a second copy is never appended.
    """
    agents_file = repo_path / "AGENTS.md"

    if not agents_file.exists():
        if not dry_run:
            agents_file.write_text(
                f"# {repo_path.name.upper()}\n\n{EXPECTED_AGENTS_CONVENTIONS}",
                encoding="utf-8",
            )
        return HygieneResult(status="created", file=str(agents_file))

    content = agents_file.read_text(encoding="utf-8")

    if not _conventions_present(content):
        if not dry_run:
            agents_file.write_text(f"{content}\n\n{EXPECTED_AGENTS_CONVENTIONS}", encoding="utf-8")
        return HygieneResult(status="appended", file=str(agents_file))

    if EXPECTED_AGENTS_CONVENTIONS.strip() not in content:
        return HygieneResult(
            status="divergent",
            file=str(agents_file),
            reason="The conventions section exists but does not match the expected block. "
            "Left as-is — run `repo-governance agents-audit` to review it.",
        )

    return HygieneResult(status="already_configured", file=str(agents_file))


def inspect_npmrc(repo_path: pathlib.Path) -> HygieneResult:
    """Inspects if .npmrc is present and configured for GitHub Packages."""
    package_json = repo_path / "package.json"
    if not package_json.exists():
        return HygieneResult(status="N/A (No package.json)", is_aligned=True)

    npmrc_file = repo_path / ".npmrc"
    if npmrc_file.exists():
        content = npmrc_file.read_text(encoding="utf-8")
        if "@rtkelly13:registry=https://npm.pkg.github.com" in content:
            return HygieneResult(status="CONFIGURED", is_aligned=True)

    return HygieneResult(status="MISSING / DRIFTED", is_aligned=False)


def align_npmrc(repo_path: pathlib.Path, dry_run: bool = False) -> HygieneResult:
    """Ensures .npmrc is configured for GitHub Packages if package.json is present."""
    package_json = repo_path / "package.json"
    if not package_json.exists():
        return HygieneResult(status="skipped", reason="No package.json found")

    npmrc_file = repo_path / ".npmrc"
    expected_line = "@rtkelly13:registry=https://npm.pkg.github.com\n"

    if npmrc_file.exists():
        content = npmrc_file.read_text(encoding="utf-8")
        if "@rtkelly13:registry=https://npm.pkg.github.com" in content:
            return HygieneResult(status="already_configured")
    else:
        content = ""

    new_content = expected_line + content
    if not dry_run:
        npmrc_file.write_text(new_content, encoding="utf-8")

    return HygieneResult(status="aligned", file=str(npmrc_file))


def inspect_ci_workflow(repo_path: pathlib.Path) -> HygieneResult:
    """Inspects if .github/workflows/ contains a CI workflow file."""
    workflows_dir = repo_path / ".github" / "workflows"
    if workflows_dir.exists() and (any(workflows_dir.glob("*.yml")) or any(workflows_dir.glob("*.yaml"))):
        return HygieneResult(status="PRESENT", is_aligned=True)
    return HygieneResult(status="MISSING", is_aligned=False)


HEX_SHA_PATTERN = re.compile(r"^[0-9a-fA-F]{40}$")
USES_PATTERN = re.compile(r"(uses:\s+)([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)@([A-Za-z0-9_.-]+)(.*)")


def inspect_pinned_shas(repo_path: pathlib.Path) -> HygieneResult:
    """Counts `uses:` refs under .github/workflows/ that are not 40-char commit SHAs.

    Read-only. Pinning itself is done in each repo by pinact (first pin) and Dependabot's
    `github-actions` ecosystem (updates), and gated in CI by `zizmor`'s `unpinned-uses` audit.
    See docs/repo-governance/action-pinning.md.
    """
    workflows_dir = repo_path / ".github" / "workflows"
    if not workflows_dir.exists():
        return HygieneResult(status="N/A (No Workflows)", is_aligned=True, unpinned_count=0)

    workflow_files = list(workflows_dir.glob("*.yml")) + list(workflows_dir.glob("*.yaml"))
    if not workflow_files:
        return HygieneResult(status="N/A (No Workflows)", is_aligned=True, unpinned_count=0)

    unpinned_count = 0
    for wf in workflow_files:
        content = wf.read_text(encoding="utf-8")
        for line in content.splitlines():
            match = USES_PATTERN.search(line)
            if match:
                ref = match.group(3)
                if not HEX_SHA_PATTERN.match(ref):
                    unpinned_count += 1

    if unpinned_count == 0:
        return HygieneResult(status="PINNED", is_aligned=True, unpinned_count=0)

    return HygieneResult(status=f"UNPINNED ({unpinned_count} actions)", is_aligned=False, unpinned_count=unpinned_count)


#: Marks a hook that blocks direct pushes to main.
_MAIN_BLOCK_MARKER = "DIRECT PUSH TO MAIN BLOCKED"
#: Marks a hook that also hands the push to git-lfs. pre-push is the hook git-lfs
#: uploads LFS content from, so a hook without it pushes pointers and no content.
_LFS_HANDOFF_MARKER = "git lfs pre-push"

PRE_PUSH_HOOK_CONTENT = """#!/usr/bin/env bash
# Enforced by align-repo-conventions
# Prevents accidental direct pushes to main branch without explicit bypass,
# then hands the push to git-lfs so LFS content is uploaded with the commits.

# Git passes the refs being pushed on stdin; keep them for git-lfs.
refs=$(cat)

while read local_ref local_oid remote_ref remote_oid
do
	if [ "$remote_ref" = "refs/heads/main" ]; then
		if [ "$ALLOW_MAIN_PUSH" != "1" ]; then
			echo "🛑 DIRECT PUSH TO MAIN BLOCKED by repository governance policy!"
			echo "Push your changes to a feature branch and merge via PR (gh pr merge --squash)."
			echo "Override for emergency push: ALLOW_MAIN_PUSH=1 git push origin main"
			exit 1
		fi
	fi
done <<< "$refs"

if command -v git-lfs >/dev/null 2>&1; then
	printf '%s\\n' "$refs" | git lfs pre-push "$@" || exit $?
fi

exit 0
"""


def _hook_is_current(content: str) -> bool:
    return _MAIN_BLOCK_MARKER in content and _LFS_HANDOFF_MARKER in content


def inspect_git_hooks(repo_path: pathlib.Path) -> HygieneResult:
    """Inspects if .git/hooks/pre-push blocks main pushes and still uploads LFS content."""
    hook_file = repo_path / ".git" / "hooks" / "pre-push"
    if hook_file.exists():
        content = hook_file.read_text(encoding="utf-8")
        if _hook_is_current(content):
            return HygieneResult(status="ENFORCED", is_aligned=True)
        if _MAIN_BLOCK_MARKER in content:
            return HygieneResult(status="OUTDATED", is_aligned=False, reason="pre-push hook skips git-lfs uploads")
    return HygieneResult(status="MISSING", is_aligned=False)


def align_git_hooks(repo_path: pathlib.Path, dry_run: bool = False) -> HygieneResult:
    """Installs protective pre-push hook into .git/hooks/pre-push."""
    hooks_dir = repo_path / ".git" / "hooks"
    if not hooks_dir.exists():
        return HygieneResult(status="skipped", reason="No .git/hooks directory found")

    hook_file = hooks_dir / "pre-push"
    if hook_file.exists() and _hook_is_current(hook_file.read_text(encoding="utf-8")):
        return HygieneResult(status="already_configured")

    if not dry_run:
        hook_file.write_text(PRE_PUSH_HOOK_CONTENT, encoding="utf-8")
        hook_file.chmod(0o755)

    return HygieneResult(status="aligned", file=str(hook_file))


def inspect_todo_md(repo_path: pathlib.Path) -> HygieneResult:
    """Inspects if local root TODO.md exists (exact uppercase casing)."""
    if any(p.name == "TODO.md" for p in repo_path.iterdir()):
        return HygieneResult(status="PRESENT", is_aligned=True)
    return HygieneResult(status="MISSING", is_aligned=False)


def align_todo_md(repo_path: pathlib.Path, dry_run: bool = False) -> HygieneResult:
    """Ensures root TODO.md exists locally for task tracking (exact uppercase casing)."""
    todo_file = repo_path / "TODO.md"
    lowercase_todo = repo_path / "todo.md"

    # On case-insensitive filesystems (macOS default), todo_file.exists() is true
    # when only todo.md exists — compare the on-disk name to avoid deleting TODO.md.
    actual_names = {p.name for p in repo_path.iterdir() if p.name.lower() == "todo.md"}
    if "todo.md" in actual_names and "TODO.md" not in actual_names:
        if not dry_run:
            content = lowercase_todo.read_text(encoding="utf-8")
            todo_file.write_text(content, encoding="utf-8")
            lowercase_todo.unlink()
        return HygieneResult(status="renamed_to_uppercase", file=str(todo_file))

    if todo_file.exists():
        return HygieneResult(status="already_exists")

    initial_content = f"# TODO: {repo_path.name.upper()}\n\n- [ ] Local tasks and notes (gitignored)\n"
    if not dry_run:
        todo_file.write_text(initial_content, encoding="utf-8")

    return HygieneResult(status="aligned", file=str(todo_file))


COMMON_SCRIPT_SUPPORT_EXTENSIONS = (
    ".dir",
    ".md",
    ".json",
    ".yml",
    ".yaml",
    ".toml",
    ".xml",
    ".sql",
    ".txt",
)


def inspect_ls_lint(
    repo_path: pathlib.Path,
    config: PolicyConfig | None = None,
) -> HygieneResult:
    """Inspects if .ls-lint.yml exists and enforces banned script rules."""
    ls_lint_file = repo_path / ".ls-lint.yml"
    if not ls_lint_file.exists():
        return HygieneResult(status="MISSING", is_aligned=False)

    content = ls_lint_file.read_text(encoding="utf-8")
    expected_banned = (".sh", ".ps1")
    key = policy_key(repo_path)
    if config is not None:
        rules = config.script_ban(key)
        if rules.enabled:
            expected_banned = tuple(ext for ext in (".sh", ".ps1") if ext in rules.banned_extensions)
    for ext in expected_banned:
        if f"{ext}: exists:0" not in content:
            return HygieneResult(status="INCOMPLETE_RULES", is_aligned=False, file=str(ls_lint_file))

    if config is not None:
        rules = config.script_ban(key)
        if rules.enabled:
            for exc in rules.allowed_exceptions:
                if exc not in content:
                    return HygieneResult(
                        status="MISSING_EXCEPTIONS",
                        is_aligned=False,
                        file=str(ls_lint_file),
                        reason=f"Missing allowed exception: {exc}",
                    )
        script_rules = config.script_rules_for_repo(key, repo_path=repo_path)
        if script_rules.allowed_extensions or script_rules.banned_extensions:
            if "scripts:" not in content:
                return HygieneResult(
                    status="MISSING_SCRIPTS_RULES",
                    is_aligned=False,
                    file=str(ls_lint_file),
                    reason="Missing scripts folder language rules",
                )
            for b_ext in script_rules.banned_extensions:
                clean_b = b_ext if b_ext.startswith(".") else f".{b_ext}"
                if f"{clean_b}: exists:0" not in content:
                    return HygieneResult(
                        status="MISSING_BANNED_EXTENSION",
                        is_aligned=False,
                        file=str(ls_lint_file),
                        reason=f"Missing banned script extension: {clean_b}",
                    )
            for a_ext in script_rules.allowed_extensions:
                clean_a = a_ext if a_ext.startswith(".") else f".{a_ext}"
                if f"{clean_a}: regex:.*" not in content:
                    return HygieneResult(
                        status="MISSING_ALLOWED_EXTENSION",
                        is_aligned=False,
                        file=str(ls_lint_file),
                        reason=f"Missing allowed script extension: {clean_a}",
                    )

    return HygieneResult(status="CONFIGURED", is_aligned=True, file=str(ls_lint_file))


def render_ls_lint_config(
    allowed_exceptions: tuple[str, ...] = (),
    allowed_script_extensions: tuple[str, ...] = (),
    banned_script_extensions: tuple[str, ...] = (),
) -> str:
    """Generates standard .ls-lint.yml content."""
    lines = [
        "# .ls-lint.yml",
        "# Policy ban on shell (.sh, .bash, .zsh) and PowerShell (.ps1, .psm1) scripts.",
        "# Exceptions must be explicitly listed in the `ignore:` section below.",
        "",
        "ls:",
        "  .sh: exists:0",
        "  .bash: exists:0",
        "  .zsh: exists:0",
        "  .ps1: exists:0",
        "  .psm1: exists:0",
        '  "**":',
        "    .sh: exists:0",
        "    .bash: exists:0",
        "    .zsh: exists:0",
        "    .ps1: exists:0",
        "    .psm1: exists:0",
    ]
    if allowed_script_extensions or banned_script_extensions:
        for scope in ("scripts", '"scripts/**"'):
            lines.append(f"  {scope}:")
            for ext in COMMON_SCRIPT_SUPPORT_EXTENSIONS:
                lines.append(f"    {ext}: regex:.*")
            for ext in allowed_script_extensions:
                clean_ext = ext if ext.startswith(".") else f".{ext}"
                if clean_ext not in COMMON_SCRIPT_SUPPORT_EXTENSIONS:
                    lines.append(f"    {clean_ext}: regex:.*")
            for ext in banned_script_extensions:
                clean_ext = ext if ext.startswith(".") else f".{ext}"
                lines.append(f"    {clean_ext}: exists:0")
            lines.append("    .*: exists:0")

    lines.extend(
        [
            "",
            "ignore:",
            "  - .git",
            "  - .venv",
            '  - "**/.venv"',
            "  - node_modules",
            '  - "**/node_modules"',
            "  - temp",
            "  - .temp",
            '  - "**/temp"',
            "  - .worktrees",
            "  - bin",
            "  - obj",
            "  - dist",
            "  - build",
        ]
    )
    if allowed_exceptions:
        lines.append("  # Explicit repository exceptions:")
        for exc in allowed_exceptions:
            lines.append(f"  - {exc}")
    lines.append("")
    return "\n".join(lines)


def align_ls_lint(
    repo_path: pathlib.Path,
    dry_run: bool = False,
    config: PolicyConfig | None = None,
) -> HygieneResult:
    """Ensures .ls-lint.yml exists and declares the banned script rules."""
    current = inspect_ls_lint(repo_path, config=config)
    if current.is_aligned:
        return HygieneResult(status="already_configured", file=current.file)

    allowed_exceptions: tuple[str, ...] = ()
    allowed_script_extensions: tuple[str, ...] = ()
    banned_script_extensions: tuple[str, ...] = ()
    if config is not None:
        key = policy_key(repo_path)
        rules = config.script_ban(key)
        if rules.enabled:
            allowed_exceptions = rules.allowed_exceptions
        script_rules = config.script_rules_for_repo(key, repo_path=repo_path)
        allowed_script_extensions = script_rules.allowed_extensions
        banned_script_extensions = script_rules.banned_extensions

    content = render_ls_lint_config(
        allowed_exceptions=allowed_exceptions,
        allowed_script_extensions=allowed_script_extensions,
        banned_script_extensions=banned_script_extensions,
    )
    ls_lint_file = repo_path / ".ls-lint.yml"
    if not dry_run:
        ls_lint_file.write_text(content, encoding="utf-8")

    return HygieneResult(status="aligned", file=str(ls_lint_file))


RENOVATE_CONFIG_FILES: tuple[str, ...] = (
    "renovate.json",
    "renovate.json5",
    ".renovaterc",
    ".renovaterc.json",
    ".renovaterc.json5",
    ".github/renovate.json",
    ".github/renovate.json5",
    ".github/.renovaterc",
    ".github/.renovaterc.json",
    ".github/.renovaterc.json5",
)

DEPENDABOT_CONFIG_FILES: tuple[str, ...] = (
    ".github/dependabot.yml",
    ".github/dependabot.yaml",
)


@dataclass(slots=True, frozen=True)
class _DependabotUpdateEntry:
    package_ecosystem: str | None = Field(default=None, alias="package-ecosystem")
    open_pull_requests_limit: int | None = Field(default=None, alias="open-pull-requests-limit")


@dataclass(slots=True, frozen=True)
class _DependabotConfig:
    version: int | str = 2
    updates: tuple[_DependabotUpdateEntry, ...] = ()


def find_renovate_config(repo_path: pathlib.Path) -> pathlib.Path | None:
    """Finds Renovate configuration file in the repository if present."""
    for rel_path in RENOVATE_CONFIG_FILES:
        candidate = repo_path / rel_path
        if candidate.is_file():
            return candidate
    return None


def find_dependabot_config(repo_path: pathlib.Path) -> pathlib.Path | None:
    """Finds Dependabot configuration file in the repository if present."""
    for rel_path in DEPENDABOT_CONFIG_FILES:
        candidate = repo_path / rel_path
        if candidate.is_file():
            return candidate
    return None


def has_dependabot_version_updates(repo_path: pathlib.Path) -> bool:
    """Checks whether Dependabot is configured for automated version updates.

    Security-only Dependabot alerts operate without a version-updates
    configuration or with updates entries disabled via open-pull-requests-limit: 0.
    """
    dep_file = find_dependabot_config(repo_path)
    if dep_file is None:
        return False
    try:
        content = dep_file.read_text(encoding="utf-8")
    except OSError:
        return False
    cfg = try_decode_yaml(_DependabotConfig, content)
    if cfg is None:
        return "updates:" in content
    if not cfg.updates:
        return False
    return any(u.open_pull_requests_limit is None or u.open_pull_requests_limit > 0 for u in cfg.updates)


def inspect_dependabot_renovate(repo_path: pathlib.Path) -> HygieneResult:
    """Policy check that flags repositories with both Dependabot version updates and Renovate config.

    Security-only Dependabot alerts are permitted alongside Renovate, but active
    version updates in .github/dependabot.yml conflict with Renovate PR automation.
    """
    renovate_file = find_renovate_config(repo_path)
    if renovate_file is None:
        return HygieneResult(status="NO_RENOVATE", is_aligned=True)

    has_version_updates = has_dependabot_version_updates(repo_path)
    if not has_version_updates:
        return HygieneResult(status="CONFIGURED", is_aligned=True, file=str(renovate_file))

    dep_file = find_dependabot_config(repo_path)
    dep_name = dep_file.name if dep_file else "dependabot.yml"
    reason = (
        f"Conflicting dependency automation: found both Renovate config ({renovate_file.name}) "
        f"and active version updates in .github/{dep_name}. "
        "Remove version updates from dependabot.yml or set open-pull-requests-limit: 0 (security-only alerts are fine)."
    )
    return HygieneResult(
        status="CONFLICT",
        is_aligned=False,
        file=str(renovate_file),
        reason=reason,
    )

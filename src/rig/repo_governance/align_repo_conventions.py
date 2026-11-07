import typer

from rig.repo_governance import cli_audits, cli_repo_checks, cli_sync, cli_triage
from rig.repo_governance.github_rulesets import (
    HARDCODED_REQUIRED_CHECKS,
    align_github_repo_settings,
    align_github_ruleset,
    build_ruleset_payload,
    get_required_checks_for_repo,
    inspect_check_names,
    inspect_github_ruleset,
    inspect_repo_settings,
)
from rig.repo_governance.repo_discovery import (
    discover_org_repos,
    get_gh_repo_name,
    inspect_repo_exists,
    inspect_repo_owner,
    is_managed_repo,
    personal_repos_dir,
    resolve_target_directories,
)
from rig.repo_governance.repo_hygiene import (
    AGENTS_CONVENTIONS_HEADING,
    EXPECTED_AGENTS_CONVENTIONS,
    HEX_SHA_PATTERN,
    PRE_PUSH_HOOK_CONTENT,
    USES_PATTERN,
    align_agents_md,
    align_git_hooks,
    align_npmrc,
    align_todo_md,
    inspect_agents_md,
    inspect_ci_workflow,
    inspect_git_hooks,
    inspect_npmrc,
    inspect_pinned_shas,
    inspect_todo_md,
)

__all__ = [
    "AGENTS_CONVENTIONS_HEADING",
    "EXPECTED_AGENTS_CONVENTIONS",
    "HARDCODED_REQUIRED_CHECKS",
    "HEX_SHA_PATTERN",
    "PRE_PUSH_HOOK_CONTENT",
    "USES_PATTERN",
    "align_agents_md",
    "align_git_hooks",
    "align_github_repo_settings",
    "align_github_ruleset",
    "align_npmrc",
    "align_todo_md",
    "app",
    "build_ruleset_payload",
    "discover_org_repos",
    "get_gh_repo_name",
    "get_required_checks_for_repo",
    "inspect_agents_md",
    "inspect_check_names",
    "inspect_ci_workflow",
    "inspect_git_hooks",
    "inspect_github_ruleset",
    "inspect_npmrc",
    "inspect_pinned_shas",
    "inspect_repo_exists",
    "inspect_repo_owner",
    "inspect_repo_settings",
    "inspect_todo_md",
    "is_managed_repo",
    "personal_repos_dir",
    "resolve_target_directories",
]

app = typer.Typer(help="CLI tool to audit, inspect status, and enforce repository conventions across GitHub repos.")
app.registered_commands.extend(
    command
    for sub_app in (cli_repo_checks.app, cli_sync.app, cli_triage.app, cli_audits.app)
    for command in sub_app.registered_commands
)


def main():
    app()


if __name__ == "__main__":
    main()

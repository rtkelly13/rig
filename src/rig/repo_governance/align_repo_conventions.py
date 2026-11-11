import typer

from rig import csharp_cli
from rig.repo_governance import cli_audits, cli_triage
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

__all__ = [
    "HARDCODED_REQUIRED_CHECKS",
    "align_github_repo_settings",
    "align_github_ruleset",
    "app",
    "build_ruleset_payload",
    "discover_org_repos",
    "get_gh_repo_name",
    "get_required_checks_for_repo",
    "inspect_check_names",
    "inspect_github_ruleset",
    "inspect_repo_exists",
    "inspect_repo_owner",
    "inspect_repo_settings",
    "is_managed_repo",
    "personal_repos_dir",
    "resolve_target_directories",
]

app = typer.Typer(help="CLI tool to audit, inspect status, and enforce repository conventions across GitHub repos.")
app.registered_commands.extend(
    command for sub_app in (cli_triage.app, cli_audits.app) for command in sub_app.registered_commands
)
csharp_cli.add_group_stubs(app, "repo-governance")


def main():
    app()


if __name__ == "__main__":
    main()

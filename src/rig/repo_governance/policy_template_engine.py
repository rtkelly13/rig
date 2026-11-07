"""
Intentional Policy Templating Engine for Open-Source and Workspace Repositories.

Inspects repository visibility (public vs private), detects runtime ecosystems,
and renders parameterized governance policies (AI_POLICY.md, SECURITY.md, README disclosures).
"""

import pathlib
from dataclasses import asdict, dataclass
from typing import Annotated

import jinja2
from pydantic import Field

from rig import proc
from rig.gh_origin import origin_args

TEMPLATES_DIR = pathlib.Path(__file__).parent / "templates"
jinja_env = jinja2.Environment(
    loader=jinja2.FileSystemLoader(TEMPLATES_DIR),
    autoescape=jinja2.select_autoescape(),
    trim_blocks=True,
    lstrip_blocks=True,
)


@dataclass(frozen=True, slots=True)
class RepoMetadata:
    """The repo facts the policy templates render from."""

    repo_name: str
    is_public: bool
    license: str
    author_email: str
    supported_runtimes: str


@dataclass(frozen=True, slots=True)
class _License:
    spdx_id: Annotated[str | None, Field(alias="spdxId")] = None


@dataclass(frozen=True, slots=True)
class _GhRepoView:
    name_with_owner: Annotated[str | None, Field(alias="nameWithOwner")] = None
    is_private: Annotated[bool, Field(alias="isPrivate")] = True
    license_info: Annotated[_License | None, Field(alias="licenseInfo")] = None


def detect_repo_metadata(repo_path: pathlib.Path) -> RepoMetadata:
    """Inspects local repository files and GitHub remote metadata."""
    repo_name = repo_path.name
    is_public = False
    license_name = "MIT"
    author_email = "94ryan.kelly@gmail.com"

    # 1. Query GitHub CLI if available, about origin rather than an `upstream` remote (rig.gh_origin).
    # Falls back to local inspection when gh is missing or fails.
    gh_data = proc.gh_json(
        _GhRepoView,
        "repo",
        "view",
        *origin_args(repo_path),
        "--json",
        "nameWithOwner,isPrivate,visibility,licenseInfo",
        cwd=repo_path,
    )
    if gh_data is not None:
        repo_name = gh_data.name_with_owner or repo_name
        is_public = not gh_data.is_private
        if gh_data.license_info and gh_data.license_info.spdx_id:
            license_name = gh_data.license_info.spdx_id

    # 2. Detect Tech Stack / Supported Runtimes
    supported_runtimes = ".NET 8 & .NET 9"
    if (repo_path / "pyproject.toml").exists() or (repo_path / "setup.py").exists():
        supported_runtimes = "Python 3.11+"
    elif (repo_path / "package.json").exists():
        supported_runtimes = "Node.js 20+ / TypeScript"
    elif list(repo_path.glob("*.sln")) or list(repo_path.glob("**/*.fsproj")) or list(repo_path.glob("**/*.csproj")):
        supported_runtimes = ".NET 8 & .NET 9"

    return RepoMetadata(
        repo_name=repo_name,
        is_public=is_public,
        license=license_name,
        author_email=author_email,
        supported_runtimes=supported_runtimes,
    )


def render_policy_template(template_name: str, context: RepoMetadata) -> str:
    """Renders a Jinja policy template with provided repository context."""
    template = jinja_env.get_template(template_name)
    return template.render(**asdict(context))


def apply_ai_policy(repo_path: pathlib.Path, context: RepoMetadata | None = None, dry_run: bool = False) -> bool:
    """Renders and writes AI_POLICY.md to target repository."""
    ctx = context or detect_repo_metadata(repo_path)
    content = render_policy_template("AI_POLICY.md.jinja", ctx)
    target_file = repo_path / "AI_POLICY.md"

    if dry_run:
        return True

    target_file.write_text(content.strip() + "\n", encoding="utf-8")
    return True


def apply_security_policy(repo_path: pathlib.Path, context: RepoMetadata | None = None, dry_run: bool = False) -> bool:
    """Renders and writes SECURITY.md to target repository."""
    ctx = context or detect_repo_metadata(repo_path)
    content = render_policy_template("SECURITY.md.jinja", ctx)
    target_file = repo_path / "SECURITY.md"

    if dry_run:
        return True

    target_file.write_text(content.strip() + "\n", encoding="utf-8")
    return True


def inject_readme_ai_disclosure(
    repo_path: pathlib.Path, context: RepoMetadata | None = None, dry_run: bool = False
) -> bool:
    """Injects or updates AI badge and AI disclosure section in README.md."""
    readme_path = repo_path / "README.md"
    if not readme_path.exists():
        return False

    ctx = context or detect_repo_metadata(repo_path)
    readme_text = readme_path.read_text(encoding="utf-8")

    # 1. Badge Injection
    ai_badge = "[![AI Assisted](https://img.shields.io/badge/development-AI%20assisted-792ee5.svg?logo=openai&logoColor=white)](AI_POLICY.md)"
    if "AI%20assisted" not in readme_text and "[![AI Assisted]" not in readme_text:
        # Place next to other badges in header
        lines = readme_text.splitlines()
        badge_idx = -1
        for i, line in enumerate(lines[:15]):
            if line.startswith("[![") or line.startswith("[! ["):
                badge_idx = i
        if badge_idx != -1:
            lines.insert(badge_idx + 1, ai_badge)
            readme_text = "\n".join(lines)
        else:
            readme_text = ai_badge + "\n\n" + readme_text

    # 2. Section Injection
    section_content = render_policy_template("README_AI_SECTION.md.jinja", ctx)
    if "## 🤖 AI Utilization & Transparency" not in readme_text:
        # Insert before License or Documentation or at the bottom
        if "## 📄 License" in readme_text:
            readme_text = readme_text.replace("## 📄 License", f"{section_content}\n\n---\n\n## 📄 License")
        elif "## License" in readme_text:
            readme_text = readme_text.replace("## License", f"{section_content}\n\n---\n\n## License")
        else:
            readme_text = readme_text.rstrip() + f"\n\n---\n\n{section_content}\n"

    if dry_run:
        return True

    readme_path.write_text(readme_text, encoding="utf-8")
    return True

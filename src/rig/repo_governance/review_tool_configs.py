"""In-repo config files for code review tools: CodeRabbit, Codecov, DeepSource and Qodana."""

from __future__ import annotations

import os
import pathlib
from dataclasses import dataclass
from typing import Literal

from rig.repo_governance.github_apps_manager import is_repo_disallowed
from rig.repo_governance.policy_config import PolicyConfig, load_policy_config
from rig.typed_yaml import try_decode_yaml


@dataclass(slots=True, frozen=True)
class ConfigChange:
    """What aligning one config file did. ``changed`` is also True for a dry run that would write."""

    changed: bool
    message: str


@dataclass(slots=True, frozen=True)
class ToolChange:
    tool: str
    changed: bool
    message: str


type ConfigState = Literal["CONFIGURED", "MISSING", "APPLICABLE", "OPTIONAL"]


@dataclass(slots=True, frozen=True)
class ConfigStatus:
    status: ConfigState
    is_aligned: bool
    file: str | None = None
    #: Qodana only: whether its workflow exists, and whether the repo is .NET.
    workflow_present: bool | None = None
    applicable: bool | None = None


@dataclass(slots=True, frozen=True)
class _AutoReview:
    enabled: object = None


@dataclass(slots=True, frozen=True)
class _Reviews:
    auto_review: _AutoReview | None = None


@dataclass(slots=True, frozen=True)
class _CodeRabbitYaml:
    reviews: _Reviews | None = None


DEFAULT_CODERABBIT_YAML = """# CodeRabbit Configuration (Governed by shared-utilities)
# See https://docs.coderabbit.ai/guides/configure-coderabbit for details

language: "en-US"
tone_instructions: "Be concise, objective, and constructive. Focus on correctness, edge cases, and performance."
early_access: false

reviews:
  profile: "chill"
  request_changes_workflow: false
  high_level_summary: true
  poem: false
  review_status: true
  collapse_walkthrough: false
  auto_review:
    enabled: true
    drafts: false
    base_branches:
      - "main"
      - "master"
  path_filters:
    - "!**/node_modules/**"
    - "!**/dist/**"
    - "!**/build/**"
    - "!**/temp/**"
    - "!**/.temp/**"
    - "!**/*.min.js"
    - "!**/*.min.css"

chat:
  auto_reply: true
"""

DEFAULT_CODERABBIT_SUPPRESSOR_YAML = """# CodeRabbit Suppressor (Governed by shared-utilities)
# Disables automated reviews on non-code knowledge repositories

reviews:
  auto_review:
    enabled: false
"""

DEFAULT_CODECOV_YAML = """# Codecov Configuration (Governed by shared-utilities)
# See https://docs.codecov.com/docs/codecov-yaml for details

coverage:
  precision: 2
  round: down
  range: "70...100"
  status:
    project:
      default:
        target: auto
        threshold: 1%
    patch:
      default:
        target: 80%
        threshold: 5%

comment:
  layout: "header, diff, flags, files"
  behavior: default
  require_changes: false
"""

DEFAULT_QODANA_YAML = """# Qodana Configuration (Governed by shared-utilities)
# See https://www.jetbrains.com/help/qodana/qodana-yaml.html

version: "1.0"
profile:
  name: qodana.recommended
"""

DEFAULT_QODANA_WORKFLOW_DOTNET = """name: Qodana (.NET)

on:
  pull_request:
  push:
    branches: [main]

jobs:
  qodana:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      security-events: write
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: "Qodana Scan (.NET)"
        uses: JetBrains/qodana-action@v2024.3
        with:
          args: --linter,jetbrains/qodana-cdnet:2024.3

      - name: "Upload SARIF to GitHub Security"
        uses: github/codeql-action/upload-sarif@v3
        if: always()
        with:
          sarif_file: ${{ runner.temp }}/qodana/results/qodana.sarif.json
"""


def inspect_coderabbit_config(repo_path: pathlib.Path) -> ConfigStatus:
    """Inspects if .coderabbit.yaml is present in the repository root."""
    config_file = repo_path / ".coderabbit.yaml"
    legacy_file = repo_path / ".coderabbit.yml"

    if config_file.exists() or legacy_file.exists():
        found = config_file if config_file.exists() else legacy_file
        return ConfigStatus("CONFIGURED", is_aligned=True, file=str(found))
    return ConfigStatus("MISSING", is_aligned=False)


def align_coderabbit_config(repo_path: pathlib.Path, dry_run: bool = False) -> ConfigChange:
    """Writes canonical .coderabbit.yaml in repository root if absent."""
    config_file = repo_path / ".coderabbit.yaml"
    legacy_file = repo_path / ".coderabbit.yml"

    if config_file.exists() or legacy_file.exists():
        return ConfigChange(False, "already configured")

    if not dry_run:
        config_file.write_text(DEFAULT_CODERABBIT_YAML, encoding="utf-8")
        return ConfigChange(True, f"created {config_file.name}")

    return ConfigChange(True, f"dry-run: would create {config_file.name}")


def inspect_codecov_config(repo_path: pathlib.Path) -> ConfigStatus:
    """Inspects if codecov.yml or .codecov.yml is present in the repository root."""
    config_file = repo_path / "codecov.yml"
    dot_file = repo_path / ".codecov.yml"
    dot_github_file = repo_path / ".github" / "codecov.yml"

    for candidate in (config_file, dot_file, dot_github_file):
        if candidate.exists():
            return ConfigStatus("CONFIGURED", is_aligned=True, file=str(candidate))
    return ConfigStatus("MISSING", is_aligned=False)


def align_codecov_config(repo_path: pathlib.Path, dry_run: bool = False) -> ConfigChange:
    """Writes canonical codecov.yml in repository root if absent."""
    res = inspect_codecov_config(repo_path)
    if res.is_aligned:
        return ConfigChange(False, "already configured")

    config_file = repo_path / "codecov.yml"
    if not dry_run:
        config_file.write_text(DEFAULT_CODECOV_YAML, encoding="utf-8")
        return ConfigChange(True, f"created {config_file.name}")

    return ConfigChange(True, f"dry-run: would create {config_file.name}")


_DOTNET_MARKER_NAMES = frozenset({"Directory.Build.props", "global.json"})
_DOTNET_MARKER_SUFFIXES = (".sln", ".slnx", ".csproj", ".fsproj")
_DOTNET_PRUNE_DIRS = frozenset({"node_modules", "temp", "dist", "build", "coverage", "vendor", "bin", "obj"})


def is_dotnet_repo(repo_path: pathlib.Path) -> bool:
    """Checks if repository contains .NET solution/SDK/project markers at any depth.

    Hidden directories and build/vendor trees are pruned during traversal.
    """
    for _root, dirs, files in os.walk(repo_path):
        dirs[:] = [d for d in dirs if not d.startswith(".") and d not in _DOTNET_PRUNE_DIRS]
        for name in files:
            if name in _DOTNET_MARKER_NAMES or name.endswith(_DOTNET_MARKER_SUFFIXES):
                return True
    return False


def generate_deepsource_config(repo_path: pathlib.Path) -> str:
    """Generates canonical .deepsource.toml tailored to detected repo languages."""
    has_python = False
    has_js_ts = False
    has_csharp = is_dotnet_repo(repo_path)

    try:
        for p in repo_path.iterdir():
            name = p.name
            if name in ("pyproject.toml", "requirements.txt", "Pipfile", "setup.py") or name.endswith(".py"):
                has_python = True
            elif name in ("package.json", "tsconfig.json") or name.endswith((".ts", ".tsx", ".js", ".jsx")):
                has_js_ts = True
            elif p.is_dir() and not name.startswith((".", "node_modules", "temp", "vendor", "dist", "bin", "obj")):
                try:
                    for sub in p.iterdir():
                        sname = sub.name
                        if sname.endswith(".py") or sname in ("pyproject.toml", "requirements.txt"):
                            has_python = True
                        elif sname.endswith((".ts", ".tsx", ".js", ".jsx")) or sname in (
                            "package.json",
                            "tsconfig.json",
                        ):
                            has_js_ts = True
                except OSError:
                    pass
    except OSError:
        pass

    analyzers: list[str] = []
    if has_python:
        analyzers.append(
            '[[analyzers]]\nname = "python"\nenabled = true\n\n[analyzers.meta]\nruntime_version = "3.x.x"'
        )
    if has_js_ts:
        analyzers.append(
            '[[analyzers]]\nname = "javascript"\nenabled = true\n\n[analyzers.meta]\nenvironment = ["nodejs"]'
        )
    if has_csharp:
        analyzers.append('[[analyzers]]\nname = "csharp"\nenabled = true')

    # Secrets analyzer is universal and supported across all repos
    analyzers.append('[[analyzers]]\nname = "secrets"\nenabled = true')

    analyzers_block = "\n\n".join(analyzers)
    return f"""# DeepSource Configuration (Governed by shared-utilities)
# See https://docs.deepsource.com for configuration syntax

version = 1

exclude_patterns = [
  "**/node_modules/**",
  "**/dist/**",
  "**/build/**",
  "**/temp/**",
  "**/.temp/**",
  "**/.venv/**",
  "**/bin/**",
  "**/obj/**",
]

test_patterns = [
  "**/tests/**",
  "**/test_*.py",
  "**/*.test.ts",
  "**/*.test.tsx",
  "**/*.spec.ts",
  "**/*.spec.tsx",
  "**/*Tests.cs",
]

{analyzers_block}
"""


def inspect_deepsource_config(repo_path: pathlib.Path) -> ConfigStatus:
    """Inspects if .deepsource.toml is present in the repository root."""
    config_file = repo_path / ".deepsource.toml"
    if config_file.exists():
        return ConfigStatus("CONFIGURED", is_aligned=True, file=str(config_file))
    return ConfigStatus("MISSING", is_aligned=False)


def align_deepsource_config(repo_path: pathlib.Path, dry_run: bool = False) -> ConfigChange:
    """Writes canonical .deepsource.toml in repository root if absent."""
    res = inspect_deepsource_config(repo_path)
    if res.is_aligned:
        return ConfigChange(False, "already configured")

    config_file = repo_path / ".deepsource.toml"
    content = generate_deepsource_config(repo_path)
    if not dry_run:
        config_file.write_text(content, encoding="utf-8")
        return ConfigChange(True, f"created {config_file.name}")

    return ConfigChange(True, f"dry-run: would create {config_file.name}")


def inspect_qodana_config(repo_path: pathlib.Path) -> ConfigStatus:
    """Inspects if qodana.yaml is present in the repository root."""
    config_file = repo_path / "qodana.yaml"
    yml_file = repo_path / "qodana.yml"
    workflow_file = repo_path / ".github" / "workflows" / "qodana.yml"

    is_dotnet = is_dotnet_repo(repo_path)
    if config_file.exists() or yml_file.exists():
        found = config_file if config_file.exists() else yml_file
        return ConfigStatus(
            "CONFIGURED",
            is_aligned=workflow_file.exists() or not is_dotnet,
            file=str(found),
            workflow_present=workflow_file.exists(),
            applicable=is_dotnet,
        )
    return ConfigStatus(
        "APPLICABLE" if is_dotnet else "OPTIONAL",
        is_aligned=not is_dotnet,  # Considered missing only if .NET repo
        applicable=is_dotnet,
    )


def align_qodana_config(repo_path: pathlib.Path, dry_run: bool = False) -> ConfigChange:
    """Writes canonical qodana.yaml and .github/workflows/qodana.yml if .NET repo."""
    if not is_dotnet_repo(repo_path):
        return ConfigChange(False, "not a .NET repository (skipped)")

    config_file = repo_path / "qodana.yaml"
    workflow_file = repo_path / ".github" / "workflows" / "qodana.yml"

    created: list[str] = []
    if not config_file.exists() and not (repo_path / "qodana.yml").exists():
        if not dry_run:
            config_file.write_text(DEFAULT_QODANA_YAML, encoding="utf-8")
        created.append("qodana.yaml")

    if not workflow_file.exists():
        if not dry_run:
            workflow_file.parent.mkdir(parents=True, exist_ok=True)
            workflow_file.write_text(DEFAULT_QODANA_WORKFLOW_DOTNET, encoding="utf-8")
        created.append(".github/workflows/qodana.yml")

    if not created:
        return ConfigChange(False, "already configured")

    action = "would create" if dry_run else "created"
    return ConfigChange(True, f"{action} {', '.join(created)}")


@dataclass(slots=True, frozen=True)
class ToolStatus:
    tool: str
    status: ConfigStatus


@dataclass(slots=True, frozen=True)
class ReviewConfigs:
    coderabbit: ConfigStatus
    codecov: ConfigStatus
    deepsource: ConfigStatus
    qodana: ConfigStatus

    def by_tool(self) -> list[ToolStatus]:
        return [
            ToolStatus("coderabbit", self.coderabbit),
            ToolStatus("codecov", self.codecov),
            ToolStatus("deepsource", self.deepsource),
            ToolStatus("qodana", self.qodana),
        ]


def inspect_review_configs(repo_path: pathlib.Path) -> ReviewConfigs:
    """Inspects all standard code review tool configurations in repository root."""
    return ReviewConfigs(
        coderabbit=inspect_coderabbit_config(repo_path),
        codecov=inspect_codecov_config(repo_path),
        deepsource=inspect_deepsource_config(repo_path),
        qodana=inspect_qodana_config(repo_path),
    )


def _coderabbit_auto_review_disabled(config_file: pathlib.Path) -> bool:
    """True only if the YAML sets reviews.auto_review.enabled to exactly false."""
    try:
        text = config_file.read_text(encoding="utf-8")
    except OSError:
        return False
    parsed = try_decode_yaml(_CodeRabbitYaml, text)
    auto = parsed.reviews.auto_review if parsed and parsed.reviews else None
    return auto is not None and auto.enabled is False


def align_review_configs(
    repo_path: pathlib.Path,
    org: str = "rtkelly-labs",
    dry_run: bool = False,
    config: PolicyConfig | None = None,
) -> list[ToolChange]:
    """Aligns all standard code review tool configurations in repository root."""
    cfg = config or load_policy_config()
    repo_name = repo_path.name
    results: list[ToolChange] = []

    # CodeRabbit: deploy suppressor if disallowed to prevent unwanted reviews if granted all-repos
    if is_repo_disallowed(repo_name, org, "coderabbitai", cfg):
        label = "CodeRabbit (.coderabbit.yaml)"
        existing = [f for f in (repo_path / ".coderabbit.yaml", repo_path / ".coderabbit.yml") if f.exists()]
        if not existing:
            if not dry_run:
                (repo_path / ".coderabbit.yaml").write_text(DEFAULT_CODERABBIT_SUPPRESSOR_YAML, encoding="utf-8")
            results.append(ToolChange(label, True, "disallowed by policy: configured suppressor (auto_review: false)"))
        elif _coderabbit_auto_review_disabled(existing[0]):
            results.append(ToolChange(label, False, "disallowed by policy: suppressor already present"))
        else:
            # Never overwrite an existing config silently; surface the conflict for a human.
            results.append(
                ToolChange(
                    label,
                    False,
                    f"CONFLICT: disallowed by policy but {existing[0].name} does not set "
                    "reviews.auto_review.enabled: false; left unchanged",
                )
            )
    else:
        change = align_coderabbit_config(repo_path, dry_run=dry_run)
        results.append(ToolChange("CodeRabbit (.coderabbit.yaml)", change.changed, change.message))

    # Codecov
    if is_repo_disallowed(repo_name, org, "codecov", cfg):
        results.append(ToolChange("Codecov (codecov.yml)", False, "disallowed by policy: skipped"))
    else:
        change = align_codecov_config(repo_path, dry_run=dry_run)
        results.append(ToolChange("Codecov (codecov.yml)", change.changed, change.message))

    # DeepSource
    if is_repo_disallowed(repo_name, org, "deepsource-io", cfg):
        results.append(ToolChange("DeepSource (.deepsource.toml)", False, "disallowed by policy: skipped"))
    else:
        change = align_deepsource_config(repo_path, dry_run=dry_run)
        results.append(ToolChange("DeepSource (.deepsource.toml)", change.changed, change.message))

    # Qodana (.NET specific, or disallowed)
    if is_repo_disallowed(repo_name, org, "qodana", cfg):
        results.append(ToolChange("Qodana (qodana.yaml & workflow)", False, "disallowed by policy: skipped"))
    else:
        change = align_qodana_config(repo_path, dry_run=dry_run)
        results.append(ToolChange("Qodana (qodana.yaml & workflow)", change.changed, change.message))

    return results

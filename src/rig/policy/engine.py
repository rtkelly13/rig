"""Read-only policy evaluation for estate commands and CLI surfaces."""

from __future__ import annotations

import fnmatch
import re
import shlex
import tomllib
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

from pydantic import StrictStr

from rig.policy.models import CommandRisk, CommandRule, Policy, ProjectCommand
from rig.typed_json import Lenient, decode, load_json, try_decode


@dataclass(slots=True, frozen=True)
class _RuleEntry:
    pattern: str
    rationale: str = ""
    exclude_systems: tuple[str, ...] = ()


@dataclass(slots=True, frozen=True)
class _SkillsSection:
    allowlist_file: str | None = None
    allow: tuple[str, ...] = ()


@dataclass(slots=True, frozen=True)
class _PolicyFile:
    commands: dict[CommandRisk, tuple[str | _RuleEntry, ...]]
    skills: _SkillsSection = field(default_factory=_SkillsSection)


def get_policy_path() -> Path:
    """Return the checked-in policy path."""
    return Path(__file__).resolve().parents[3] / "config" / "cli-allowlists.json"


def load_policy(path: Path | None = None) -> Policy:
    """Load and validate the estate policy, failing closed on malformed rules.

    A misshapen rule raises ``pydantic.ValidationError`` rather than being dropped:
    a silently missing deny rule would let a broader allow rule match instead.
    """
    policy_path = path or get_policy_path()
    policy_file = decode(_PolicyFile, load_json(policy_path.read_text(encoding="utf-8")))
    rules = [
        CommandRule(pattern=entry, risk=risk)
        if isinstance(entry, str)
        else CommandRule(
            pattern=entry.pattern,
            risk=risk,
            rationale=entry.rationale,
            exclude_systems=entry.exclude_systems,
        )
        for risk in CommandRisk
        for entry in policy_file.commands.get(risk, ())
    ]
    allowlist = policy_file.skills.allow
    if policy_file.skills.allowlist_file is not None:
        allowlist_text = (policy_path.parent / policy_file.skills.allowlist_file).read_text(encoding="utf-8")
        allowlist = decode(tuple[str, ...], load_json(allowlist_text))
    return Policy(skills_allowlist=list(allowlist), command_rules=rules)


def check_command(command: str, policy: Policy | None = None) -> CommandRisk:
    """Classify a command using the most specific matching policy rule.

    Denied rules win, followed by external, local, and safe rules. Unknown
    commands are denied so a new command cannot silently bypass review.
    """
    normalized = " ".join(command.strip().split())
    if not _is_direct_command(normalized):
        return CommandRisk.DENIED
    matches = [rule for rule in (policy or load_policy()).command_rules if fnmatch.fnmatch(normalized, rule.pattern)]
    if not matches:
        return CommandRisk.DENIED
    precedence = {
        CommandRisk.DENIED: 3,
        CommandRisk.EXTERNAL: 2,
        CommandRisk.LOCAL: 1,
        CommandRisk.SAFE: 0,
    }
    return max(matches, key=lambda rule: (precedence[rule.risk], len(rule.pattern))).risk


def _is_direct_command(command: str) -> bool:
    """Reject shell composition before glob policy matching can hide it."""
    if not command or any(marker in command for marker in ("$(", "`", "\n", "\r")):
        return False
    try:
        lexer = shlex.shlex(command, posix=True, punctuation_chars=";&|<>")
        lexer.whitespace_split = True
        tokens = list(lexer)
    except ValueError:
        return False
    control_tokens = {";", "&", "&&", "|", "||", ">", ">>", "<", "<<"}
    return bool(tokens) and not any(token in control_tokens for token in tokens)


def _command_risk(name: str, invocation: str) -> CommandRisk:
    text = f"{name} {invocation}".lower()
    if any(word in text for word in ("publish", "deploy", "destroy", "push", "rm ", "reset", "clean")):
        return CommandRisk.EXTERNAL
    if any(
        word in text
        for word in (
            "format",
            "--write",
            "--fix",
            "update",
            "prepare",
            "build",
            "coverage",
            "snapshot",
            "watch",
            "e2e",
            "live",
            " dev",
            " start",
            " serve",
        )
    ):
        return CommandRisk.LOCAL
    if any(word in text for word in ("test", "lint", "check", "typecheck", "status", "help", "validate")):
        return CommandRisk.SAFE
    return CommandRisk.LOCAL


#: Script name -> its command; a value that is not a string reads as None and is skipped.
type _Scripts = Lenient[dict[str, Lenient[StrictStr]]]


@dataclass(frozen=True, slots=True)
class _PackageJson:
    scripts: _Scripts = None


@dataclass(frozen=True, slots=True)
class _ProjectTable:
    scripts: _Scripts = None


@dataclass(frozen=True, slots=True)
class _PyProject:
    project: Lenient[_ProjectTable] = None


def audit_project_commands(root: Path) -> list[ProjectCommand]:
    """Discover package scripts and Python entrypoints across direct child repos."""
    projects = [root, *sorted(path for path in root.iterdir() if path.is_dir() and not path.name.startswith("."))]
    results: list[ProjectCommand] = []
    for project in projects:
        package_file = project / "package.json"
        if package_file.is_file():
            package = try_decode(_PackageJson, load_json(package_file.read_text(encoding="utf-8")))
            for name, invocation in (package.scripts if package is not None and package.scripts else {}).items():
                if invocation is not None:
                    results.append(
                        ProjectCommand(
                            project=project.name,
                            manifest=package_file,
                            name=name,
                            invocation=f"pnpm {name}",
                            risk=_command_risk(name, invocation),
                            source=invocation,
                        )
                    )
        pyproject = project / "pyproject.toml"
        if pyproject.is_file():
            manifest = try_decode(_PyProject, tomllib.loads(pyproject.read_text(encoding="utf-8")))
            project_table = manifest.project if manifest is not None else None
            scripts = project_table.scripts if project_table is not None else None
            for name, target in (scripts or {}).items():
                if target is not None:
                    results.append(
                        ProjectCommand(
                            project=project.name,
                            manifest=pyproject,
                            name=name,
                            invocation=f"uv run {name}",
                            risk=_command_risk(name, target),
                            source=target,
                        )
                    )
    return sorted(results, key=lambda item: (item.project, item.invocation))


def summarize_history(history_path: Path | None = None) -> Counter[str]:
    """Summarize known command families without returning raw history lines."""
    path = history_path or Path.home() / ".zsh_history"
    counts: Counter[str] = Counter()
    if not path.is_file():
        return counts
    patterns = {
        "uv validation": re.compile(r"\buv run (help|status|pytest|ruff|pyright|check)\b"),
        "pnpm validation": re.compile(r"\bpnpm (test|typecheck|lint|build|check|ci)\b"),
        "git inspection": re.compile(r"\bgit (status|diff|log|show|branch)\b"),
        "workspace inspection": re.compile(r"\bworkspace (skills|status|audit|projects)\b"),
    }
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        for family, pattern in patterns.items():
            if pattern.search(line):
                counts[family] += 1
                break
    return counts

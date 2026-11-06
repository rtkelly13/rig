"""Project the estate command policy into native agent permission files."""

from __future__ import annotations

import copy
import fnmatch
import json
import shlex
from dataclasses import dataclass
from pathlib import Path

from rig.policy.models import CommandRisk, Policy, ProjectionResult, ProjectionStatus
from rig.typed_json import load_json, try_decode

_BEGIN_MARKER = "# BEGIN shared-utilities policy"
_END_MARKER = "# END shared-utilities policy"


@dataclass(slots=True, frozen=True)
class CommandPrefix:
    """The literal tokens a policy glob starts with, and whether anything may follow them."""

    tokens: tuple[str, ...]
    wildcard: bool


@dataclass(frozen=True)
class NativeProjection:
    """A native permission writer for one supported agent surface."""

    system: str
    path: Path | None

    def apply(self, policy: Policy, dry_run: bool = False) -> ProjectionResult:
        if self.path is None:
            return ProjectionResult(
                self.system,
                None,
                ProjectionStatus.UNSUPPORTED,
                "No persistent command permission file is exposed by this CLI.",
            )

        try:
            if self.system == "codex":
                return _apply_codex(self.path, policy, dry_run)
            data = _load_json(self.path)
            rendered = _render_json_config(self.system, data, policy)
            if rendered == data:
                return ProjectionResult(self.system, self.path, ProjectionStatus.UNCHANGED, "Already up to date.")
            if dry_run:
                return ProjectionResult(self.system, self.path, ProjectionStatus.DRY_RUN, "Would update native rules.")
            self.path.parent.mkdir(parents=True, exist_ok=True)
            self.path.write_text(json.dumps(rendered, indent=2) + "\n", encoding="utf-8")
            return ProjectionResult(self.system, self.path, ProjectionStatus.APPLIED, "Updated native rules.")
        except (OSError, UnicodeError, ValueError, json.JSONDecodeError) as exc:
            return ProjectionResult(self.system, self.path, ProjectionStatus.FAILED, str(exc))


def get_native_projection(system: str, home: Path | None = None) -> NativeProjection:
    """Resolve the native permission path for an agent without touching disk."""
    base_home = home or Path.home()
    if system == "claude":
        return NativeProjection(system, base_home / ".claude" / "settings.json")
    if system == "codex":
        return NativeProjection(system, base_home / ".codex" / "rules" / "default.rules")
    if system == "opencode":
        config_dir = base_home / ".config" / "opencode"
        for name in ("opencode.json", "opencode.jsonc", "config.json"):
            candidate = config_dir / name
            if candidate.is_file():
                return NativeProjection(system, candidate)
        return NativeProjection(system, config_dir / "opencode.json")
    if system == "antigravity":
        return NativeProjection(system, base_home / ".gemini" / "antigravity-cli" / "settings.json")
    raise ValueError(f"Unknown native permission system: {system}")


def project_native_permissions(
    policy: Policy,
    systems: list[str] | None = None,
    home: Path | None = None,
    dry_run: bool = False,
) -> list[ProjectionResult]:
    """Apply policy to selected agent surfaces, preserving unmanaged settings."""
    target_systems = systems or ["opencode", "claude", "codex", "antigravity"]
    return [get_native_projection(system, home).apply(policy, dry_run=dry_run) for system in target_systems]


def command_prefix(pattern: str) -> CommandPrefix | None:
    """Convert a policy glob into a native command prefix and wildcard flag."""
    wildcard = pattern.rstrip().endswith("*")
    text = pattern.rstrip()[:-1].rstrip() if wildcard else pattern.strip()
    if "*" in text or "?" in text:
        return None
    try:
        tokens = shlex.split(text)
    except ValueError:
        return None
    return CommandPrefix(tuple(tokens), wildcard) if tokens else None


def _rules_for(policy: Policy, risk: CommandRisk, system: str) -> list[CommandPrefix]:
    shadows = _unexpressible_denies(policy, system) if risk is CommandRisk.SAFE else []
    return [
        prefix
        for rule in policy.command_rules
        if rule.risk is risk and system not in rule.exclude_systems
        if (prefix := command_prefix(rule.pattern))
        if not _shadowed(prefix, shadows, system)
    ]


def _excluded_rules(policy: Policy, risk: CommandRisk, system: str) -> list[CommandPrefix]:
    """Rules kept off ``system``; a copy an earlier sync wrote is removed, since merges only add.

    That is the rules opted out with ``exclude_systems``, plus, for allow rules, any a deny the
    surface cannot express would carve an exception out of (`_shadowed`).
    """
    shadows = _unexpressible_denies(policy, system) if risk is CommandRisk.SAFE else []
    return [
        prefix
        for rule in policy.command_rules
        if rule.risk is risk
        if (prefix := command_prefix(rule.pattern))
        if system in rule.exclude_systems or _shadowed(prefix, shadows, system)
    ]


def _unexpressible_denies(policy: Policy, system: str) -> list[str]:
    """Deny globs that apply to ``system`` but have no prefix form (interior ``*``): prefix-only surfaces drop them."""
    return [
        rule.pattern
        for rule in policy.command_rules
        if rule.risk is CommandRisk.DENIED and system not in rule.exclude_systems
        if command_prefix(rule.pattern) is None
    ]


#: Surfaces whose rules always match as prefixes: an exact policy command still allows anything after it.
_PREFIX_MATCH_ONLY = frozenset({"codex", "antigravity"})


def _shadowed(prefix: CommandPrefix, denies: list[str], system: str) -> bool:
    """Whether allowing ``prefix`` would also allow a command one of ``denies`` forbids.

    A prefix-only surface cannot carry those denies, so projecting the allow alone would turn the
    deny into an allow (``gh stack sync *`` without ``gh stack sync *--prune*``). Such an allow is
    held back and the surface keeps prompting. An exact command is checked against each glob; a
    wildcard prefix conservatively overlaps any glob whose literal head is compatible with it.
    On Codex and Antigravity every rule is a prefix, so an exact command counts as a wildcard there.
    """
    command = " ".join(prefix.tokens)
    wildcard = prefix.wildcard or system in _PREFIX_MATCH_ONLY
    for deny in denies:
        if not wildcard:
            if fnmatch.fnmatch(command, deny):
                return True
            continue
        head = deny.split("*", 1)[0]
        head_tokens = head.split() if head.endswith(" ") else head.split()[:-1]
        shorter = min(len(head_tokens), len(prefix.tokens))
        if list(prefix.tokens[:shorter]) == head_tokens[:shorter]:
            return True
    return False


def _remove_all(target: list[object], values: list[str]) -> None:
    unwanted = set(values)
    target[:] = [item for item in target if not (isinstance(item, str) and item in unwanted)]


def _render_json_config(system: str, data: dict[str, object], policy: Policy) -> dict[str, object]:
    rendered = copy.deepcopy(data)
    if system in {"claude", "antigravity"}:
        permissions = _object_at(rendered, "permissions", "permissions")
        allow = _array_at(permissions, "allow", "permissions.allow")
        deny = _array_at(permissions, "deny", "permissions.deny")
        if system == "claude":
            for target, risk in ((allow, CommandRisk.SAFE), (deny, CommandRisk.DENIED)):
                _remove_all(target, _claude_rules(policy, risk, excluded=True))
                _merge_unique(target, _claude_rules(policy, risk, excluded=False))
        else:
            allow[:] = _migrate_antigravity_rules(allow)
            deny[:] = _migrate_antigravity_rules(deny)
            for target, risk in ((allow, CommandRisk.SAFE), (deny, CommandRisk.DENIED)):
                _remove_all(target, _antigravity_rules(_excluded_rules(policy, risk, system)))
                _merge_unique(target, _antigravity_rules(_rules_for(policy, risk, system)))
        return rendered

    if system == "opencode":
        permission = _object_at(rendered, "permission", "permission")
        # In v2, OpenCode's shell tool action is named "shell", while v1 is "bash".
        # We project into both so all versions and migration paths resolve cleanly.
        shell = _object_at(permission, "shell", "permission.shell")
        bash = _object_at(permission, "bash", "permission.bash")
        shell.setdefault("*", "ask")
        bash.setdefault("*", "ask")
        for risk, effect in (
            (CommandRisk.SAFE, "allow"),
            (CommandRisk.LOCAL, "ask"),
            (CommandRisk.EXTERNAL, "ask"),
            (CommandRisk.DENIED, "deny"),
        ):
            for prefix in _excluded_rules(policy, risk, system):
                rule_str = _opencode_rule(prefix)
                if shell.get(rule_str) == effect:
                    del shell[rule_str]
                if bash.get(rule_str) == effect:
                    del bash[rule_str]
            for prefix in _rules_for(policy, risk, system):
                rule_str = _opencode_rule(prefix)
                shell.setdefault(rule_str, effect)
                bash.setdefault(rule_str, effect)

        # Allow access to the monorepo estate root so cross-repo sibling traversal
        # doesn't trip OpenCode's external_directory circuit-breaker
        ext_dir = _object_at(permission, "external_directory", "permission.external_directory")
        ext_dir.setdefault("~/code/**", "allow")
        return rendered

    raise ValueError(f"JSON projection is not supported for {system}")


def _object_at(parent: dict[str, object], key: str, label: str) -> dict[str, object]:
    """The object under ``key``, created when absent; set back on ``parent`` so edits to it land there."""
    raw = parent.get(key)
    child = {} if raw is None else try_decode(dict[str, object], raw)
    if child is None:
        raise ValueError(f"{label} must be an object")
    parent[key] = child
    return child


def _array_at(parent: dict[str, object], key: str, label: str) -> list[object]:
    """The array under ``key``, created when absent; set back on ``parent`` so edits to it land there."""
    raw = parent.get(key)
    child = [] if raw is None else try_decode(list[object], raw)
    if child is None:
        raise ValueError(f"{label} must be an array")
    parent[key] = child
    return child


def _load_json(path: Path) -> dict[str, object]:
    if not path.is_file():
        return {}
    data = try_decode(dict[str, object], load_json(path.read_text(encoding="utf-8")))
    if data is None:
        raise ValueError("native config root must be an object")
    return data


def _merge_unique(target: list[object], values: list[str]) -> None:
    existing = {item for item in target if isinstance(item, str)}
    for value in values:
        if value not in existing:
            target.append(value)
            existing.add(value)


def _claude_rule(prefix: CommandPrefix) -> str:
    command = " ".join(prefix.tokens)
    return f"Bash({command}:*)" if prefix.wildcard else f"Bash({command})"


def claude_rule(pattern: str) -> str | None:
    """Render a policy glob as a Claude Code ``Bash(...)`` rule.

    A literal or trailing-``*`` glob becomes the prefix form every surface shares. A glob
    with an interior ``*`` (``gh pr merge * --auto --squash``) has no prefix form, so the
    prefix-only surfaces (Codex, Antigravity, OpenCode's projection) skip it; Claude Code
    matches ``*`` anywhere in a rule, so it is projected there verbatim.
    """
    if prefix := command_prefix(pattern):
        return _claude_rule(prefix)
    text = " ".join(pattern.split())
    if not text or "?" in text or any(quote in text for quote in "\"'"):
        return None
    return f"Bash({text})"


def _claude_rules(policy: Policy, risk: CommandRisk, *, excluded: bool) -> list[str]:
    return [
        rule_text
        for rule in policy.command_rules
        if rule.risk is risk and ("claude" in rule.exclude_systems) is excluded
        if (rule_text := claude_rule(rule.pattern))
    ]


def _opencode_rule(prefix: CommandPrefix) -> str:
    command = " ".join(prefix.tokens)
    return f"{command} *" if prefix.wildcard else command


def _migrate_antigravity_rules(rules: list[object]) -> list[object]:
    """Rewrite legacy ``unsandboxed(...)`` rules as ``command(...)``, deduplicated.

    Antigravity deprecated ``unsandboxed`` and now reports every rule written in it
    as invalid when it loads the settings file. Rules that already migrated are kept
    once, so a second sync is idempotent and cannot grow the list.
    """
    migrated: list[object] = []
    seen: set[str] = set()
    for rule in rules:
        if isinstance(rule, str):
            if rule.startswith("unsandboxed(") and rule.endswith(")"):
                inner = rule[len("unsandboxed(") : -1]
                rule = f"command({inner})"
            if rule not in seen:
                seen.add(rule)
                migrated.append(rule)
        else:
            migrated.append(rule)
    return migrated


def _antigravity_rules(prefixes: list[CommandPrefix]) -> list[str]:
    return [f"command({' '.join(prefix.tokens)})" for prefix in prefixes]


def _codex_rule(prefix: CommandPrefix, decision: str, rationale: str) -> str:
    pattern = ", ".join(json.dumps(token) for token in prefix.tokens)
    return f"prefix_rule(\n    pattern=[{pattern}],\n    decision={decision!r},\n    justification={rationale!r},\n)"


def _apply_codex(path: Path, policy: Policy, dry_run: bool) -> ProjectionResult:
    existing = path.read_text(encoding="utf-8") if path.is_file() else ""
    blocks: list[str] = []
    rationales = {
        CommandRisk.SAFE: "Estate validation command.",
        CommandRisk.LOCAL: "Local mutation requires approval.",
        CommandRisk.EXTERNAL: "External side effect requires approval.",
        CommandRisk.DENIED: "Blocked by the estate policy.",
    }
    decisions = {
        CommandRisk.SAFE: "allow",
        CommandRisk.LOCAL: "prompt",
        CommandRisk.EXTERNAL: "prompt",
        CommandRisk.DENIED: "forbidden",
    }
    for risk in CommandRisk:
        for prefix in _rules_for(policy, risk, "codex"):
            blocks.append(_codex_rule(prefix, decisions[risk], rationales[risk]))
    managed = f"{_BEGIN_MARKER}\n{chr(10).join(blocks)}\n{_END_MARKER}"
    if _BEGIN_MARKER in existing and _END_MARKER in existing:
        before, remainder = existing.split(_BEGIN_MARKER, 1)
        _old, after = remainder.split(_END_MARKER, 1)
        updated = f"{before.rstrip()}\n\n{managed}\n{after.lstrip()}"
    else:
        updated = f"{existing.rstrip()}\n\n{managed}\n" if existing.strip() else f"{managed}\n"
    if updated == existing:
        return ProjectionResult("codex", path, ProjectionStatus.UNCHANGED, "Already up to date.")
    if dry_run:
        return ProjectionResult("codex", path, ProjectionStatus.DRY_RUN, "Would update native rules.")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(updated, encoding="utf-8")
    return ProjectionResult("codex", path, ProjectionStatus.APPLIED, "Updated managed native rules block.")

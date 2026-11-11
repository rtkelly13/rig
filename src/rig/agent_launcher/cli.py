"""The ``agent`` launcher — start your preferred AI coding agent CLI.

Reads the preferred agent from ``~/.workspace/config.json`` or command-line dispatch flags
(-agy, -ccc, -agywsl, -oc, -codex) and runs it forwarding any extra arguments.
"""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import sys
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import ClassVar

from pydantic import BaseModel, ConfigDict, Field
from rich.console import Console

from rig.typed_json import try_decode_json


class ClaudeCodeDefaults(BaseModel):
    model_config: ClassVar[ConfigDict] = ConfigDict(populate_by_name=True)

    model: str | None = None
    remote_control: bool = False


class AgentDefaults(BaseModel):
    model_config: ClassVar[ConfigDict] = ConfigDict(populate_by_name=True)

    claude_code: ClaudeCodeDefaults | None = Field(default=None, alias="claude-code")


class AgentConfig(BaseModel):
    model_config: ClassVar[ConfigDict] = ConfigDict(populate_by_name=True)

    preferred: str | None = None
    defaults: AgentDefaults | None = None
    danger: bool = False


class ToolsConfig(BaseModel):
    agent: AgentConfig | None = None


class WorkspaceConfigFile(BaseModel):
    tools: ToolsConfig | None = None


@dataclass(slots=True, frozen=True)
class AgentMeta:
    display_name: str
    executable: str


SUPPORTED_AGENTS: dict[str, AgentMeta] = {
    "antigravity": AgentMeta(display_name="Antigravity CLI", executable="agy"),
    "claude-code": AgentMeta(display_name="Claude Code", executable="claude"),
    "agywsl": AgentMeta(display_name="Antigravity CLI (WSL Ubuntu)", executable="wsl"),
    "opencode": AgentMeta(display_name="OpenCode", executable="opencode"),
    "codex": AgentMeta(display_name="OpenAI Codex", executable="codex"),
    "github-copilot": AgentMeta(display_name="GitHub Copilot", executable="copilot"),
}

VALID_AGENT_IDS_STR = ", ".join(SUPPORTED_AGENTS.keys())

#: Other names an agent's executable goes by, tried in order when it is not on PATH. OpenCode 2
#: installs as ``opencode``, except beside Orca, which names it ``opencode2``.
EXECUTABLE_ALTERNATES: dict[str, tuple[str, ...]] = {"opencode": ("opencode2",)}

AGENT_DANGER_FLAGS: dict[str, str] = {
    "antigravity": "--dangerously-skip-permissions",
    "claude-code": "--dangerously-skip-permissions",
    "agywsl": "--dangerously-skip-permissions",
    "opencode": "--auto",
    "codex": "--dangerously-bypass-approvals-and-sandbox",
    "github-copilot": "--allow-all",
}

AGENT_DANGER_EQUIVALENTS: dict[str, set[str]] = {
    "antigravity": {"--dangerously-skip-permissions"},
    "claude-code": {"--dangerously-skip-permissions"},
    "agywsl": {"--dangerously-skip-permissions"},
    "opencode": {"--auto"},
    "codex": {"--dangerously-bypass-approvals-and-sandbox", "--yolo"},
    "github-copilot": {"--allow-all", "--yolo"},
}

USAGE = f"""\
Usage: agent [-agy | -ccc | -agywsl | -oc | -codex | -cpl] [-dng] [-r]
             [--use <agent-id>] [--print-config] [<agent args>...]

Launches your preferred AI coding agent or the specified agent.

Agent shortcuts:
  -agy               Launch Antigravity CLI on Windows (agy)
  -ccc               Launch Claude Code (claude)
  -agywsl            Launch Antigravity CLI in WSL2 Ubuntu
  -oc, -opencode     Launch OpenCode
  -codex             Launch OpenAI Codex
  -cpl, -copilot     Launch GitHub Copilot CLI

Options:
  -dng, --dng        Dangerously run with permissions skipped / auto-approved
                     for the target agent.
  -r, --remote-control
                     Enable Remote Control for this run (Claude Code only), so
                     the session is reachable from the Claude mobile app / claude.ai.
  --use <agent-id>   Launch a specific agent for this run instead of the stored
                     preference. One of: {VALID_AGENT_IDS_STR}.
  --print-config     Print the resolved agent and exact command, then exit.
  -h, --help         Show this help and exit.

Note: `-r` and `-dng` are consumed by this launcher, not forwarded. Use the long form
`--resume` to resume a Claude Code session.

Any other arguments are forwarded verbatim to the underlying agent CLI.
"""


def canonical_agent_id(agent_id: str) -> str:
    """Map legacy or convenience aliases to canonical agent IDs."""
    if agent_id == "gemini":
        return "antigravity"
    if agent_id == "claude":
        return "claude-code"
    if agent_id == "copilot":
        return "github-copilot"
    return agent_id


def get_workspace_config_path() -> Path:
    """Return path to workspace config.json, honoring WORKSPACE_HOME override."""
    override = os.environ.get("WORKSPACE_HOME")
    base = Path(override).expanduser() if override else Path.home() / ".workspace"
    return base / "config.json"


def load_agent_config(config_path: Path | None = None) -> AgentConfig:
    """Load agent settings from ~/.workspace/config.json, returning empty config if missing."""
    path = config_path or get_workspace_config_path()
    if not path.is_file():
        return AgentConfig()
    try:
        cfg = try_decode_json(WorkspaceConfigFile, path.read_bytes())
        if cfg and cfg.tools and cfg.tools.agent:
            return cfg.tools.agent
    except OSError:
        pass
    return AgentConfig()


@dataclass(slots=True, frozen=True)
class ParsedArgs:
    """Result of splitting launcher-only flags from passthrough args."""

    agent_override: str | None
    print_config: bool
    show_help: bool
    remote_control: bool
    danger: bool
    passthrough: list[str]


def parse_args(argv: Sequence[str]) -> ParsedArgs:
    """Consume leading launcher flags; stop at first non-launcher token leaving remainder as passthrough."""
    agent_override: str | None = None
    print_config = False
    show_help = False
    remote_control = False
    danger = False
    rest = list(argv)

    while rest:
        tok = rest[0]
        if tok in ("-h", "--help"):
            show_help = True
            rest.pop(0)
            continue
        if tok in ("-r", "--remote-control"):
            remote_control = True
            rest.pop(0)
            continue
        if tok in ("-dng", "--dng", "--dangerously-skip-permissions"):
            danger = True
            rest.pop(0)
            continue
        if tok == "--print-config":
            print_config = True
            rest.pop(0)
            continue
        if tok == "-agy":
            agent_override = "antigravity"
            rest.pop(0)
            continue
        if tok == "-ccc":
            agent_override = "claude-code"
            rest.pop(0)
            continue
        if tok == "-agywsl":
            agent_override = "agywsl"
            rest.pop(0)
            continue
        if tok in ("-oc", "-opencode"):
            agent_override = "opencode"
            rest.pop(0)
            continue
        if tok == "-codex":
            agent_override = "codex"
            rest.pop(0)
            continue
        if tok in ("-cpl", "-copilot"):
            agent_override = "github-copilot"
            rest.pop(0)
            continue
        if tok == "--use":
            if len(rest) < 2:
                raise ValueError("--use requires an agent id")
            agent_override = rest[1]
            rest = rest[2:]
            continue
        if tok.startswith("--use="):
            agent_override = tok.split("=", 1)[1]
            rest.pop(0)
            continue
        break

    return ParsedArgs(
        agent_override=agent_override,
        print_config=print_config,
        show_help=show_help,
        remote_control=remote_control,
        danger=danger,
        passthrough=rest,
    )


def windows_to_wsl_path(path: Path | str) -> str:
    """Translate a Windows path to a POSIX WSL path (/mnt/<drive>/...)."""
    raw = str(path).replace("\\", "/")
    match = re.match(r"^([a-zA-Z]):/(.*)", raw)
    if match:
        drive = match.group(1).lower()
        rest = match.group(2)
        return f"/mnt/{drive}/{rest}"

    if raw.startswith("/"):
        return raw

    resolved = Path(path).resolve()
    resolved_str = str(resolved).replace("\\", "/")
    match_resolved = re.match(r"^([a-zA-Z]):/(.*)", resolved_str)
    if match_resolved:
        drive = match_resolved.group(1).lower()
        rest = match_resolved.group(2)
        return f"/mnt/{drive}/{rest}"

    return resolved.as_posix()


@dataclass(slots=True, frozen=True)
class AgentResolution:
    agent_id: str
    display_name: str
    executable: str
    full_command: list[str]


def resolve_agent_command(
    parsed: ParsedArgs,
    *,
    config: AgentConfig | None = None,
    cwd: Path | None = None,
) -> AgentResolution:
    """Resolve the target agent, executable name, and complete command line."""
    cfg = config if config is not None else load_agent_config()

    if parsed.agent_override is not None:
        agent_id = canonical_agent_id(parsed.agent_override)
        if agent_id not in SUPPORTED_AGENTS:
            raise ValueError(f"Unknown agent id {parsed.agent_override!r}. Valid ids: {VALID_AGENT_IDS_STR}.")
    else:
        pref = cfg.preferred
        canonical = canonical_agent_id(pref) if pref else None
        agent_id = canonical if canonical and canonical in SUPPORTED_AGENTS else "claude-code"

    meta = SUPPORTED_AGENTS[agent_id]
    passthrough = list(parsed.passthrough)
    is_danger = parsed.danger or bool(cfg.danger)
    danger_present = any(flag in passthrough for flag in AGENT_DANGER_EQUIVALENTS.get(agent_id, set()))

    if agent_id == "claude-code":
        cmd_args: list[str] = []
        remote = parsed.remote_control
        if not remote and cfg.defaults and cfg.defaults.claude_code:
            remote = cfg.defaults.claude_code.remote_control
        if remote and "--remote-control" not in passthrough:
            cmd_args.append("--remote-control")

        if is_danger and not danger_present:
            cmd_args.append("--dangerously-skip-permissions")

        has_model_flag = any(arg == "--model" or arg.startswith("--model=") for arg in passthrough)
        if not has_model_flag:
            model = "opus"
            if cfg.defaults and cfg.defaults.claude_code and cfg.defaults.claude_code.model:
                model = cfg.defaults.claude_code.model
            cmd_args.extend(["--model", model])

        return AgentResolution(
            agent_id=agent_id,
            display_name=meta.display_name,
            executable=meta.executable,
            full_command=[meta.executable, *cmd_args, *passthrough],
        )

    if agent_id == "agywsl":
        wsl_path = windows_to_wsl_path(cwd or Path.cwd())
        has_mode_flag = any(arg == "--mode" or arg.startswith("--mode=") for arg in passthrough)
        mode_args = ["--mode", "auto"] if not has_mode_flag else []
        danger_args = ["--dangerously-skip-permissions"] if is_danger and not danger_present else []
        cmd = ["wsl", "-d", "Ubuntu", "--cd", wsl_path, "agy", *mode_args, *danger_args, *passthrough]
        return AgentResolution(
            agent_id=agent_id,
            display_name=meta.display_name,
            executable=meta.executable,
            full_command=cmd,
        )

    extra_args: list[str] = []
    if is_danger and not danger_present and agent_id in AGENT_DANGER_FLAGS:
        extra_args.append(AGENT_DANGER_FLAGS[agent_id])

    return AgentResolution(
        agent_id=agent_id,
        display_name=meta.display_name,
        executable=meta.executable,
        full_command=[meta.executable, *extra_args, *passthrough],
    )


def main(
    argv: Sequence[str] | None = None,
    *,
    config: AgentConfig | None = None,
    cwd: Path | None = None,
) -> int:
    """Launcher CLI entry point."""
    console = Console()
    err_console = Console(stderr=True)
    raw_args = list(sys.argv[1:] if argv is None else argv)

    if raw_args and raw_args[0] in ("models", "--models"):
        from rig import csharp_cli

        return csharp_cli.run(["models", *raw_args[1:]])

    try:
        parsed = parse_args(raw_args)
    except ValueError as exc:
        err_console.print(f"error: {exc}", highlight=False)
        return 2

    if parsed.show_help:
        console.print(USAGE, highlight=False, end="")
        return 0

    try:
        resolution = resolve_agent_command(parsed, config=config, cwd=cwd)
    except ValueError as exc:
        err_console.print(f"error: {exc}", highlight=False)
        return 2

    names = (resolution.executable, *EXECUTABLE_ALTERNATES.get(resolution.executable, ()))
    found = next(((name, path) for name in names if (path := shutil.which(name))), None)

    if parsed.print_config:
        shown = [found[0] if found else resolution.executable, *resolution.full_command[1:]]
        console.print(f"agent: {resolution.display_name} ({resolution.agent_id})", highlight=False)
        console.print(f"command: {' '.join(shown)}", highlight=False)
        return 0

    resolved = found[1] if found else None
    if resolved is None:
        err_console.print(
            f"{resolution.display_name} ('{resolution.executable}') was not found on PATH. Install it and try again.",
            highlight=False,
        )
        return 127

    cmd_to_run = [resolved, *resolution.full_command[1:]]
    try:
        proc = subprocess.run(cmd_to_run, check=False)
        return proc.returncode
    except KeyboardInterrupt:
        return 130


def agent_main(argv: Sequence[str] | None = None) -> int:
    """Console script entry point."""
    return main(argv)

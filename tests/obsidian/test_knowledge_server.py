"""Feature: one shared OpenKnowledge server for the vault (`vault knowledge-server`).

Each Claude session used to start its own `ok mcp` over stdio. A LaunchAgent now keeps one
server for the vault running, and Claude Code's user-scope entry points at its `/mcp`.
"""

from __future__ import annotations

import json
import pathlib
import plistlib
from collections.abc import Callable
from dataclasses import dataclass
from typing import Annotated

import pytest
from pydantic import Field
from typer.testing import CliRunner

from rig.obsidian import knowledge_server as ks
from rig.obsidian.cli import app
from rig.typed_json import decode

_load_plist: Callable[[bytes], object] = plistlib.loads

_LADDER = {"type": "stdio", "command": "/bin/sh", "args": ["-l", "-c", "# ok-mcp-v2\nexec npx -y ok mcp"]}


@dataclass(frozen=True, slots=True)
class _Agent:
    label: Annotated[str, Field(alias="Label")]
    program_arguments: Annotated[list[str], Field(alias="ProgramArguments")]
    working_directory: Annotated[str, Field(alias="WorkingDirectory")]
    run_at_load: Annotated[bool, Field(alias="RunAtLoad")]
    keep_alive: Annotated[bool, Field(alias="KeepAlive")]
    throttle: Annotated[int, Field(alias="ThrottleInterval")]
    nice: Annotated[int, Field(alias="Nice")]


def _claude_json(tmp_path: pathlib.Path, servers: dict[str, object]) -> pathlib.Path:
    (tmp_path / ".claude.json").write_text(json.dumps({"numStartups": 3, "mcpServers": servers}), encoding="utf-8")
    return tmp_path


def test_the_launch_agent_keeps_one_vault_server_running(tmp_path: pathlib.Path) -> None:
    """Scenario: the agent runs the pinned CLI with node, rooted at the vault, and never idles out."""
    node, script, root = tmp_path / "node", tmp_path / "cli.mjs", tmp_path / "vault"

    agent = decode(_Agent, _load_plist(ks.render(node, script, root)))

    assert agent.label == "dev.rtkelly.rig.open-knowledge"
    assert agent.program_arguments[:5] == [str(node), str(script), "--cwd", str(root), "start"]
    assert agent.program_arguments[5:7] == ["--port", str(ks.PORT)]
    assert agent.program_arguments[7:9] == ["--idle-shutdown", "off"], "KeepAlive would restart it every 30 minutes"
    assert "--only" not in agent.program_arguments, "server-only mode drops the editor UI used for review"
    assert agent.working_directory == str(root)
    assert agent.run_at_load and agent.keep_alive
    assert agent.throttle >= 30
    assert agent.nice == 10


def test_the_per_session_ladder_is_not_the_shared_server(tmp_path: pathlib.Path) -> None:
    """Scenario: the stdio entry `ok init` writes reads as stdio, so `status` reports it."""
    have = ks.current_registration(_claude_json(tmp_path, {ks.SERVER_NAME: _LADDER}))

    assert have == ks.Registration("stdio", "/bin/sh")
    assert not have.shared


def test_an_http_entry_at_the_shared_url_is_shared(tmp_path: pathlib.Path) -> None:
    entry = {"type": "http", "url": ks.mcp_url()}

    assert ks.current_registration(_claude_json(tmp_path, {ks.SERVER_NAME: entry})).shared


def test_an_http_entry_on_another_port_is_not_shared(tmp_path: pathlib.Path) -> None:
    entry = {"type": "http", "url": ks.mcp_url(ks.PORT + 1)}

    assert not ks.current_registration(_claude_json(tmp_path, {ks.SERVER_NAME: entry})).shared


@pytest.mark.parametrize("text", ["", "not json", "[]"])
def test_an_unreadable_claude_json_reads_as_absent(tmp_path: pathlib.Path, text: str) -> None:
    (tmp_path / ".claude.json").write_text(text, encoding="utf-8")

    assert ks.current_registration(tmp_path) == ks.Registration(None, None)


def test_connect_replaces_the_user_entry_through_claude_mcp() -> None:
    assert ks.connect_commands("claude") == [
        ["claude", "mcp", "remove", "--scope", "user", "open-knowledge"],
        ["claude", "mcp", "add", "--scope", "user", "--transport", "http", "open-knowledge", ks.mcp_url()],
    ]


def test_connect_without_apply_changes_nothing(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch, cli: CliRunner
) -> None:
    """Scenario: a dry run prints both commands and leaves `~/.claude.json` as it was."""
    user_home = _claude_json(tmp_path, {ks.SERVER_NAME: _LADDER})
    before = (user_home / ".claude.json").read_text(encoding="utf-8")
    monkeypatch.setattr(pathlib.Path, "home", lambda: user_home)
    def which(name: str) -> str:
        return f"/bin/{name}"

    monkeypatch.setattr("shutil.which", which)

    result = cli.invoke(app, ["knowledge-server", "connect"])

    assert result.exit_code == 0, result.output
    assert "mcp remove" in result.output and "mcp add" in result.output
    assert "dry run" in result.output
    assert (user_home / ".claude.json").read_text(encoding="utf-8") == before

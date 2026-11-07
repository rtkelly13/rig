"""Tests for the agent launcher CLI, WSL path mapping, and devenv setup steps."""

from __future__ import annotations

from pathlib import Path

import pytest

from rig.agent_launcher.cli import (
    AgentConfig,
    AgentDefaults,
    ClaudeCodeDefaults,
    canonical_agent_id,
    main,
    parse_args,
    resolve_agent_command,
    windows_to_wsl_path,
)
from rig.devenv.steps.ai_tools.agent_launcher_setup import (
    generate_ps1_content,
    generate_sh_content,
    get_estate_root,
    install_agent_launcher,
)
from rig.devenv.steps.ai_tools.agent_shortcut import (
    BEGIN_MARKER,
    END_MARKER,
    configure_agent_shortcut,
    generate_posix_block,
    generate_powershell_block,
    upsert_agent_shortcut,
)


@pytest.mark.ci
def test_parse_args_shortcuts():
    res_agy = parse_args(["-agy", "status"])
    assert res_agy.agent_override == "antigravity"
    assert res_agy.passthrough == ["status"]

    res_ccc = parse_args(["-ccc", "-r", "explain this"])
    assert res_ccc.agent_override == "claude-code"
    assert res_ccc.remote_control is True
    assert res_ccc.passthrough == ["explain this"]

    res_wsl = parse_args(["-agywsl", "--mode", "auto"])
    assert res_wsl.agent_override == "agywsl"
    assert res_wsl.passthrough == ["--mode", "auto"]

    res_oc = parse_args(["-oc", "run"])
    assert res_oc.agent_override == "opencode"
    assert res_oc.passthrough == ["run"]

    res_opencode = parse_args(["-opencode", "diff"])
    assert res_opencode.agent_override == "opencode"
    assert res_opencode.passthrough == ["diff"]

    res_codex = parse_args(["-codex", "gen"])
    assert res_codex.agent_override == "codex"
    assert res_codex.passthrough == ["gen"]


@pytest.mark.ci
def test_parse_args_use_and_flags():
    res = parse_args(["--use", "codex", "--flag", "value"])
    assert res.agent_override == "codex"
    assert res.passthrough == ["--flag", "value"]

    res_eq = parse_args(["--use=opencode", "arg1"])
    assert res_eq.agent_override == "opencode"
    assert res_eq.passthrough == ["arg1"]

    with pytest.raises(ValueError, match="--use requires an agent id"):
        parse_args(["--use"])

    res_cfg = parse_args(["--print-config", "-agy"])
    assert res_cfg.print_config is True
    assert res_cfg.agent_override == "antigravity"

    res_help = parse_args(["-h"])
    assert res_help.show_help is True


@pytest.mark.ci
def test_canonical_agent_id():
    assert canonical_agent_id("gemini") == "antigravity"
    assert canonical_agent_id("claude") == "claude-code"
    assert canonical_agent_id("copilot") == "github-copilot"
    assert canonical_agent_id("antigravity") == "antigravity"
    assert canonical_agent_id("unknown-agent") == "unknown-agent"


@pytest.mark.ci
def test_windows_to_wsl_path():
    assert windows_to_wsl_path(r"C:\code\personal\shared-utilities") == "/mnt/c/code/personal/shared-utilities"
    assert windows_to_wsl_path("C:/code/personal/shared-utilities") == "/mnt/c/code/personal/shared-utilities"
    assert windows_to_wsl_path("d:/repo/project") == "/mnt/d/repo/project"
    assert windows_to_wsl_path("/mnt/c/code") == "/mnt/c/code"
    assert windows_to_wsl_path("/home/user/project") == "/home/user/project"


@pytest.mark.ci
def test_resolve_agent_command_antigravity():
    parsed = parse_args(["-agy", "arg1", "--flag"])
    res = resolve_agent_command(parsed)
    assert res.agent_id == "antigravity"
    assert res.display_name == "Antigravity CLI"
    assert res.executable == "agy"
    assert res.full_command == ["agy", "arg1", "--flag"]


@pytest.mark.ci
def test_resolve_agent_command_claude_defaults():
    parsed = parse_args(["-ccc"])
    res = resolve_agent_command(parsed)
    assert res.agent_id == "claude-code"
    assert res.display_name == "Claude Code"
    assert res.executable == "claude"
    assert res.full_command == ["claude", "--model", "opus"]


@pytest.mark.ci
def test_resolve_agent_command_claude_remote_control():
    parsed = parse_args(["-ccc", "-r", "some prompt"])
    res = resolve_agent_command(parsed)
    assert res.full_command == ["claude", "--remote-control", "--model", "opus", "some prompt"]


@pytest.mark.ci
def test_resolve_agent_command_claude_custom_model_passthrough():
    parsed = parse_args(["-ccc", "--model", "sonnet", "prompt"])
    res = resolve_agent_command(parsed)
    assert res.full_command == ["claude", "--model", "sonnet", "prompt"]


@pytest.mark.ci
def test_resolve_agent_command_claude_config_defaults():
    cfg = AgentConfig(
        preferred="claude-code",
        defaults=AgentDefaults(claude_code=ClaudeCodeDefaults(model="haiku", remote_control=True)),
    )
    parsed = parse_args([])
    res = resolve_agent_command(parsed, config=cfg)
    assert res.full_command == ["claude", "--remote-control", "--model", "haiku"]


@pytest.mark.ci
def test_resolve_agent_command_agywsl():
    cwd = Path("C:/code/personal/shared-utilities")
    parsed = parse_args(["-agywsl", "explain", "code"])
    res = resolve_agent_command(parsed, cwd=cwd)
    assert res.agent_id == "agywsl"
    assert res.display_name == "Antigravity CLI (WSL Ubuntu)"
    assert res.executable == "wsl"
    expected = [
        "wsl",
        "-d",
        "Ubuntu",
        "--cd",
        "/mnt/c/code/personal/shared-utilities",
        "agy",
        "--mode",
        "auto",
        "explain",
        "code",
    ]
    assert res.full_command == expected


@pytest.mark.ci
def test_resolve_agent_command_agywsl_with_mode():
    cwd = Path("C:/code/personal/shared-utilities")
    parsed = parse_args(["-agywsl", "--mode", "manual", "prompt"])
    res = resolve_agent_command(parsed, cwd=cwd)
    expected = [
        "wsl",
        "-d",
        "Ubuntu",
        "--cd",
        "/mnt/c/code/personal/shared-utilities",
        "agy",
        "--mode",
        "manual",
        "prompt",
    ]
    assert res.full_command == expected


@pytest.mark.ci
def test_parse_args_danger():
    res1 = parse_args(["-dng", "prompt"])
    assert res1.danger is True
    assert res1.passthrough == ["prompt"]

    res2 = parse_args(["-agy", "--dng", "status"])
    assert res2.agent_override == "antigravity"
    assert res2.danger is True
    assert res2.passthrough == ["status"]

    res3 = parse_args(["--dangerously-skip-permissions", "-oc", "run"])
    assert res3.agent_override == "opencode"
    assert res3.danger is True
    assert res3.passthrough == ["run"]


@pytest.mark.ci
def test_resolve_agent_command_danger():
    # Test Antigravity on Windows
    res_agy = resolve_agent_command(parse_args(["-agy", "-dng", "status"]))
    assert res_agy.full_command == ["agy", "--dangerously-skip-permissions", "status"]

    # Test Claude Code
    res_ccc = resolve_agent_command(parse_args(["-ccc", "-dng", "prompt"]))
    assert res_ccc.full_command == ["claude", "--dangerously-skip-permissions", "--model", "opus", "prompt"]

    # Test Claude Code with remote control
    res_ccc_r = resolve_agent_command(parse_args(["-ccc", "-dng", "-r", "prompt"]))
    assert res_ccc_r.full_command == [
        "claude",
        "--remote-control",
        "--dangerously-skip-permissions",
        "--model",
        "opus",
        "prompt",
    ]

    # Test WSL Antigravity
    cwd = Path("C:/code/personal/shared-utilities")
    res_wsl = resolve_agent_command(parse_args(["-agywsl", "-dng", "prompt"]), cwd=cwd)
    assert res_wsl.full_command == [
        "wsl",
        "-d",
        "Ubuntu",
        "--cd",
        "/mnt/c/code/personal/shared-utilities",
        "agy",
        "--mode",
        "auto",
        "--dangerously-skip-permissions",
        "prompt",
    ]

    # Test OpenCode
    res_oc = resolve_agent_command(parse_args(["-oc", "-dng", "run"]))
    assert res_oc.full_command == ["opencode", "--auto", "run"]

    # Test OpenAI Codex
    res_codex = resolve_agent_command(parse_args(["-codex", "-dng", "prompt"]))
    assert res_codex.full_command == ["codex", "--dangerously-bypass-approvals-and-sandbox", "prompt"]

    # Test GitHub Copilot
    res_copilot = resolve_agent_command(parse_args(["--use", "github-copilot", "-dng", "prompt"]))
    assert res_copilot.full_command == ["copilot", "--allow-all", "prompt"]

    # Test GitHub Copilot shortcut
    res_cpl = resolve_agent_command(parse_args(["-cpl", "-dng", "prompt"]))
    assert res_cpl.full_command == ["copilot", "--allow-all", "prompt"]


@pytest.mark.ci
def test_resolve_agent_command_danger_deduplication():
    # Deduplicate Antigravity flag
    res_agy = resolve_agent_command(parse_args(["-agy", "-dng", "--dangerously-skip-permissions", "status"]))
    assert res_agy.full_command == ["agy", "--dangerously-skip-permissions", "status"]

    # Deduplicate OpenCode flag
    res_oc = resolve_agent_command(parse_args(["-oc", "-dng", "--auto", "run"]))
    assert res_oc.full_command == ["opencode", "--auto", "run"]

    # Deduplicate Codex with --yolo alias already provided
    res_codex = resolve_agent_command(parse_args(["-codex", "-dng", "--yolo", "prompt"]))
    assert res_codex.full_command == ["codex", "--yolo", "prompt"]

    # Deduplicate Copilot with --yolo alias already provided
    res_copilot = resolve_agent_command(parse_args(["--use", "github-copilot", "-dng", "--yolo", "prompt"]))
    assert res_copilot.full_command == ["copilot", "--yolo", "prompt"]


@pytest.mark.ci
def test_resolve_agent_command_danger_from_config():
    cfg = AgentConfig(preferred="antigravity", danger=True)
    res = resolve_agent_command(parse_args([]), config=cfg)
    assert res.full_command == ["agy", "--dangerously-skip-permissions"]


@pytest.mark.ci
def test_resolve_agent_command_unknown_agent():
    parsed = parse_args(["--use", "invalid-agent"])
    with pytest.raises(ValueError, match="Unknown agent id 'invalid-agent'"):
        resolve_agent_command(parsed)


@pytest.mark.ci
def test_main_print_config(capsys: pytest.CaptureFixture[str]):
    exit_code = main(["--print-config", "-agy", "-dng", "status"])
    assert exit_code == 0
    captured = capsys.readouterr()
    assert "agent: Antigravity CLI (antigravity)" in captured.out
    assert "command: agy --dangerously-skip-permissions status" in captured.out


@pytest.mark.ci
def test_main_help(capsys: pytest.CaptureFixture[str]):
    exit_code = main(["-h"])
    assert exit_code == 0
    captured = capsys.readouterr()
    assert "Usage: agent" in captured.out
    assert "-agy" in captured.out
    assert "-ccc" in captured.out
    assert "-agywsl" in captured.out
    assert "-dng" in captured.out


@pytest.mark.ci
def test_main_missing_executable(monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]):
    monkeypatch.setattr("shutil.which", lambda _cmd: None)
    exit_code = main(["-agy"])
    assert exit_code == 127
    captured = capsys.readouterr()
    assert "Antigravity CLI ('agy') was not found on PATH" in captured.err


@pytest.mark.ci
def test_main_invalid_args(capsys: pytest.CaptureFixture[str]):
    exit_code = main(["--use"])
    assert exit_code == 2
    captured = capsys.readouterr()
    assert "error: --use requires an agent id" in captured.err


@pytest.mark.ci
def test_shim_generation(tmp_path: Path):
    proj = tmp_path / "personal" / "shared-utilities"
    ps1 = generate_ps1_content(proj)
    assert f'uv run --no-sync --project "{proj}" python -m rig.agent_launcher @args' in ps1
    assert "exit $LASTEXITCODE" in ps1

    sh = generate_sh_content(proj)
    assert f'exec uv run --no-sync --project "{proj.as_posix()}" python -m rig.agent_launcher "$@"' in sh


@pytest.mark.ci
def test_estate_root_resolution(tmp_path: Path):
    proj = tmp_path / "personal" / "shared-utilities"
    proj.mkdir(parents=True)
    estate = get_estate_root(proj)
    assert estate == tmp_path


@pytest.mark.ci
def test_install_agent_launcher(tmp_path: Path):
    estate = tmp_path / "estate"
    proj = estate / "personal" / "shared-utilities"
    proj.mkdir(parents=True)

    shim = install_agent_launcher(project_root=proj, estate_root=estate)
    assert shim.exists()
    assert (estate / "agent.ps1").exists() or (estate / "agent.sh").exists()


@pytest.mark.ci
def test_upsert_agent_shortcut_new():
    existing = "oh-my-posh init pwsh | Invoke-Expression\n"
    shim = Path("C:/code/agent.ps1")
    block = generate_powershell_block(shim)
    updated = upsert_agent_shortcut(existing, block)

    assert BEGIN_MARKER in updated
    assert END_MARKER in updated
    assert "& 'C:\\code\\agent.ps1' @args" in updated or "& 'C:/code/agent.ps1' @args" in updated


@pytest.mark.ci
def test_configure_agent_shortcut_file(tmp_path: Path):
    profile = tmp_path / "profile.ps1"
    shim = Path("C:/code/agent.ps1")

    assert configure_agent_shortcut(shim, profile_path=profile) is True
    content = profile.read_text(encoding="utf-8")
    assert BEGIN_MARKER in content
    assert END_MARKER in content

    # Running again is idempotent
    assert configure_agent_shortcut(shim, profile_path=profile) is True
    assert profile.read_text(encoding="utf-8") == content


@pytest.mark.ci
def test_generate_posix_block():
    shim = Path("/home/user/code/agent.sh")
    block = generate_posix_block(shim)
    assert "agent() {" in block
    assert '"/home/user/code/agent.sh" "$@"' in block

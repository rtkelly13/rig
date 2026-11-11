"""Tests for the devenv step that installs the agent launcher shim and shell shortcut."""

from __future__ import annotations

from pathlib import Path

import pytest

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
def test_shim_generation(tmp_path: Path):
    proj = tmp_path / "personal" / "shared-utilities"
    ps1 = generate_ps1_content(proj)
    assert f'dotnet run --project "{proj / "src" / "Rig.Cli"}" -- agent @args' in ps1
    assert "exit $LASTEXITCODE" in ps1

    sh = generate_sh_content(proj)
    assert f'exec dotnet run --project "{(proj / "src" / "Rig.Cli").as_posix()}" -- agent "$@"' in sh


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

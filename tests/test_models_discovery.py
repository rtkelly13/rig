"""Unit tests for deterministic AI model discovery in rig."""

from __future__ import annotations

import json
from pathlib import Path
from unittest.mock import MagicMock

import pytest
from typer.testing import CliRunner

from rig.models.cli import app
from rig.models.discovery import (
    discover_all_models,
    discover_antigravity_models,
    discover_claude_models,
    discover_codex_models,
    discover_copilot_models,
    discover_opencode_models,
)

runner = CliRunner()


@pytest.mark.ci
def test_discover_claude_models():
    models = discover_claude_models()
    assert len(models) == 1
    assert models[0].model_id == "opus"
    assert models[0].agent == "claude-code"
    assert models[0].provider == "anthropic"


@pytest.mark.ci
def test_discover_codex_models_empty_when_missing(tmp_path: Path):
    models = discover_codex_models(tmp_path)
    assert models == ()


@pytest.mark.ci
def test_discover_codex_models_from_cache(tmp_path: Path):
    cache = tmp_path / "models_cache.json"
    cache.write_text(
        json.dumps(
            {
                "models": [
                    {
                        "slug": "gpt-6.1-sol",
                        "display_name": "GPT-6.1-Sol",
                        "description": "Latest workhorse model",
                        "supported_reasoning_levels": [
                            {"effort": "low"},
                            {"effort": "medium"},
                            {"effort": "high"},
                        ],
                    }
                ]
            }
        ),
        encoding="utf-8",
    )

    models = discover_codex_models(tmp_path)
    assert len(models) == 1
    assert models[0].model_id == "gpt-6.1-sol"
    assert models[0].display_name == "GPT-6.1-Sol"
    assert models[0].provider == "openai"
    assert models[0].reasoning_levels == ("low", "medium", "high")


@pytest.mark.ci
def test_discover_opencode_models_from_cli(monkeypatch: pytest.MonkeyPatch):
    fake_stdout = """
opencode/claude-opus-5-5
opencode/deepseek-v4.1-flash
github-copilot/gpt-6.1-sol
"""

    def fake_run(*args: object, **kwargs: object):
        mock = MagicMock()
        mock.returncode = 0
        mock.stdout = fake_stdout
        return mock

    monkeypatch.setattr("subprocess.run", fake_run)
    monkeypatch.setattr("shutil.which", lambda cmd: "/fake/opencode")

    models = discover_opencode_models(allow_cli=True)
    assert len(models) == 3
    ids = {m.model_id for m in models}
    assert "opencode/claude-opus-5-5" in ids
    assert "opencode/deepseek-v4.1-flash" in ids
    assert "github-copilot/gpt-6.1-sol" in ids


@pytest.mark.ci
def test_discover_opencode_models_from_cache(tmp_path: Path):
    cache = tmp_path / "models.json"
    cache.write_text(
        json.dumps(
            {
                "deepseek": {
                    "models": {
                        "deepseek-flash": {
                            "name": "DeepSeek V4.1 Flash",
                            "description": "Fast multimodal model",
                        }
                    }
                }
            }
        ),
        encoding="utf-8",
    )

    models = discover_opencode_models(cache_path=cache, allow_cli=False)
    assert len(models) == 1
    assert models[0].model_id == "deepseek/deepseek-flash"
    assert models[0].display_name == "DeepSeek V4.1 Flash"
    assert models[0].provider == "deepseek"


@pytest.mark.ci
def test_discover_antigravity_models_no_fallback():
    models = discover_antigravity_models(allow_cli=False)
    assert models == ()


@pytest.mark.ci
def test_discover_copilot_models_no_fallback(tmp_path: Path):
    nonexistent = tmp_path / "nonexistent.json"
    models = discover_copilot_models(cache_path=nonexistent)
    assert models == ()


@pytest.mark.ci
def test_discover_all_models_evaluating_defaults():
    models = discover_all_models()
    assert len(models) == 4
    agents = {m.agent for m in models}
    assert agents == {"claude-code", "codex", "antigravity", "opencode"}
    ids = {m.model_id for m in models}
    assert "opus" in ids
    assert "gpt-6.1-sol" in ids
    assert "gemini-3.8-flash-medium" in ids
    assert "deepseek/deepseek-flash" in ids


@pytest.mark.ci
def test_discover_all_models_agent_filtering():
    claude_only = discover_all_models(agent="claude")
    assert len(claude_only) == 1
    assert claude_only[0].agent == "claude-code"
    assert claude_only[0].model_id == "opus"

    codex_only = discover_all_models(agent="codex")
    assert len(codex_only) == 1
    assert codex_only[0].agent == "codex"
    assert codex_only[0].model_id == "gpt-6.1-sol"

    copilot_only = discover_all_models(agent="copilot")
    assert len(copilot_only) == 1
    assert copilot_only[0].agent == "github-copilot"
    assert copilot_only[0].model_id == "gpt-6.1-sol"


@pytest.mark.ci
def test_cli_models_json_output():
    res = runner.invoke(app, ["--agent", "claude", "--json"])
    assert res.exit_code == 0
    parsed = json.loads(res.output)
    assert isinstance(parsed, list)
    assert any(m["model_id"] == "opus" for m in parsed)


@pytest.mark.ci
def test_cli_models_provider_filter():
    res = runner.invoke(app, ["--provider", "anthropic", "--json"])
    assert res.exit_code == 0
    parsed = json.loads(res.output)
    assert len(parsed) > 0
    assert all(m["provider"] == "anthropic" for m in parsed)

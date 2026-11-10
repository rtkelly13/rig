"""Deterministic AI model discovery across agent CLIs and local runtime caches.

Interrogates provider CLIs (`opencode models`, `agy models`, `codex`) and their deterministic
runtime model caches (`~/.cache/opencode/models.json`, `~/.codex/models_cache.json`) to enumerate
available models, aliases, and reasoning capabilities.
"""

from __future__ import annotations

import re
import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path

from pydantic import Field

from rig.typed_json import Lenient, try_decode_json


@dataclass(slots=True, frozen=True)
class ModelEffortLevel:
    """A reasoning effort tier for a model (e.g. low, medium, high)."""

    effort: str
    description: Lenient[str] = None


@dataclass(slots=True, frozen=True)
class CodexCachedModel:
    """A model entry from Codex's models_cache.json."""

    slug: str
    display_name: str
    description: Lenient[str] = None
    default_reasoning_level: Lenient[str] = None
    supported_reasoning_levels: tuple[ModelEffortLevel, ...] = ()


@dataclass(slots=True, frozen=True)
class CodexCacheFile:
    """Structure of ~/.codex/models_cache.json."""

    models: tuple[CodexCachedModel, ...] = ()


@dataclass(slots=True, frozen=True)
class OpencodeModelInfo:
    """One model entry inside an OpenCode provider catalog."""

    name: Lenient[str] = None
    description: Lenient[str] = None


@dataclass(slots=True, frozen=True)
class OpencodeProviderCatalog:
    """A provider entry containing a mapping of model ID to model metadata."""

    models: dict[str, OpencodeModelInfo] = Field(default_factory=dict)


@dataclass(slots=True, frozen=True)
class DiscoveredModel:
    """A deterministic representation of an AI model available in rig."""

    agent: str
    model_id: str
    display_name: str
    provider: str | None = None
    reasoning_levels: tuple[str, ...] = ()
    description: str | None = None


# Models actively evaluated across developer environments
EVALUATED_MODELS: tuple[DiscoveredModel, ...] = (
    DiscoveredModel(
        agent="claude-code",
        model_id="opus",
        display_name="Claude Opus 5.5 (Medium)",
        provider="anthropic",
        reasoning_levels=("medium",),
        description="Anthropic Claude Opus 5.5 evaluated at medium reasoning effort.",
    ),
    DiscoveredModel(
        agent="codex",
        model_id="gpt-6.1-sol",
        display_name="GPT-6.1 Sol (Medium)",
        provider="openai",
        reasoning_levels=("medium",),
        description="OpenAI GPT-6.1 Sol evaluated at medium reasoning effort.",
    ),
    DiscoveredModel(
        agent="antigravity",
        model_id="gemini-3.8-flash-medium",
        display_name="Gemini 3.8 Flash (Medium)",
        provider="google",
        reasoning_levels=("medium",),
        description="Google Gemini 3.8 Flash evaluated at medium reasoning effort.",
    ),
    DiscoveredModel(
        agent="opencode",
        model_id="deepseek/deepseek-flash",
        display_name="DeepSeek Flash 4.1",
        provider="deepseek",
        reasoning_levels=("medium",),
        description="DeepSeek Flash 4.1 evaluated model for high-throughput reasoning.",
    ),
)

COPILOT_EVALUATED_MODELS: tuple[DiscoveredModel, ...] = (
    DiscoveredModel(
        agent="github-copilot",
        model_id="gpt-6.1-sol",
        display_name="GPT-6.1 Sol (Medium)",
        provider="openai",
        reasoning_levels=("medium",),
        description="OpenAI Sol 6.1 served via GitHub Copilot.",
    ),
)


def discover_codex_models(codex_dir: Path | None = None) -> tuple[DiscoveredModel, ...]:
    """Enumerate models from Codex's deterministic cache (~/.codex/models_cache.json)."""
    base = codex_dir or Path.home() / ".codex"
    cache_path = base / "models_cache.json"
    if not cache_path.is_file():
        return ()

    try:
        data = try_decode_json(CodexCacheFile, cache_path.read_bytes())
        if data is None or not data.models:
            return ()

        results: list[DiscoveredModel] = []
        for m in data.models:
            efforts = tuple(lvl.effort for lvl in m.supported_reasoning_levels)
            results.append(
                DiscoveredModel(
                    agent="codex",
                    model_id=m.slug,
                    display_name=m.display_name,
                    provider="openai",
                    reasoning_levels=efforts,
                    description=m.description,
                )
            )
        return tuple(results)
    except OSError:
        return ()


def discover_opencode_models(
    cache_path: Path | None = None,
    allow_cli: bool = True,
) -> tuple[DiscoveredModel, ...]:
    """Enumerate models supported by OpenCode.

    First tries executing `opencode models`. If that times out or fails (e.g. background
    service stopped or sandboxed), falls back to parsing `~/.cache/opencode/models.json`.
    """
    executable = shutil.which("opencode") or shutil.which("opencode2")
    if allow_cli and executable:
        try:
            proc = subprocess.run(
                [executable, "models"],
                capture_output=True,
                text=True,
                timeout=3,
                check=False,
            )
            if proc.returncode == 0 and proc.stdout.strip():
                models_from_cli: list[DiscoveredModel] = []
                for line in proc.stdout.splitlines():
                    cleaned = line.strip()
                    if not cleaned or cleaned.startswith("Fetching") or cleaned.startswith("Error"):
                        continue
                    if "/" in cleaned:
                        prov, mid = cleaned.split("/", 1)
                        models_from_cli.append(
                            DiscoveredModel(
                                agent="opencode",
                                model_id=cleaned,
                                display_name=mid,
                                provider=prov,
                            )
                        )
                    else:
                        models_from_cli.append(
                            DiscoveredModel(
                                agent="opencode",
                                model_id=cleaned,
                                display_name=cleaned,
                                provider="opencode",
                            )
                        )
                if models_from_cli:
                    return tuple(models_from_cli)
        except (subprocess.SubprocessError, OSError):
            pass

    # Deterministic fallback: read OpenCode local model cache
    target_cache = cache_path or Path.home() / ".cache" / "opencode" / "models.json"
    if not target_cache.is_file():
        return ()

    try:
        data = try_decode_json(dict[str, OpencodeProviderCatalog], target_cache.read_bytes())
        if not data:
            return ()

        results: list[DiscoveredModel] = []
        # Curated top providers relevant to rig workflows
        priority_providers = frozenset(
            {
                "opencode",
                "github-copilot",
                "anthropic",
                "openai",
                "google",
                "deepseek",
            }
        )

        for prov_id, prov_catalog in data.items():
            if prov_id not in priority_providers or not prov_catalog.models:
                continue
            for model_id, info in prov_catalog.models.items():
                name = info.name or model_id
                results.append(
                    DiscoveredModel(
                        agent="opencode",
                        model_id=f"{prov_id}/{model_id}",
                        display_name=name,
                        provider=prov_id,
                        description=info.description,
                    )
                )

        return tuple(results)
    except OSError:
        return ()


def discover_antigravity_models(allow_cli: bool = True) -> tuple[DiscoveredModel, ...]:
    """Enumerate models supported by Google Antigravity (agy)."""
    if allow_cli and shutil.which("agy"):
        try:
            proc = subprocess.run(
                ["agy", "models"],
                capture_output=True,
                text=True,
                timeout=4,
                check=False,
            )
            if proc.returncode == 0 and proc.stdout.strip():
                models: list[DiscoveredModel] = []
                for line in proc.stdout.splitlines():
                    cleaned = line.strip()
                    if not cleaned or "Fetching" in cleaned:
                        continue
                    parts = re.split(r"\s{2,}", cleaned, maxsplit=1)
                    if len(parts) == 2:
                        mid, name = parts[0].strip(), parts[1].strip()
                        prov = "google"
                        if "claude" in mid:
                            prov = "anthropic"
                        elif "gpt" in mid:
                            prov = "openai"
                        models.append(
                            DiscoveredModel(
                                agent="antigravity",
                                model_id=mid,
                                display_name=name,
                                provider=prov,
                            )
                        )
                if models:
                    return tuple(models)
        except (subprocess.SubprocessError, OSError):
            pass

    return ()


def discover_copilot_models(cache_path: Path | None = None) -> tuple[DiscoveredModel, ...]:
    """Enumerate models supported by GitHub Copilot CLI."""
    target_cache = cache_path or Path.home() / ".cache" / "opencode" / "models.json"
    if target_cache.is_file():
        try:
            data = try_decode_json(dict[str, OpencodeProviderCatalog], target_cache.read_bytes())
            if data:
                for prov_id, prov_catalog in data.items():
                    if prov_id == "github-copilot" and prov_catalog.models:
                        results: list[DiscoveredModel] = []
                        for model_id, info in prov_catalog.models.items():
                            results.append(
                                DiscoveredModel(
                                    agent="github-copilot",
                                    model_id=model_id,
                                    display_name=info.name or model_id,
                                    provider="github-copilot",
                                    description=info.description,
                                )
                            )
                        if results:
                            return tuple(results)
        except OSError:
            pass

    return ()


def discover_claude_models() -> tuple[DiscoveredModel, ...]:
    """Enumerate Claude Code evaluated models."""
    return tuple(m for m in EVALUATED_MODELS if m.agent == "claude-code")


def discover_all_models(
    agent: str | None = None,
    all_models: bool = False,
) -> tuple[DiscoveredModel, ...]:
    """Enumerate models across coding assistants in rig.

    By default (`all_models=False`), restricts output strictly to the models actively
    being evaluated across environments:
    - Codex: Sol 6.1 medium
    - Claude Code: Opus 5.5 medium
    - Antigravity: Gemini 3.8 medium
    - OpenCode: DeepSeek Flash 4.1
    (and Copilot when filtering by Copilot).

    To interrogate live provider CLIs and local caches for all available models
    without synthetic fallbacks, pass `all_models=True`.
    """
    normalized = (agent or "").lower().strip()

    if not all_models:
        if not normalized:
            return EVALUATED_MODELS
        if normalized in ("claude", "claude-code", "ccc"):
            return tuple(m for m in EVALUATED_MODELS if m.agent == "claude-code")
        if normalized in ("codex", "openai"):
            return tuple(m for m in EVALUATED_MODELS if m.agent == "codex")
        if normalized in ("antigravity", "agy", "gemini"):
            return tuple(m for m in EVALUATED_MODELS if m.agent == "antigravity")
        if normalized in ("opencode", "oc"):
            return tuple(m for m in EVALUATED_MODELS if m.agent == "opencode")
        if normalized in ("copilot", "github-copilot", "cpl"):
            return COPILOT_EVALUATED_MODELS
        return ()

    if normalized in ("claude", "claude-code", "ccc"):
        return discover_claude_models()
    if normalized in ("codex", "openai"):
        return discover_codex_models()
    if normalized in ("opencode", "oc"):
        return discover_opencode_models()
    if normalized in ("antigravity", "agy", "gemini"):
        return discover_antigravity_models()
    if normalized in ("copilot", "github-copilot", "cpl"):
        return discover_copilot_models()

    all_found: list[DiscoveredModel] = []
    all_found.extend(discover_claude_models())
    all_found.extend(discover_codex_models())
    all_found.extend(discover_antigravity_models())
    all_found.extend(discover_copilot_models())
    all_found.extend(discover_opencode_models())

    return tuple(all_found)

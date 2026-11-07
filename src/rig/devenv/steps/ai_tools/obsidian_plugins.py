"""Install and enable Obsidian community plugins from their GitHub releases.

Obsidian has no CLI, but a community plugin is only three files in
``<vault>/.obsidian/plugins/<id>/`` plus an entry in ``community-plugins.json``.
Releases publish exactly those files, so installation is fully automatable — the
part that is not is any plugin needing an in-app API key.

Plugin ids resolve to repos through the official registry, so a caller names the
plugin the way the user sees it rather than hard-coding a repo.
"""

from __future__ import annotations

import json
import urllib.parse
from dataclasses import dataclass
from http.client import HTTPException
from pathlib import Path

from rich.console import Console

from rig.devenv.downloads import fetch_bytes
from rig.typed_json import decode, load_json, try_decode

console = Console()

REGISTRY_URL = "https://raw.githubusercontent.com/obsidianmd/obsidian-releases/master/community-plugins.json"
RELEASE_API = "https://api.github.com/repos/{repo}/releases/latest"
ASSETS = ("manifest.json", "main.js", "styles.css")  # styles.css is optional
REQUIRED = ("manifest.json", "main.js")

# Plugins this estate wants in an agent-facing vault.
#
# Deliberately excluded: anything that rewrites files on save. The vault fences
# generated content between okf:begin/okf:end markers and has several writers
# (Obsidian, open-knowledge, plannotator, agents), so a formatter or a schema
# enforcer would fight the generator. That rules out Linter, Metadata Menu and
# obsidian-git's auto-commit. Dataview is excluded too — the `bases` core plugin
# already queries frontmatter natively, including nested keys like `generated.by`.
DEFAULT_PLUGINS: tuple[str, ...] = (
    "obsidian-local-rest-api",  # serves the vault over MCP
    "templater-obsidian",  # stamps provenance at note creation (ADR 0007)
    "table-editor-obsidian",  # Advanced Tables — ADRs and hubs are table-dense
    "obsidian-tasks-plugin",  # queries plain `- [ ]` markdown, invents no format
)

# Core plugins that ship with Obsidian but are off by default.
DEFAULT_CORE_PLUGINS: tuple[str, ...] = (
    "properties",  # the frontmatter editor — the whole OKF layer is frontmatter
)


@dataclass(slots=True, frozen=True)
class PluginSpec:
    """A resolved community plugin, as the registry lists it."""

    id: str
    repo: str
    name: str


@dataclass(slots=True, frozen=True)
class _PluginManifest:
    version: str


@dataclass(slots=True, frozen=True)
class _ReleaseAsset:
    name: str
    browser_download_url: str


@dataclass(slots=True, frozen=True)
class _Release:
    tag_name: str
    assets: tuple[_ReleaseAsset, ...]


#: What fetching and reading a remote file can raise: network, HTTP framing, refused URLs and bad JSON.
_FETCH_ERRORS = (OSError, HTTPException, ValueError)


# Asset URLs arrive from the community registry and the GitHub releases API — remote
# data. `urlopen` honours `file://` and other schemes, so a hostile or corrupted entry
# could otherwise make this read a local path. Restrict to HTTPS, and to the two hosts
# that legitimately serve plugin assets.
_ALLOWED_HOSTS = frozenset(
    {"raw.githubusercontent.com", "api.github.com", "github.com", "objects.githubusercontent.com"}
)


def _check_url(url: str) -> str:
    """Return the URL if it is an HTTPS fetch from a known host; raise otherwise."""
    parts = urllib.parse.urlsplit(url)
    if parts.scheme != "https":
        raise ValueError(f"refusing non-HTTPS plugin URL: {url!r}")
    if parts.hostname not in _ALLOWED_HOSTS:
        raise ValueError(f"refusing plugin URL from unexpected host {parts.hostname!r}: {url!r}")
    return url


def _get(url: str, timeout: int = 30) -> bytes:
    return fetch_bytes(_check_url(url), user_agent="devenv-obsidian-plugins", timeout=timeout)


def parse_registry(data: object) -> list[PluginSpec]:
    """The registry's well-formed entries; one bad entry must not hide every other plugin."""
    entries = decode(list[object], data)
    return [spec for entry in entries if (spec := try_decode(PluginSpec, entry)) is not None]


def fetch_registry() -> list[PluginSpec]:
    """The official community plugin registry."""
    return parse_registry(load_json(_get(REGISTRY_URL).decode("utf-8")))


def resolve_plugin(plugin_id: str, registry: list[PluginSpec]) -> PluginSpec | None:
    """Map a plugin id to its repo via the registry."""
    return next((spec for spec in registry if spec.id == plugin_id), None)


def installed_version(vault: Path, plugin_id: str) -> str | None:
    """Version currently installed, or None."""
    manifest = vault / ".obsidian" / "plugins" / plugin_id / "manifest.json"
    if not manifest.is_file():
        return None
    try:
        parsed = try_decode(_PluginManifest, load_json(manifest.read_text(encoding="utf-8")))
    except (OSError, json.JSONDecodeError):
        return None
    return parsed.version if parsed else None


def enable_plugin(vault: Path, plugin_id: str, dry_run: bool = False) -> bool:
    """Add the plugin to community-plugins.json, preserving anything already enabled."""
    path = vault / ".obsidian" / "community-plugins.json"
    enabled: list[str] = []
    if path.is_file():
        try:
            raw = load_json(path.read_text(encoding="utf-8"))
            enabled = [str(x) for x in try_decode(list[object], raw) or []]
        except (OSError, json.JSONDecodeError):
            enabled = []
    if plugin_id in enabled:
        return False
    enabled.append(plugin_id)
    if not dry_run:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(enabled, indent=2) + "\n", encoding="utf-8")
    return True


def enable_core_plugins(
    vault: Path,
    plugin_ids: tuple[str, ...] = DEFAULT_CORE_PLUGINS,
    dry_run: bool = False,
) -> list[str]:
    """Turn on built-in plugins that ship disabled.

    `core-plugins.json` is a mapping of id -> bool in current Obsidian. Older vaults
    used a list of enabled ids; both shapes are handled, and the existing shape is
    preserved so Obsidian does not rewrite it on next launch.
    """
    path = vault / ".obsidian" / "core-plugins.json"
    if not path.is_file():
        return []
    try:
        raw = load_json(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return []

    turned_on: list[str] = []
    if (mapping := try_decode(dict[str, object], raw)) is not None:
        for pid in plugin_ids:
            if mapping.get(pid) is not True:
                mapping[pid] = True
                turned_on.append(pid)
        payload: object = mapping
    elif (listed := try_decode(list[object], raw)) is not None:
        enabled = [str(x) for x in listed]
        for pid in plugin_ids:
            if pid not in enabled:
                enabled.append(pid)
                turned_on.append(pid)
        payload = enabled
    else:
        return []

    if turned_on and not dry_run:
        path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    return turned_on


def install_plugin(vault: Path, spec: PluginSpec, dry_run: bool = False) -> str | None:
    """Download the latest release into the vault. Returns the version installed."""
    try:
        release = decode(_Release, load_json(_get(RELEASE_API.format(repo=spec.repo)).decode("utf-8")))
    except _FETCH_ERRORS as e:
        console.print(f"[red]{spec.id}: could not read latest release ({e})[/red]")
        return None

    tag = release.tag_name
    by_name = {asset.name: asset.browser_download_url for asset in release.assets}
    missing = [a for a in REQUIRED if a not in by_name]
    if missing:
        console.print(f"[red]{spec.id}: release {tag} is missing {', '.join(missing)}[/red]")
        return None

    target = vault / ".obsidian" / "plugins" / spec.id
    if dry_run:
        return tag

    target.mkdir(parents=True, exist_ok=True)
    for asset in ASSETS:
        url = by_name.get(asset)
        if not url:
            continue  # styles.css is genuinely optional
        try:
            (target / asset).write_bytes(_get(url))
        except _FETCH_ERRORS as e:
            console.print(f"[red]{spec.id}: failed downloading {asset} ({e})[/red]")
            return None
    return tag


def setup_obsidian_plugins(
    vault: Path,
    plugin_ids: tuple[str, ...] = DEFAULT_PLUGINS,
    dry_run: bool = False,
) -> None:
    """Install and enable the named plugins in a vault."""
    if not (vault / ".obsidian").is_dir():
        console.print(f"[dim]Not an Obsidian vault, skipping: {vault}[/dim]")
        return

    try:
        registry = fetch_registry()
    except _FETCH_ERRORS as e:
        console.print(f"[red]Could not fetch the Obsidian plugin registry: {e}[/red]")
        return

    for plugin_id in plugin_ids:
        spec = resolve_plugin(plugin_id, registry)
        if spec is None:
            console.print(f"[red]{plugin_id}: not in the community plugin registry[/red]")
            continue

        have = installed_version(vault, plugin_id)
        version = install_plugin(vault, spec, dry_run=dry_run)
        if version is None:
            continue

        if have and have in version:
            console.print(f"[green]✓ {spec.name} {have} already installed[/green]")
        else:
            verb = "would install" if dry_run else "installed"
            console.print(f"[cyan]{verb} {spec.name} {version}[/cyan]")

        if enable_plugin(vault, plugin_id, dry_run=dry_run):
            console.print(f"  [dim green]+ enabled {plugin_id}[/dim green]")

    for pid in enable_core_plugins(vault, dry_run=dry_run):
        console.print(f"[cyan]enabled core plugin: {pid}[/cyan]")

    console.print(
        "[dim]Restart Obsidian to load newly installed plugins. Plugins needing an "
        "in-app API key must be configured in Obsidian's settings.[/dim]"
    )

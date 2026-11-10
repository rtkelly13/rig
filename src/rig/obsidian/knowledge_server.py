"""One OpenKnowledge server for the vault, shared by every Claude session.

Registered as a stdio server, OpenKnowledge starts `npx … ok mcp` in every session: about
120 MB each, measured. One resident `ok start` on a fixed port serves the same tools at
``/mcp``, and the vault's skill library with them, to any number of sessions, and its
editor UI at ``/`` for reviewing Markdown in the browser.

The server is rooted at the vault, so it answers only for the vault: a call naming another
folder is refused. That is all rig asks of it, since every skill lookup names the vault
(``skills({ cwd: <vault> })``). See `docs/vault/knowledge-server.md`.

macOS only: it runs as a LaunchAgent (`rig.launchd`), like the OTel collector.
"""

from __future__ import annotations

import pathlib
import plistlib
import shutil
from dataclasses import dataclass

import httpx

from rig import launchd, proc
from rig.roots import app_data_dir
from rig.typed_json import decode_json, is_object, load_json

LABEL = "dev.rtkelly.rig.open-knowledge"
PACKAGE = "@inkeep/open-knowledge"
#: Pinned so the resident server changes only through a PR; `npx …@latest` moved on every session.
VERSION = "0.89.4"
PORT = 47311
#: The name Claude Code knows the server by; the same one `ok init` registers.
SERVER_NAME = "open-knowledge"
#: Seconds launchd waits before restarting a server that exited.
THROTTLE_SECONDS = 60


def mcp_url(port: int = PORT) -> str:
    return f"http://127.0.0.1:{port}/mcp"


def editor_url(port: int = PORT) -> str:
    return f"http://127.0.0.1:{port}/"


def project_root() -> pathlib.Path:
    """The vault repository, which holds `.ok/` and the skill library; the notes are under it in `Vault/`."""
    return pathlib.Path.home() / "code" / "personal" / "obsidian-vault"


def home() -> pathlib.Path:
    return app_data_dir() / "open-knowledge"


def entry_point(prefix: pathlib.Path | None = None) -> pathlib.Path:
    """The package's CLI module, run with node directly: launchd has no shell PATH for the `ok` shebang."""
    return (prefix or home()) / "node_modules" / PACKAGE / "dist" / "cli.mjs"


def log_path() -> pathlib.Path:
    return app_data_dir() / "logs" / "open-knowledge.log"


def plist_path() -> pathlib.Path:
    return launchd.agents_dir() / f"{LABEL}.plist"


def installed_version(prefix: pathlib.Path | None = None) -> str | None:
    manifest = (prefix or home()) / "node_modules" / PACKAGE / "package.json"
    try:
        data = load_json(manifest.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    version = data.get("version") if is_object(data) else None
    return version if isinstance(version, str) else None


def install(prefix: pathlib.Path | None = None) -> pathlib.Path:
    """Install the pinned package under rig's data folder, unless that version is already there."""
    target = prefix or home()
    if installed_version(target) != VERSION:
        npm = shutil.which("npm")
        if npm is None:
            raise FileNotFoundError("npm is not on PATH; install Node.js 24 or later")
        target.mkdir(parents=True, exist_ok=True)
        argv = [npm, "install", "--prefix", str(target), "--no-fund", "--no-audit", f"{PACKAGE}@{VERSION}"]
        _ = proc.run(argv, timeout=600, check=True, scope="rig.obsidian.knowledge_server")
    return entry_point(target)


def program_arguments(node: pathlib.Path, script: pathlib.Path, root: pathlib.Path, port: int = PORT) -> list[str]:
    """The server's command line. ``--idle-shutdown off`` matters: with `KeepAlive` the default 30 minutes
    would make launchd restart it every half hour."""
    return [
        str(node),
        str(script),
        "--cwd",
        str(root),
        "start",
        "--port",
        str(port),
        "--idle-shutdown",
        "off",
        "--no-open-browser",
    ]


def render(node: pathlib.Path, script: pathlib.Path, root: pathlib.Path, port: int = PORT) -> bytes:
    log = str(log_path())
    agent = {
        "Label": LABEL,
        "ProgramArguments": program_arguments(node, script, root, port),
        "WorkingDirectory": str(root),
        "RunAtLoad": True,
        "KeepAlive": True,
        "ThrottleInterval": THROTTLE_SECONDS,
        "ProcessType": "Background",
        "LowPriorityIO": True,
        "Nice": 10,
        "StandardOutPath": log,
        "StandardErrorPath": log,
    }
    return plistlib.dumps(agent)


def node_path() -> pathlib.Path:
    found = shutil.which("node")
    if found is None:
        raise FileNotFoundError("node is not on PATH; install Node.js 24 or later")
    return pathlib.Path(found)


def install_service(script: pathlib.Path, root: pathlib.Path | None = None) -> pathlib.Path:
    path = plist_path()
    log_path().parent.mkdir(parents=True, exist_ok=True)
    launchd.load(path, render(node_path(), script, root or project_root()))
    return path


def uninstall_service() -> bool:
    return launchd.remove(plist_path())


def service_installed() -> bool:
    return plist_path().is_file()


def service_loaded() -> bool:
    return launchd.loaded(LABEL)


def healthy(port: int = PORT, *, timeout: float = 2.0) -> bool:
    """Whether the server answers its health probe on *port*."""
    try:
        return httpx.get(f"http://127.0.0.1:{port}/healthz", timeout=timeout).status_code == 200
    except httpx.HTTPError:
        return False


# --------------------------------------------------------------------------- the Claude Code entry


@dataclass(frozen=True, slots=True)
class Registration:
    """How `~/.claude.json` registers the server at user scope: absent, or its transport and target."""

    transport: str | None
    target: str | None

    @property
    def shared(self) -> bool:
        return self.transport == "http" and self.target == mcp_url()


def claude_json_path(user_home: pathlib.Path | None = None) -> pathlib.Path:
    return (user_home or pathlib.Path.home()) / ".claude.json"


def registration(text: str) -> Registration:
    """Read the user-scope entry from the text of `~/.claude.json`; never writes it."""
    data = decode_json(dict[str, object], text)
    servers = data.get("mcpServers")
    entry = servers.get(SERVER_NAME) if is_object(servers) else None
    if not is_object(entry):
        return Registration(None, None)
    transport = entry.get("type", "stdio")
    url = entry.get("url")
    command = entry.get("command")
    target = url if isinstance(url, str) else command if isinstance(command, str) else None
    return Registration(transport if isinstance(transport, str) else None, target)


def current_registration(user_home: pathlib.Path | None = None) -> Registration:
    try:
        return registration(claude_json_path(user_home).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return Registration(None, None)


def connect_commands(claude: str = "claude") -> list[list[str]]:
    """Replace the user-scope entry with the shared server, through `claude mcp`, which owns that file.

    `~/.claude.json` is rewritten by every running session, so rig never edits it itself.
    """
    return [
        [claude, "mcp", "remove", "--scope", "user", SERVER_NAME],
        [claude, "mcp", "add", "--scope", "user", "--transport", "http", SERVER_NAME, mcp_url()],
    ]

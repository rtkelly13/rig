"""Cross-platform background service installer for Rig Web Dashboard (macOS LaunchAgents & Windows Task Scheduler)."""

from __future__ import annotations

import socket
import subprocess
import sys
import time
from pathlib import Path

from desktop_manager.runtime import current_link, running_copy

WEB_LABEL_PROD = "com.rtkelly.rig.web"
WEB_LABEL_PREVIEW = "com.rtkelly.rig.web.preview"
WIN_TASK_PROD = "Shared Utilities\\Web Dashboard"
WIN_TASK_PREVIEW = "Shared Utilities\\Web Dashboard Preview"


def get_service_label(preview: bool) -> str:
    return WEB_LABEL_PREVIEW if preview else WEB_LABEL_PROD


def get_launch_agent_path(preview: bool) -> Path:
    label = get_service_label(preview)
    return Path.home() / "Library" / "LaunchAgents" / f"{label}.plist"


def _resolve_executable() -> Path:
    """Find the desktop-manager executable in current environment or runtime copy."""
    # Check if we are running from an installed copy
    copy_link = current_link()
    if running_copy(__file__) and copy_link.exists():
        if sys.platform == "win32":
            candidate = copy_link / ".venv" / "Scripts" / "desktop-manager.exe"
        else:
            candidate = copy_link / ".venv" / "bin" / "desktop-manager"
        if candidate.exists():
            return candidate

    # Otherwise use current sys.executable's sibling
    bin_dir = Path(sys.executable).parent
    candidate = bin_dir / ("desktop-manager.exe" if sys.platform == "win32" else "desktop-manager")
    if candidate.exists():
        return candidate
    return Path(sys.executable)


def install_web_service(preview: bool = False, port: int | None = None) -> str:
    """Register and start the web dashboard as a background persistent service."""
    effective_port = port if port is not None else (8091 if preview else 8090)
    executable = _resolve_executable()
    preview_arg = " --preview" if preview else ""

    if sys.platform == "darwin":
        plist_path = get_launch_agent_path(preview)
        plist_path.parent.mkdir(parents=True, exist_ok=True)
        label = get_service_label(preview)
        log_name = f"rig-web{'-preview' if preview else ''}"
        stdout_log = Path.home() / "Library" / "Logs" / f"{log_name}.log"
        stderr_log = Path.home() / "Library" / "Logs" / f"{log_name}-err.log"

        # Unload if already registered
        if plist_path.is_file():
            subprocess.run(["launchctl", "unload", str(plist_path)], capture_output=True, check=False)

        plist_content = f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>{label}</string>
    <key>ProgramArguments</key>
    <array>
        <string>{executable}</string>
        <string>web</string>
        <string>--port</string>
        <string>{effective_port}</string>
        <string>--no-open</string>
        {"<string>--preview</string>" if preview else ""}
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>{stdout_log}</string>
    <key>StandardErrorPath</key>
    <string>{stderr_log}</string>
    <key>WorkingDirectory</key>
    <string>{Path.cwd()}</string>
</dict>
</plist>
"""
        plist_path.write_text(plist_content, encoding="utf-8")
        res = subprocess.run(["launchctl", "load", str(plist_path)], capture_output=True, text=True, check=False)
        if res.returncode != 0:
            return f"Service written to {plist_path}, but launchctl load exited {res.returncode}: {res.stderr}"
        return f"Loaded macOS LaunchAgent [{label}] running on port {effective_port} (PID supervised via launchd)."

    if sys.platform == "win32":
        task_name = WIN_TASK_PREVIEW if preview else WIN_TASK_PROD
        cmd = f'"{executable}" web --port {effective_port} --no-open{preview_arg}'
        res = subprocess.run(
            ["schtasks.exe", "/create", "/tn", task_name, "/tr", cmd, "/sc", "onlogon", "/f"],
            capture_output=True,
            text=True,
            check=False,
        )
        if res.returncode != 0:
            return f"Failed to register Windows task {task_name}: {res.stderr}"
        # Start immediately
        subprocess.run(["schtasks.exe", "/run", "/tn", task_name], capture_output=True, check=False)
        return f"Registered and started Windows scheduled task [{task_name}] on port {effective_port}."

    return "Background service installation is supported on macOS and Windows."


def uninstall_web_service(preview: bool = False) -> str:
    """Unload and remove the background web service."""
    if sys.platform == "darwin":
        plist_path = get_launch_agent_path(preview)
        label = get_service_label(preview)
        if plist_path.is_file():
            subprocess.run(["launchctl", "unload", str(plist_path)], capture_output=True, check=False)
            plist_path.unlink(missing_ok=True)
            return f"Unloaded and removed macOS LaunchAgent [{label}]."
        return f"No LaunchAgent found for [{label}]."

    if sys.platform == "win32":
        task_name = WIN_TASK_PREVIEW if preview else WIN_TASK_PROD
        subprocess.run(["schtasks.exe", "/delete", "/tn", task_name, "/f"], capture_output=True, check=False)
        return f"Removed Windows Task Scheduler entry [{task_name}]."

    return "Background service uninstallation is supported on macOS and Windows."


def answering(port: int, host: str = "127.0.0.1") -> bool:
    """Whether something accepts connections on *host*:*port*."""
    try:
        with socket.create_connection((host, port), timeout=0.5):
            return True
    except OSError:
        return False


def ensure_web_service(port: int = 8090, timeout_s: float = 30.0) -> bool:
    """Leave a dashboard answering on *port*: start the service only when nothing answers yet.

    Safe to run on every restore: a running dashboard is left alone, where `install_web_service`
    would restart it. True once something answers, False when nothing did within *timeout_s*.
    """
    if answering(port):
        return True
    _ = install_web_service(port=port)
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        if answering(port):
            return True
        time.sleep(0.5)
    return False

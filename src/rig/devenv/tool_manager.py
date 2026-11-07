"""Tool management orchestration across package managers."""

from __future__ import annotations

import shutil

from .config.models import ToolDefinition
from .engine.context import Platform, get_current_platform
from .providers.base import PackageManager
from .providers.homebrew import HomebrewManager
from .providers.winget import WingetManager


class ToolManager:
    """Orchestrates checking, installing, and updating tools."""

    def __init__(self, platform: Platform | None = None) -> None:
        self.platform: Platform = platform or get_current_platform()
        self.brew: HomebrewManager = HomebrewManager()
        self.winget: WingetManager = WingetManager()

    def get_package_manager(self) -> PackageManager | None:
        """Return the primary package manager for the current OS."""
        if self.platform == Platform.WINDOWS:
            return self.winget if self.winget.is_available() else None
        if self.platform in (Platform.MACOS, Platform.LINUX):
            return self.brew if self.brew.is_available() else None
        return None

    def is_tool_installed(self, tool: ToolDefinition) -> bool:
        """Check if tool binary or package is present."""
        if tool.install.check_cmd and shutil.which(tool.install.check_cmd):
            return True

        pm = self.get_package_manager()
        if not pm:
            return False

        if self.platform == Platform.WINDOWS and tool.install.winget:
            return pm.is_installed(tool.install.winget)
        if self.platform in (Platform.MACOS, Platform.LINUX) and tool.install.brew:
            return pm.is_installed(tool.install.brew)

        return False

    def install_tool(self, tool: ToolDefinition, silent: bool = True) -> bool:
        """Install a single tool using the appropriate package manager."""
        if self.is_tool_installed(tool):
            return True

        pm = self.get_package_manager()
        if not pm:
            return False

        if self.platform == Platform.WINDOWS and tool.install.winget:
            return pm.install(tool.install.winget, silent=silent)
        if self.platform in (Platform.MACOS, Platform.LINUX) and tool.install.brew:
            return pm.install(tool.install.brew, silent=silent)

        return False

    def update_tool(self, tool: ToolDefinition, silent: bool = True) -> bool:
        """Update a tool if package manager supports it."""
        pm = self.get_package_manager()
        if not pm:
            return False

        if self.platform == Platform.WINDOWS and tool.install.winget:
            return pm.update(tool.install.winget, silent=silent)
        if self.platform in (Platform.MACOS, Platform.LINUX) and tool.install.brew:
            return pm.update(tool.install.brew, silent=silent)

        return False

    def update_all_tools(self, silent: bool = False) -> bool:
        """Update all tools via primary package manager."""
        pm = self.get_package_manager()
        if not pm:
            return False
        return pm.update_all(silent=silent)

"""Step execution context and platform detection."""

from __future__ import annotations

from dataclasses import dataclass, field

from rig.platforms import Platform, get_current_platform

__all__ = ["Platform", "StepContext", "get_current_platform"]


@dataclass
class StepContext:
    """Runtime context passed to each setup step."""

    platform: Platform = field(default_factory=get_current_platform)
    mode: str = "full"  # "full", "express", "agent"
    dry_run: bool = False
    use_silent_mode: bool = True
    verbose: bool = False

    @property
    def is_windows(self) -> bool:
        return self.platform == Platform.WINDOWS

    @property
    def is_macos(self) -> bool:
        return self.platform == Platform.MACOS

    @property
    def is_linux(self) -> bool:
        return self.platform == Platform.LINUX

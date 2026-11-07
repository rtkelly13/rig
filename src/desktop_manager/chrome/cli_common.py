"""Shared by the `chrome-tabs` commands: the console, trace spans, plan lines and window reads."""

from __future__ import annotations

from collections.abc import Generator, Sequence
from contextlib import AbstractContextManager, contextmanager, suppress
from typing import TYPE_CHECKING

import typer
from rich.console import Console
from rich.markup import escape

from desktop_manager.chrome.backend import ChromeSession
from desktop_manager.chrome.pr_prune import PlacedTab
from desktop_manager.chrome.tabs import ChromeWindow
from desktop_manager.chrome.traces import SCOPE
from rig import tracing

if TYPE_CHECKING:
    from desktop_manager.chrome.desktops import Desktops
    from desktop_manager.chrome.driver import ChromeDriver


console = Console(soft_wrap=True)


def command_driver(ctx: typer.Context) -> ChromeDriver:
    """The command's driver: the `ChromeSession` a caller passed as the context's `obj`, else this platform's."""
    return ctx.ensure_object(ChromeSession).driver


def trace_span(name: str, **attrs: tracing.AttributeValue) -> AbstractContextManager[tracing.Span]:
    return tracing.span(f"chrome_tabs.{name}", scope=SCOPE, **attrs)


def filter_profile(windows: list[ChromeWindow], profile: str | None) -> list[ChromeWindow]:
    if profile is None:
        return windows
    wanted = profile.casefold()
    return [
        window
        for window in windows
        if wanted in {(window.profile_dir or "").casefold(), (window.profile_name or "").casefold()}
    ]


def list_profile_windows(driver: ChromeDriver, profile: str | None, *, phase: str) -> list[ChromeWindow]:
    """`list_windows` for `profile`, as one span."""
    with trace_span("read_windows", **{"read.phase": phase}) as span:
        windows = filter_profile(driver.list_windows(), profile)
        span.set(**{"chrome.windows": len(windows), "chrome.tabs": sum(len(window.tabs) for window in windows)})
        return windows


def plan_line(item: PlacedTab, why: str) -> str:
    where = f"{item.profile}, window {item.window_number}, tab {item.tab.index + 1}"
    return f"  {item.label}  [{where}]  {why}  | {short(item.tab.title)}"


def short(title: str, width: int = 70) -> str:
    return title if len(title) <= width else title[: width - 1] + "…"


def print_lines(lines: Sequence[str]) -> None:
    for line in lines:
        console.print(escape(line), style=None if line.startswith(" ") else "bold")


@contextmanager
def keep_desktop(desktops: Desktops) -> Generator[None]:
    """Put the user back on the virtual desktop they were on, if the steps inside moved them.

    Bringing a window forward, or sending it back home while it has the focus, can
    take Windows to that window's desktop."""
    start = desktops.current()
    try:
        yield
    finally:
        with suppress(Exception):
            now = desktops.current()
            if start is not None and now is not None and now != start:
                with trace_span("restore_desktop", **{"desktop.from": now, "desktop.to": start}):
                    desktops.go_to(start)

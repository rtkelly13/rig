"""Which operating systems a tool supports, detected one way everywhere.

Two kinds of platform check exist, and each has one spelling:

- **Capability gates** answer "does this command run here at all?". Decorate the
  command with :func:`requires`. It refuses cleanly off-platform, tags the command's
  ``--help`` text, and records the platforms on the function so tooling (the README
  table, the entry-point smoke test) can list Windows-only commands without reading
  their bodies.
- **Low-level branches** inside code that already knows it may run anywhere (path
  semantics, process flags) test ``sys.platform == "win32"`` literally. Pyright
  narrows on that exact expression, which is what makes ``ctypes.windll`` and
  ``winreg`` type-check; a helper function would hide it. Never ``os.name`` or
  ``platform.system()``: :func:`get_current_platform` reads ``sys.platform`` too.
"""

from __future__ import annotations

import functools
import inspect
import sys
from collections.abc import Callable
from enum import StrEnum
from types import BuiltinFunctionType, FunctionType, MethodType
from typing import Protocol

import click
import typer

_DECLARED: dict[Callable[..., object], tuple[Platform, ...]] = {}


class _Unwrap(Protocol):
    def __call__(
        self, func: Callable[..., object], /, *, stop: Callable[[Callable[..., object]], bool]
    ) -> Callable[..., object]: ...


_UNWRAP: _Unwrap = inspect.unwrap


class Platform(StrEnum):
    """Supported operating systems."""

    WINDOWS = "Windows"
    MACOS = "macOS"
    LINUX = "Linux"
    UNKNOWN = "Unknown"


def get_current_platform() -> Platform:
    """Detect the operating system this process runs on.

    Reads ``sys.platform``, the same value the low-level branches test, so one
    monkeypatch of ``sys.platform`` moves both kinds of check together.
    """
    if sys.platform == "win32":
        return Platform.WINDOWS
    if sys.platform == "darwin":
        return Platform.MACOS
    if sys.platform.startswith("linux"):
        return Platform.LINUX
    return Platform.UNKNOWN


def platform_label(platforms: tuple[Platform, ...]) -> str:
    """Human label for a set of platforms: ``Windows``, ``Windows/macOS``."""
    return "/".join(p.value for p in platforms)


def supported_platforms(func: Callable[..., object]) -> tuple[Platform, ...] | None:
    """The platforms a :func:`requires`-decorated function declared, else None (portable).

    Follows ``__wrapped__``, so it also finds the declaration through wrappers Typer adds.
    """
    return _DECLARED.get(_UNWRAP(func, stop=lambda f: f in _DECLARED))


def requires[**P, R](*platforms: Platform) -> Callable[[Callable[P, R]], Callable[P, R]]:
    """Mark a CLI command as running only on ``platforms``.

    Off-platform the command prints ``<name> is only available on <platforms>.`` to
    stderr and exits 1, instead of failing later with an import error or traceback.
    The first docstring line gains a ``[Windows only]`` suffix so ``--help`` shows it.
    Apply it under the Typer ``@app.command`` decorator.
    """
    if not platforms:
        raise ValueError("requires() needs at least one platform")
    declared = tuple(platforms)
    label = platform_label(declared)

    def decorator(func: Callable[P, R]) -> Callable[P, R]:
        @functools.wraps(func)
        def wrapper(*args: P.args, **kwargs: P.kwargs) -> R:
            if get_current_platform() not in declared:
                typer.echo(f"{_command_name(func)} is only available on {label}.", err=True)
                raise typer.Exit(code=1)
            return func(*args, **kwargs)

        wrapper.__doc__ = _tag_doc(func.__doc__, label)
        _DECLARED[wrapper] = declared
        return wrapper

    return decorator


def _command_name(func: Callable[..., object]) -> str:
    """The name the user typed (``status``), falling back to the function name."""
    ctx = click.get_current_context(silent=True)
    if ctx is not None and ctx.info_name:
        return ctx.info_name
    return callable_name(func).removesuffix("_cmd").replace("_", "-")


def callable_name(fn: Callable[..., object]) -> str:
    """``fn.__name__`` for functions and methods; the type's name for any other callable."""
    if isinstance(fn, FunctionType | MethodType | BuiltinFunctionType):
        return fn.__name__
    return type(fn).__name__


def _tag_doc(doc: str | None, label: str) -> str:
    tag = f"[{label} only]"
    if not doc:
        return tag
    first, sep, rest = doc.partition("\n")
    return f"{first.rstrip()} {tag}{sep}{rest}"


__all__ = [
    "Platform",
    "get_current_platform",
    "platform_label",
    "requires",
    "supported_platforms",
]

"""Terminal compatibility, emulation, and UI rendering helpers."""

from __future__ import annotations

import contextlib
import io
import json
import os
import sys
from typing import TextIO, override

from rich import box
from rich.console import Console
from rich.table import Table


class TerminalState:
    """Manages terminal rendering state and plain-text flags."""

    plain_mode: bool = os.environ.get("NO_COLOR", "") != ""


def enable_plain_mode() -> None:
    """Switch all ui output functions to plain-text / ASCII mode."""
    TerminalState.plain_mode = True


def is_plain_mode() -> bool:
    """Return True when plain-text mode is active."""
    return TerminalState.plain_mode


def _stdout_supports_unicode(file: TextIO | None = None) -> bool:
    """Return True if file (default sys.stdout) can render Unicode safely."""
    f = file or sys.stdout

    # Known Unicode-capable terminals across OS platforms
    if os.environ.get("WT_SESSION"):  # Windows Terminal
        return True
    if os.environ.get("TERM_PROGRAM") in ("vscode", "iTerm.app", "Hyper", "Apple_Terminal", "Ghostty"):
        return True
    if os.environ.get("CONEMUPID"):  # ConEmu / cmder
        return True
    term = os.environ.get("TERM", "")
    if term.startswith(("xterm", "rxvt", "screen", "tmux", "alacritty", "kitty")):
        return True
    if term in ("linux",):  # Linux kernel console
        return True

    # Encoding probe
    encoding = f.encoding or "ascii"
    if encoding.lower() in ("utf-8", "utf8", "utf_8"):
        return True

    try:
        "\u2500\u2502".encode(encoding)  # Rich table borders probe
        return True
    except (UnicodeEncodeError, LookupError):
        return False


_UNICODE_REPLACEMENTS = {
    "\u2026": "...",  # … ellipsis
    "\u2013": "-",  # – en dash
    "\u2014": "--",  # — em dash
    "\u2015": "--",  # ― horizontal bar
    "\u2713": "OK",  # ✓ check mark
    "\u2714": "+",  # ✔ heavy check mark
    "\u2717": "X",  # ✗ cross mark
    "\u26a0": "!!",  # ⚠ warning
    "\u2139": "[i]",  # ℹ info
    "\u2192": "->",  # → right arrow
    "\u2191": "^",  # ↑ up arrow
    "\u2022": "-",  # • bullet
    "\u2605": "*",  # ★ star
    "\u25b6": ">",  # ▶ triangle
    "\u25c6": "#",  # ◆ diamond
    "\u25cf": ".",  # ● circle
    "\u25ce": "o",  # ◎ bullseye
    "\u25cb": "o",  # ○ circle
    "\U0001f4e6": "[PKG]",
    "\U0001f5d1\ufe0f": "[DEL]",
    "\u280b": "*",
    # Box-drawing
    "\u2500": "-",
    "\u2501": "-",
    "\u2502": "|",
    "\u2503": "|",
    "\u250c": "+",
    "\u2510": "+",
    "\u2514": "+",
    "\u2518": "+",
    "\u251c": "+",
    "\u2524": "+",
    "\u252c": "+",
    "\u2534": "+",
    "\u253c": "+",
    # Heavy box-drawing (Rich HEAVY_HEAD)
    "\u250f": "+",
    "\u2513": "+",
    "\u2517": "+",
    "\u251b": "+",
    "\u2523": "+",
    "\u252b": "+",
    "\u2533": "+",
    "\u253b": "+",
    "\u254b": "+",
    "\u2520": "+",
    "\u2528": "+",
    "\u2521": "+",
    "\u2529": "+",
    "\u2547": "+",
    "\u2548": "+",
    "\u252f": "+",
    "\u2537": "+",
    # Rounded corners (box.ROUNDED)
    "\u256d": "+",
    "\u256e": "+",
    "\u256f": "+",
    "\u2570": "+",
    # Double-line box-drawing (box.DOUBLE)
    "\u2550": "=",
    "\u2551": "|",
    "\u2554": "+",
    "\u2557": "+",
    "\u255a": "+",
    "\u255d": "+",
    "\u2560": "+",
    "\u2563": "+",
    "\u2566": "+",
    "\u2569": "+",
    "\u256c": "+",
}


def sanitize_text(text: str) -> str:
    """Replace unicode symbols when output cannot be encoded safely."""
    if _stdout_supports_unicode():
        return text

    cleaned = text
    for symbol, replacement in _UNICODE_REPLACEMENTS.items():
        cleaned = cleaned.replace(symbol, replacement)

    encoding = sys.stdout.encoding
    if encoding:
        with contextlib.suppress(Exception):
            cleaned = cleaned.encode(encoding, errors="replace").decode(encoding)
    return cleaned


class _SanitizingWriter(io.StringIO):
    """Text stream that sanitizes Unicode on write() for non-Unicode environments, then forwards it."""

    def __init__(self, file: TextIO | None = None) -> None:
        super().__init__()
        self._explicit: TextIO | None = file

    @property
    def _file(self) -> TextIO:
        return self._explicit if self._explicit is not None else sys.stdout

    @property
    def target_encoding(self) -> str | None:
        """The encoding of the stream written through to, which Rich should render for."""
        return self._file.encoding

    @override
    def write(self, s: str, /) -> int:
        return self._file.write(sanitize_text(s))

    @override
    def flush(self) -> None:
        self._file.flush()

    @override
    def isatty(self) -> bool:
        return self._file.isatty()

    @override
    def fileno(self) -> int:
        return self._file.fileno()


class _SanitizingConsole(Console):
    """A Console over a `_SanitizingWriter` that picks glyphs for the stream behind it."""

    def __init__(self, writer: _SanitizingWriter, *, width: int | None = None) -> None:
        super().__init__(file=writer, width=width, force_terminal=True, legacy_windows=False)
        self._writer: _SanitizingWriter = writer

    @property
    @override
    def encoding(self) -> str:
        return (self._writer.target_encoding or "utf-8").lower()


def make_console(file: TextIO | None = None) -> Console:
    """Create a Rich Console that degrades safely on any terminal environment."""
    f = file or sys.stdout
    supports_unicode = _stdout_supports_unicode(f)

    if not supports_unicode:
        return _SanitizingConsole(_SanitizingWriter(file))

    if sys.platform == "win32":
        if isinstance(f, io.TextIOWrapper) and f.encoding.lower() not in ("utf-8", "utf8", "utf_8"):
            f.reconfigure(encoding="utf-8")
        return Console(file=file, legacy_windows=False)

    return Console()


def table_box(unicode_box: box.Box | None = None) -> box.Box:
    """Return a Rich table box style safe for the current terminal."""
    if not _stdout_supports_unicode():
        return box.ASCII
    return unicode_box if unicode_box is not None else box.ROUNDED


console = make_console()


def md_table(headers: list[str], rows: list[list[str]]) -> str:
    """Render a GitHub-flavored Markdown table as a string."""
    escaped_headers = [h.replace("|", r"\|") for h in headers]
    lines = [
        "| " + " | ".join(escaped_headers) + " |",
        "| " + " | ".join(["---"] * len(headers)) + " |",
    ]
    for row in rows:
        escaped_cells = [cell.replace("|", r"\|").replace("\n", " ") for cell in row]
        lines.append("| " + " | ".join(escaped_cells) + " |")
    return "\n".join(lines) + "\n"


def print_json(data: object) -> None:
    """Print data formatted as indented JSON."""
    formatted = json.dumps(data, indent=2, ensure_ascii=True)
    sys.stdout.write(formatted + "\n")


def render_table_to_string(table: Table, width: int = 100, force_legacy: bool = False) -> str:
    """Render a Rich Table to a plain string buffer with terminal emulation."""
    buf = io.StringIO()
    if force_legacy:
        test_console = _SanitizingConsole(_SanitizingWriter(buf), width=width)
    else:
        test_console = Console(file=buf, width=width, highlight=False)
    test_console.print(table)
    return buf.getvalue()

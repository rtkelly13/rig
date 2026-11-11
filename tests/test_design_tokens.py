"""Gate: rig's stylesheets and templates take every colour and size from the design system.

Colours, type sizes and spacing come from `@rtkelly-labs/design-system` tokens, through Tailwind
utilities (`bg-surface-raised`, `text-caption`, `max-w-325`) or `var(--ds-*)`. A raw hex or `rgb()`
colour, or an arbitrary-value class such as `text-[0.82rem]`, bypasses the tokens and drifts
from the design system. Where a token is missing, the fix belongs upstream in
rtkelly-labs/design-system, not here.

Compiled output (`gallery.css`, `app_assets/`, the C4 viewer's `c4.css`) and vendored files are
out of scope: Tailwind writes the theme's literal values into them.
"""

from __future__ import annotations

import re
from pathlib import Path

import pytest

pytestmark = pytest.mark.ci

REPO_ROOT = Path(__file__).resolve().parents[1]

SCANNED = (
    "styles/*.css",
    "web/*.css",
    "web/*.tsx",
    "src/rig/image_drift/templates/*.jinja",
    "src/rig/web_host/templates/*.html",
    "src/rig/web_host/templates/studio.css",
    "src/rig/c4_okf/static/*.html",
)

# A hex colour, not an HTML entity (`&#128204;`) or an id fragment (`#add-row`).
RAW_HEX = re.compile(r"(?<![&\w])#[0-9a-fA-F]{3,8}\b(?![\w-])")
RAW_FUNCTION = re.compile(r"\b(?:rgba?|hsla?)\(")
# `text-[0.82rem]`, `hover:max-w-[1200px]`, `-left-[3px]`.
ARBITRARY_VALUE = re.compile(r"(?<![\w-])-?[a-z][\w-]*-\[[^\]\s]+\]")
# `[image-rendering:pixelated]`; variants like `[&_svg]:size-3` start with `&` and are allowed.
ARBITRARY_PROPERTY = re.compile(r"(?<![\w&-])\[[a-z-]+:[^\]\s]+\]")


def violations(text: str) -> list[str]:
    """Every raw colour or arbitrary-value class in a stylesheet or template."""
    found: list[str] = []
    for pattern in (RAW_HEX, RAW_FUNCTION, ARBITRARY_VALUE, ARBITRARY_PROPERTY):
        found.extend(match.group(0) for match in pattern.finditer(text))
    return found


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ('<p class="text-caption text-content-muted">', []),
        ("color: var(--ds-accent-primary);", []),
        ("background: color-mix(in oklab, var(--ds-accent-primary) 12%, transparent);", []),
        ("<span>&#128204; Canonical</span>", []),
        ('<a href="#add-row">', []),
        ('<a className="[&_svg]:size-3">', []),
        ('[data-theme="midnight"] .c4-top {', []),
        ("color: #0a0a1a;", ["#0a0a1a"]),
        ("background: rgba(34, 211, 238, 0.12);", ["rgba("]),
        ("fill: hsl(10 20% 30%);", ["hsl("]),
        ('<p class="text-[0.82rem] max-w-[1200px]">', ["text-[0.82rem]", "max-w-[1200px]"]),
        ('<b class="hover:border-[#f59e0b]">', ["#f59e0b", "border-[#f59e0b]"]),
        ("@apply w-[110px];", ["w-[110px]"]),
        ('<img class="[image-rendering:pixelated]">', ["[image-rendering:pixelated]"]),
    ],
)
def test_violation_detection(text: str, expected: list[str]) -> None:
    assert sorted(violations(text)) == sorted(expected)


def _scanned_files() -> list[Path]:
    files = {path for pattern in SCANNED for path in REPO_ROOT.glob(pattern)}
    return sorted(files)


def test_scope_covers_the_sources() -> None:
    names = {path.relative_to(REPO_ROOT).as_posix() for path in _scanned_files()}
    assert {"styles/gallery.src.css", "styles/c4.css", "web/app.src.css", "web/App.tsx"} <= names


def test_stylesheets_and_templates_use_design_system_tokens() -> None:
    found: list[tuple[str, str]] = []
    for path in _scanned_files():
        relative = path.relative_to(REPO_ROOT).as_posix()
        found.extend((relative, hit) for hit in violations(path.read_text(encoding="utf-8")))
    assert not found, (
        "Use @rtkelly-labs/design-system tokens (Tailwind utilities or var(--ds-*)), not raw colours "
        f"or arbitrary values; a missing token goes upstream: {found}"
    )

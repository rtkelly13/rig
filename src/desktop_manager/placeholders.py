"""Substitution of ``{name}`` placeholders in plan values."""

from __future__ import annotations

from desktop_manager.models import PLACEHOLDER_PATTERN


def substitute_placeholders(value: str | None, substitutions: dict[str, str]) -> str | None:
    """Replace all ``{name}`` placeholders using the provided mapping."""
    if value is None:
        return None
    for name, replacement in substitutions.items():
        value = value.replace(f"{{{name}}}", replacement)
    unresolved = {match.group(0) for match in PLACEHOLDER_PATTERN.finditer(value)}
    if unresolved:
        placeholders = ", ".join(sorted(unresolved))
        raise ValueError(f"Unresolved placeholders: {placeholders}")
    return value

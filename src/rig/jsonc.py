"""JSON with comments and trailing commas (JSONC), as VS Code writes its settings files.

Two things, both string-aware: ``loads`` parses JSONC into plain JSON values, and ``members`` reports
where each key of an object sits in the source text so callers can insert text without rewriting
the file. Rewriting through ``json.dump`` would drop every comment the user wrote.
"""

from __future__ import annotations

from dataclasses import dataclass

from rig.typed_json import load_json

__all__ = ["JsoncError", "Member", "comments_above", "loads", "members", "root_object"]

_WHITESPACE = " \t\r\n﻿"


class JsoncError(ValueError):
    """The text is not a JSONC document this module can read."""


@dataclass(frozen=True, slots=True)
class Member:
    """One ``"key": value`` pair of an object, with offsets into the source text."""

    key: str
    key_start: int
    value_start: int
    value_end: int


def _skip_trivia(text: str, i: int) -> int:
    """Skip whitespace and comments starting at ``i``."""
    n = len(text)
    while i < n:
        if text[i] in _WHITESPACE:
            i += 1
        elif text.startswith("//", i):
            end = text.find("\n", i)
            i = n if end < 0 else end + 1
        elif text.startswith("/*", i):
            end = text.find("*/", i + 2)
            if end < 0:
                raise JsoncError(f"unterminated block comment at offset {i}")
            i = end + 2
        else:
            break
    return i


def _skip_string(text: str, i: int) -> int:
    """Return the offset just past the string literal that starts at ``i``."""
    i += 1
    n = len(text)
    while i < n:
        c = text[i]
        if c == "\\":
            i += 2
        elif c == '"':
            return i + 1
        else:
            i += 1
    raise JsoncError("unterminated string")


def _skip_value(text: str, i: int) -> int:
    """Return the offset just past the value that starts at ``i``."""
    if i >= len(text):
        raise JsoncError("expected a value, found end of text")
    c = text[i]
    if c == '"':
        return _skip_string(text, i)
    if c in "{[":
        depth = 0
        n = len(text)
        while i < n:
            i = _skip_trivia(text, i)
            if i >= n:
                break
            c = text[i]
            if c == '"':
                i = _skip_string(text, i)
                continue
            if c in "{[":
                depth += 1
            elif c in "}]":
                depth -= 1
                if depth == 0:
                    return i + 1
            i += 1
        raise JsoncError("unterminated object or array")
    start = i
    while i < len(text) and text[i] not in ",}]/" and text[i] not in _WHITESPACE:
        i += 1
    if i == start:
        raise JsoncError(f"expected a value at offset {start}")
    return i


def root_object(text: str) -> int:
    """Offset of the ``{`` that opens the document's top-level object."""
    i = _skip_trivia(text, 0)
    if i >= len(text) or text[i] != "{":
        raise JsoncError("the document is not a JSON object")
    return i


def members(text: str, open_brace: int) -> list[Member]:
    """The members of the object whose ``{`` is at ``open_brace``, in source order."""
    if text[open_brace] != "{":
        raise JsoncError(f"no object at offset {open_brace}")
    found: list[Member] = []
    i = open_brace + 1
    while True:
        i = _skip_trivia(text, i)
        if i >= len(text):
            raise JsoncError("unterminated object")
        c = text[i]
        if c == "}":
            return found
        if c == ",":
            i += 1
            continue
        if c != '"':
            raise JsoncError(f"expected a key at offset {i}")
        key_end = _skip_string(text, i)
        key = load_json(text[i:key_end])
        if not isinstance(key, str):
            raise JsoncError(f"bad key at offset {i}")
        colon = _skip_trivia(text, key_end)
        if colon >= len(text) or text[colon] != ":":
            raise JsoncError(f"expected ':' after key {key!r}")
        value_start = _skip_trivia(text, colon + 1)
        value_end = _skip_value(text, value_start)
        found.append(Member(key=key, key_start=i, value_start=value_start, value_end=value_end))
        i = value_end


def comments_above(text: str, start: int) -> list[str]:
    """The ``//`` comment lines directly above the line holding ``start``, without the ``//``.

    Empty when something other than whitespace comes before ``start`` on its line.
    """
    line_start = text.rfind("\n", 0, start) + 1
    if text[line_start:start].strip():
        return []
    found: list[str] = []
    end = line_start - 1
    while end > 0:
        prev_start = text.rfind("\n", 0, end) + 1
        line = text[prev_start:end].strip()
        if not line.startswith("//"):
            break
        found.append(line[2:].strip())
        end = prev_start - 1
    found.reverse()
    return found


def _to_json(text: str) -> str:
    """Drop comments and trailing commas so the standard JSON parser accepts the text."""
    out: list[str] = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == '"':
            end = _skip_string(text, i)
            out.append(text[i:end])
            i = end
        elif text.startswith("//", i) or text.startswith("/*", i):
            i = _skip_trivia(text, i)
            out.append(" ")
        elif c == ",":
            nxt = _skip_trivia(text, i + 1)
            if nxt < n and text[nxt] in "}]":
                i += 1
            else:
                out.append(c)
                i += 1
        else:
            out.append(c)
            i += 1
    return "".join(out).lstrip("﻿")


def loads(text: str) -> object:
    """Parse JSONC text into plain JSON values."""
    try:
        return load_json(_to_json(text))
    except ValueError as exc:
        raise JsoncError(str(exc)) from exc

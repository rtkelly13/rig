"""The frontmatter keys the vault tools read by name, decoded once from a note's free-form metadata."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Annotated

from pydantic import Field, StrictStr

from rig.typed_json import Lenient, decode


@dataclass(frozen=True, slots=True)
class NoteMeta:
    """Named frontmatter fields; a value of the wrong type reads as absent."""

    type: Lenient[StrictStr] = None
    title: Lenient[StrictStr] = None
    source: Lenient[StrictStr] = None
    url: Lenient[StrictStr] = None
    related_article: Lenient[StrictStr] = None
    lint_ignore: Lenient[StrictStr | list[object]] = None
    lint_ignore_kebab: Annotated[Lenient[StrictStr | list[object]], Field(alias="lint-ignore")] = None


def read_note_meta(metadata: object) -> NoteMeta:
    """Decode the named fields out of a note's frontmatter mapping, ignoring every other key."""
    return decode(NoteMeta, metadata)

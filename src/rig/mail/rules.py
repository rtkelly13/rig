"""The rules file: its schema, loading, and the Gmail search query each rule stands for."""

from __future__ import annotations

import os
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Annotated, ClassVar

import yaml
from pydantic import ConfigDict, Field, StrictBool, StrictStr, ValidationError

from rig.repo_governance.align_repo_conventions import personal_repos_dir
from rig.typed_json import decode
from rig.typed_yaml import load_yaml

RULES_ENV = "MAIL_RULES"
_LOCAL_RULES = Path("rig") / "temp" / "mail" / "rules.yaml"
_DURATION = re.compile(r"^\d+[dmy]$")
_FORBID: ConfigDict = ConfigDict(extra="forbid")


class RulesError(ValueError):
    """The rules file is missing, malformed, or describes a rule that cannot work."""


@dataclass(frozen=True, slots=True)
class Match:
    """What a rule selects. Every populated field must hold; lists inside a field are ORed."""

    __pydantic_config__: ClassVar[ConfigDict] = _FORBID

    senders: Annotated[tuple[StrictStr, ...], Field(alias="from")] = ()
    to: tuple[StrictStr, ...] = ()
    subject: tuple[StrictStr, ...] = ()
    query: StrictStr = ""
    exclude: StrictStr = ""

    @property
    def is_empty(self) -> bool:
        """True when the rule would match every message."""
        return not (self.senders or self.to or self.subject or self.query)


@dataclass(frozen=True, slots=True)
class Actions:
    """What happens to a matching thread. Mirrors the actions a Gmail filter can take."""

    __pydantic_config__: ClassVar[ConfigDict] = _FORBID

    label: StrictStr | None = None
    archive: StrictBool = False
    mark_read: StrictBool = False
    star: StrictBool = False
    important: StrictBool | None = None
    trash: StrictBool = False

    @property
    def is_empty(self) -> bool:
        """True when the rule would do nothing."""
        return not (self.label or self.archive or self.mark_read or self.star or self.trash) and self.important is None

    @property
    def leaves_inbox(self) -> bool:
        """True when a matching thread ends up out of the inbox."""
        return self.archive or self.trash


@dataclass(frozen=True, slots=True)
class Rule:
    """One named rule. ``older_than`` makes it sweep-only: filters act on arrival, never on age."""

    __pydantic_config__: ClassVar[ConfigDict] = _FORBID

    name: StrictStr
    match: Match
    actions: Actions
    older_than: StrictStr | None = None
    note: StrictStr = ""

    @property
    def exportable(self) -> bool:
        """Whether a Gmail filter can express this rule."""
        return self.older_than is None


@dataclass(frozen=True, slots=True)
class RuleSet:
    """The whole file. Rules apply in order; the first rule that claims a sender wins in coverage."""

    __pydantic_config__: ClassVar[ConfigDict] = _FORBID

    rules: tuple[Rule, ...] = field(default_factory=tuple)


def default_rules_path() -> Path:
    """``$MAIL_RULES``, else ``temp/mail/rules.yaml`` in the main rig checkout (gitignored: it holds addresses)."""
    override = os.environ.get(RULES_ENV)
    if override:
        return Path(override).expanduser()
    return personal_repos_dir() / _LOCAL_RULES


def parse_rules(text: str, where: str = "rules") -> RuleSet:
    """Decode and validate rules text; every problem is a ``RulesError`` naming the rule."""
    try:
        ruleset = decode(RuleSet, load_yaml(text))
    except yaml.YAMLError as error:
        raise RulesError(f"{where}: not valid YAML ({error})") from error
    except ValidationError as error:
        raise RulesError(f"{where}: {error}") from error
    _validate(ruleset, where)
    return ruleset


def load_rules(path: Path) -> RuleSet:
    """Read and validate the rules file at ``path``."""
    if not path.is_file():
        raise RulesError(f"no rules file at {path.as_posix()} (set --rules or ${RULES_ENV})")
    return parse_rules(path.read_text(encoding="utf-8"), path.as_posix())


def _validate(ruleset: RuleSet, where: str) -> None:
    seen: set[str] = set()
    for rule in ruleset.rules:
        prefix = f"{where}: rule `{rule.name}`"
        if rule.name in seen:
            raise RulesError(f"{prefix} is defined twice")
        seen.add(rule.name)
        if rule.match.is_empty:
            raise RulesError(f"{prefix} matches everything; give it from, to, subject or query")
        if rule.actions.is_empty:
            raise RulesError(f"{prefix} has no actions")
        if rule.older_than is not None and not _DURATION.match(rule.older_than):
            raise RulesError(f"{prefix}: older_than must look like 3d, 2m or 1y, not {rule.older_than!r}")


def _quote(term: str) -> str:
    """Quote a term that holds whitespace so Gmail reads it as a phrase."""
    return f'"{term}"' if any(ch.isspace() for ch in term) and not term.startswith('"') else term


def _any_of(operator: str, values: tuple[str, ...]) -> str:
    terms = [_quote(value) for value in values]
    joined = terms[0] if len(terms) == 1 else "(" + " OR ".join(terms) + ")"
    return f"{operator}:{joined}"


def match_query(match: Match) -> str:
    """The Gmail search that selects what ``match`` selects, as a filter would see it."""
    parts: list[str] = []
    if match.senders:
        parts.append(_any_of("from", match.senders))
    if match.to:
        parts.append(_any_of("to", match.to))
    if match.subject:
        parts.append(_any_of("subject", match.subject))
    if match.query:
        parts.append(f"({match.query})")
    if match.exclude:
        # Gmail renders a filter's "Doesn't have" field as -{...}; match that exactly.
        parts.append(f"-{{{match.exclude}}}")
    return " ".join(parts)


def sweep_query(rule: Rule) -> str:
    """The search for threads this rule has not acted on yet.

    A rule that clears the inbox only needs to look in the inbox. A rule that leaves mail in
    place looks everywhere for mail still missing *any* of its actions (label, star, read,
    importance), so adding an action to an existing rule catches up mail it already labelled,
    and a re-run is a no-op.
    """
    parts = [match_query(rule.match)]
    if rule.actions.leaves_inbox:
        parts.append("in:inbox")
    else:
        unmet = _unmet_terms(rule.actions)
        if unmet:
            parts.append(unmet[0] if len(unmet) == 1 else "{" + " ".join(unmet) + "}")
    if rule.older_than:
        parts.append(f"older_than:{rule.older_than}")
    return " ".join(parts)


def _unmet_terms(actions: Actions) -> list[str]:
    """Gmail terms that each match mail still missing one of ``actions``; OR them to find work."""
    terms: list[str] = []
    if actions.label:
        terms.append(f"-label:{search_label(actions.label)}")
    if actions.star:
        terms.append("-is:starred")
    if actions.mark_read:
        terms.append("is:unread")
    if actions.important is not None:
        terms.append("-is:important" if actions.important else "is:important")
    return terms


def search_label(name: str) -> str:
    """Gmail's search form of a label name: lowercase, spaces and ``/`` become ``-``."""
    return re.sub(r"[\s/]+", "-", name.strip()).lower()

"""Compile rules into Gmail's filter-import XML and into the connector's sweep plan."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import UTC, datetime
from xml.etree.ElementTree import Element, SubElement, indent, tostring

from pydantic import TypeAdapter

from rig.mail.rules import Rule, RuleSet, sweep_query

_ATOM = "http://www.w3.org/2005/Atom"
_APPS = "http://schemas.google.com/apps/2006"


@dataclass(frozen=True, slots=True)
class FilterProperty:
    """One ``<apps:property>`` of a Gmail filter entry."""

    name: str
    value: str


def _joined(values: tuple[str, ...]) -> str:
    return " OR ".join(f'"{v}"' if any(c.isspace() for c in v) else v for v in values)


def filter_properties(rule: Rule) -> list[FilterProperty]:
    """The criteria and actions of ``rule`` as Gmail filter properties, in Gmail's own order."""
    match, actions = rule.match, rule.actions
    props: list[FilterProperty] = []
    if match.senders:
        props.append(FilterProperty("from", _joined(match.senders)))
    if match.to:
        props.append(FilterProperty("to", _joined(match.to)))
    if match.subject:
        props.append(FilterProperty("subject", _joined(match.subject)))
    if match.query:
        props.append(FilterProperty("hasTheWord", match.query))
    if match.exclude:
        props.append(FilterProperty("doesNotHaveTheWord", match.exclude))
    if actions.label:
        props.append(FilterProperty("label", actions.label))
    flags = {
        "shouldArchive": actions.archive,
        "shouldMarkAsRead": actions.mark_read,
        "shouldStar": actions.star,
        "shouldTrash": actions.trash,
        "shouldAlwaysMarkAsImportant": actions.important is True,
        "shouldNeverMarkAsImportant": actions.important is False,
    }
    props.extend(FilterProperty(name, "true") for name, on in flags.items() if on)
    return props


def filters_xml(ruleset: RuleSet, now: datetime | None = None) -> str:
    """Gmail's filter export format; import it under Settings → Filters → Import filters.

    Sweep-only rules (``older_than``) are left out, since a filter cannot act on age.
    """
    stamp = (now or datetime.now(UTC)).strftime("%Y-%m-%dT%H:%M:%SZ")
    feed = Element("feed", {"xmlns": _ATOM, "xmlns:apps": _APPS})
    SubElement(feed, "title").text = "Mail Filters"
    SubElement(feed, "id").text = "tag:mail.google.com,2008:filters:"
    SubElement(feed, "updated").text = stamp
    for index, rule in enumerate(r for r in ruleset.rules if r.exportable):
        entry = SubElement(feed, "entry")
        SubElement(entry, "category", {"term": "filter"})
        SubElement(entry, "title").text = "Mail Filter"
        SubElement(entry, "id").text = f"tag:mail.google.com,2008:filter:{index}"
        SubElement(entry, "updated").text = stamp
        SubElement(entry, "content").text = rule.name
        for prop in filter_properties(rule):
            SubElement(entry, "apps:property", {"name": prop.name, "value": prop.value})
    indent(feed)
    return "<?xml version='1.0' encoding='UTF-8'?>\n" + tostring(feed, encoding="unicode") + "\n"


@dataclass(frozen=True, slots=True)
class SweepStep:
    """One connector pass: search ``query``, then apply the actions to every thread found."""

    rule: str
    query: str
    label: str | None
    archive: bool
    mark_read: bool
    star: bool
    trash: bool
    important: bool | None
    filter_covers_new_mail: bool


def sweep_plan(ruleset: RuleSet, only: str | None = None) -> list[SweepStep]:
    """The sweep for every rule, or just the one named ``only``."""
    return [
        SweepStep(
            rule=rule.name,
            query=sweep_query(rule),
            label=rule.actions.label,
            archive=rule.actions.archive,
            mark_read=rule.actions.mark_read,
            star=rule.actions.star,
            trash=rule.actions.trash,
            important=rule.actions.important,
            filter_covers_new_mail=rule.exportable,
        )
        for rule in ruleset.rules
        if only is None or rule.name == only
    ]


def sweep_json(steps: list[SweepStep]) -> str:
    """The plan as indented JSON for the skill to read."""
    return TypeAdapter(list[SweepStep]).dump_json(steps, indent=2).decode()


@dataclass(frozen=True, slots=True)
class ApiFilter:
    """A rule as a Gmail API filter (``users.settings.filters``), labels still by name.

    ``add``/``remove`` hold system label IDs (INBOX, UNREAD, STARRED, IMPORTANT, TRASH);
    ``label`` is the user label to resolve to an ID at create time.
    """

    rule: str
    sender: str
    to: str
    subject: str
    query: str
    negated_query: str
    label: str | None
    add: tuple[str, ...]
    remove: tuple[str, ...]


def api_filters(ruleset: RuleSet) -> list[ApiFilter]:
    """Every exportable rule as a Gmail API filter, matching ``filters_xml`` field for field."""
    out: list[ApiFilter] = []
    for rule in (r for r in ruleset.rules if r.exportable):
        match, acts = rule.match, rule.actions
        add = [name for name, on in (("STARRED", acts.star), ("IMPORTANT", acts.important is True)) if on]
        add += ["TRASH"] if acts.trash else []
        remove = [name for name, on in (("INBOX", acts.archive), ("UNREAD", acts.mark_read)) if on]
        remove += ["IMPORTANT"] if acts.important is False else []
        out.append(
            ApiFilter(
                rule=rule.name,
                sender=_joined(match.senders),
                to=_joined(match.to),
                subject=_joined(match.subject),
                query=match.query,
                negated_query=match.exclude,
                label=acts.label,
                add=tuple(add),
                remove=tuple(remove),
            )
        )
    return out


def api_filters_json(filters: list[ApiFilter]) -> str:
    """The filters as indented JSON."""
    return TypeAdapter(list[ApiFilter]).dump_json(filters, indent=2).decode()

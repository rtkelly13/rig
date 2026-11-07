"""The Gmail rules compiler: validation, queries, filter XML, sweep plans and coverage."""

from __future__ import annotations

import json
from datetime import UTC, datetime
from pathlib import Path
from xml.etree.ElementTree import fromstring

import pytest
from typer.testing import CliRunner

from rig.mail.apps_script import apps_script
from rig.mail.cli import app
from rig.mail.coverage import SampleMessage, coverage, read_sample
from rig.mail.export import api_filters, filter_properties, filters_xml, sweep_plan
from rig.mail.rules import RulesError, match_query, parse_rules, sweep_query

pytestmark = pytest.mark.ci

RULES = """
rules:
  - name: meeting-notes
    match: {from: [notes@meetings.example]}
    actions: {label: Meeting Notes, archive: true}
  - name: dev-failures
    match:
      from: [ci@build.example]
      subject: [Failed, with issues]
    actions: {label: Dev Alerts, star: true, important: true}
  - name: dev-noise
    match:
      from: [ci@build.example]
      exclude: subject:failed
    actions: {label: Dev Alerts, archive: true, mark_read: true}
  - name: stale-alerts
    match: {query: "label:Alerts"}
    older_than: 7d
    actions: {archive: true}
"""


def _ruleset():
    return parse_rules(RULES)


def test_match_query_ors_within_a_field_and_quotes_phrases():
    rule = _ruleset().rules[1]
    assert match_query(rule.match) == 'from:ci@build.example subject:(Failed OR "with issues")'


def test_exclude_renders_like_gmails_doesnt_have_field():
    assert match_query(_ruleset().rules[2].match).endswith("-{subject:failed}")


def test_sweep_query_scopes_inbox_clearing_rules_to_the_inbox():
    assert sweep_query(_ruleset().rules[0]) == "from:notes@meetings.example in:inbox"


def test_sweep_query_for_in_place_rule_finds_mail_missing_any_action():
    # Labelled but unstarred mail is still work: adding `star` to a rule must catch it up.
    assert sweep_query(_ruleset().rules[1]).endswith("{-label:dev-alerts -is:starred -is:important}")


def test_sweep_plan_carries_importance():
    step = sweep_plan(_ruleset(), only=_ruleset().rules[1].name)[0]
    assert step.important is True


def test_sweep_query_carries_age():
    assert sweep_query(_ruleset().rules[3]) == "(label:Alerts) in:inbox older_than:7d"


@pytest.mark.parametrize(
    ("text", "message"),
    [
        ("rules: [{name: a, match: {}, actions: {archive: true}}]", "matches everything"),
        ("rules: [{name: a, match: {from: [x]}, actions: {}}]", "no actions"),
        (
            "rules: [{name: a, match: {from: [x]}, actions: {archive: true}},"
            " {name: a, match: {from: [y]}, actions: {archive: true}}]",
            "defined twice",
        ),
        ("rules: [{name: a, match: {from: [x]}, actions: {archive: true}, older_than: week}]", "older_than"),
        ("rules: [{name: a, match: {form: [x]}, actions: {archive: true}}]", "form"),
        ("rules: [", "not valid YAML"),
    ],
)
def test_bad_rules_are_rejected_with_a_reason(text: str, message: str):
    with pytest.raises(RulesError, match=message):
        _ = parse_rules(text)


def test_filter_properties_cover_actions():
    names = {p.name: p.value for p in filter_properties(_ruleset().rules[1])}
    assert names == {
        "from": "ci@build.example",
        "subject": 'Failed OR "with issues"',
        "label": "Dev Alerts",
        "shouldStar": "true",
        "shouldAlwaysMarkAsImportant": "true",
    }


def test_filters_xml_is_gmail_import_shaped_and_skips_sweep_only_rules():
    xml = filters_xml(_ruleset(), now=datetime(2026, 11, 4, tzinfo=UTC))
    root = fromstring(xml)  # noqa: S314 - parsing our own output
    entries = root.findall("{http://www.w3.org/2005/Atom}entry")
    assert len(entries) == 3
    first = entries[0].findall("{http://schemas.google.com/apps/2006}property")
    assert [(p.get("name"), p.get("value")) for p in first] == [
        ("from", "notes@meetings.example"),
        ("label", "Meeting Notes"),
        ("shouldArchive", "true"),
    ]


def test_sweep_plan_marks_rules_no_filter_covers():
    plan = {step.rule: step for step in sweep_plan(_ruleset())}
    assert plan["stale-alerts"].filter_covers_new_mail is False
    assert plan["meeting-notes"].filter_covers_new_mail is True
    assert [step.rule for step in sweep_plan(_ruleset(), "dev-noise")] == ["dev-noise"]


def test_coverage_counts_per_message_and_flags_partial_senders():
    sample = [
        SampleMessage("notes@meetings.example", "Notes: standup", ()),
        SampleMessage("notes@meetings.example", "Notes: retro", ()),
        SampleMessage("ci@build.example", "Nightly job - Failed", ()),
        SampleMessage("peer@work.example", "Pre-read", ()),
    ]
    rows = {row.sender: row for row in coverage(_ruleset(), sample)}
    assert rows["notes@meetings.example"].claimed == 2
    assert rows["notes@meetings.example"].rules == ("meeting-notes",)
    assert rows["ci@build.example"].rules == ("dev-failures",)
    assert rows["peer@work.example"].claimed == 0
    assert rows["peer@work.example"].unclaimed_subject == "Pre-read"


def _page(tmp_path: Path) -> Path:
    page = {
        "threads": [
            {"id": "1", "messages": [{"sender": "notes@meetings.example", "subject": "Notes: x"}]},
            {
                "id": "2",
                "messages": [{"sender": "peer@work.example", "subject": "Hi", "toRecipients": ["me@x.example"]}],
            },
            {"id": "3"},
        ]
    }
    path = tmp_path / "page.json"
    _ = path.write_text(json.dumps(page), encoding="utf-8")
    return path


def test_read_sample_takes_connector_pages_and_skips_empty_threads(tmp_path: Path):
    sample = read_sample([_page(tmp_path)])
    assert [m.sender for m in sample] == ["notes@meetings.example", "peer@work.example"]
    assert sample[1].to == ("me@x.example",)


def test_cli_end_to_end(tmp_path: Path):
    rules = tmp_path / "rules.yaml"
    _ = rules.write_text(RULES, encoding="utf-8")
    runner = CliRunner()

    assert runner.invoke(app, ["check", "--rules", str(rules)]).exit_code == 0

    out = tmp_path / "filters.xml"
    result = runner.invoke(app, ["filters", "--rules", str(rules), "-o", str(out)])
    assert result.exit_code == 0, result.output
    assert "notes@meetings.example" in out.read_text(encoding="utf-8")

    result = runner.invoke(app, ["sweep", "--rules", str(rules), "--rule", "nope"])
    assert result.exit_code == 1

    result = runner.invoke(app, ["coverage", str(_page(tmp_path)), "--rules", str(rules), "--json", "--unclaimed"])
    assert result.exit_code == 0, result.output
    assert [row["sender"] for row in json.loads(result.output)] == ["peer@work.example"]


def test_cli_reports_missing_rules_file(tmp_path: Path):
    result = CliRunner().invoke(app, ["check", "--rules", str(tmp_path / "missing.yaml")])
    assert result.exit_code == 1
    assert "no rules file" in result.output


def test_apps_script_embeds_the_sweep_plan_as_valid_json():
    script = apps_script(sweep_plan(_ruleset()), api_filters(_ruleset()))
    steps_json = script.split("const STEPS = ", 1)[1].split(";\nconst FILTERS", 1)[0]
    assert [step["rule"] for step in json.loads(steps_json)] == [
        "meeting-notes",
        "dev-failures",
        "dev-noise",
        "stale-alerts",
    ]
    for function in ("function setup()", "function triage()", "function syncFilters()"):
        assert function in script


def test_api_filters_map_actions_to_system_labels_and_skip_sweep_only_rules():
    filters = {f.rule: f for f in api_filters(_ruleset())}
    assert set(filters) == {"meeting-notes", "dev-failures", "dev-noise"}
    noise = filters["dev-noise"]
    assert (noise.sender, noise.negated_query, noise.label) == ("ci@build.example", "subject:failed", "Dev Alerts")
    assert noise.remove == ("INBOX", "UNREAD")
    assert filters["dev-failures"].add == ("STARRED", "IMPORTANT")
    assert filters["dev-failures"].subject == 'Failed OR "with issues"'


def test_apps_script_version_changes_with_the_rules():
    def version(text: str) -> str:
        ruleset = parse_rules(text)
        script = apps_script(sweep_plan(ruleset), api_filters(ruleset))
        return script.split("const VERSION = '", 1)[1][:12]

    assert version(RULES) == version(RULES)
    assert version(RULES) != version(RULES.replace("Meeting Notes", "Meetings"))

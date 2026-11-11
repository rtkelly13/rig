using Rig.Core.Mail;

namespace Rig.Core.Tests.Mail;

/// <summary>
/// The rules file, its schema and the coverage reader, ported from <c>tests/test_mail.py</c> plus the edges the oracle does
/// not reach. Every address is a placeholder: no mail data belongs in git.
/// </summary>
public sealed class MailRulesTests : IDisposable
{
    private const string Rules = """
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
        """;

    private readonly TempDir temp = new();

    private static MailRuleSet RuleSet => MailRuleSet.Parse(Rules);

    public void Dispose() => temp.Dispose();

    [Fact]
    public void Match_query_ors_within_a_field_and_quotes_phrases() =>
        Assert.Equal("from:ci@build.example subject:(Failed OR \"with issues\")", MailRuleSet.MatchQuery(RuleSet.Rules[1].Match));

    [Fact]
    public void Exclude_renders_like_gmails_doesnt_have_field() =>
        Assert.EndsWith("-{subject:failed}", MailRuleSet.MatchQuery(RuleSet.Rules[2].Match), StringComparison.Ordinal);

    [Fact]
    public void Sweep_query_scopes_inbox_clearing_rules_to_the_inbox() =>
        Assert.Equal("from:notes@meetings.example in:inbox", MailRuleSet.SweepQuery(RuleSet.Rules[0]));

    [Fact]
    public void Sweep_query_for_an_in_place_rule_finds_mail_missing_any_action() =>
        // Labelled but unstarred mail is still work: adding `star` to a rule must catch it up.
        Assert.EndsWith("{-label:dev-alerts -is:starred -is:important}", MailRuleSet.SweepQuery(RuleSet.Rules[1]), StringComparison.Ordinal);

    [Fact]
    public void Sweep_query_carries_age() =>
        Assert.Equal("(label:Alerts) in:inbox older_than:7d", MailRuleSet.SweepQuery(RuleSet.Rules[3]));

    [Fact]
    public void Sweep_query_without_unmet_actions_is_the_match_alone()
    {
        var rule = new MailRule("bare", new MailMatch(["a@x.example"], [], []), new MailActions());

        Assert.Equal("from:a@x.example", MailRuleSet.SweepQuery(rule));
    }

    [Fact]
    public void Sweep_plan_carries_importance_and_marks_rules_no_filter_covers()
    {
        var plan = MailExport.SweepPlan(RuleSet).ToDictionary(step => step.Rule);

        Assert.True(plan["dev-failures"].Important);
        Assert.False(plan["stale-alerts"].FilterCoversNewMail);
        Assert.True(plan["meeting-notes"].FilterCoversNewMail);
        Assert.Equal(["dev-noise"], MailExport.SweepPlan(RuleSet, "dev-noise").Select(step => step.Rule));
    }

    [Fact]
    public void Filter_properties_cover_actions()
    {
        var props = MailExport.FilterProperties(RuleSet.Rules[1]).ToDictionary(p => p.Name, p => p.Value);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["from"] = "ci@build.example",
                ["subject"] = "Failed OR \"with issues\"",
                ["label"] = "Dev Alerts",
                ["shouldStar"] = "true",
                ["shouldAlwaysMarkAsImportant"] = "true",
            },
            props);
    }

    [Fact]
    public void Api_filters_map_actions_to_system_labels_and_skip_sweep_only_rules()
    {
        var filters = MailExport.ApiFilters(RuleSet).ToDictionary(f => f.Rule);

        Assert.Equal(["meeting-notes", "dev-failures", "dev-noise"], filters.Keys);
        var noise = filters["dev-noise"];
        Assert.Equal(("ci@build.example", "subject:failed", "Dev Alerts"), (noise.Sender, noise.NegatedQuery, noise.Label));
        Assert.Equal(["INBOX", "UNREAD"], noise.Remove);
        Assert.Equal(["STARRED", "IMPORTANT"], filters["dev-failures"].Add);
    }

    [Fact]
    public void Apps_script_version_changes_with_the_rules()
    {
        static string Version(string text)
        {
            var ruleset = MailRuleSet.Parse(text);
            var script = AppsScript.Render(MailExport.SweepPlan(ruleset), MailExport.ApiFilters(ruleset));
            return script.Split("const VERSION = '", 2)[1][..12];
        }

        Assert.Equal(Version(Rules), Version(Rules));
        Assert.NotEqual(Version(Rules), Version(Rules.Replace("Meeting Notes", "Meetings", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("rules: [{name: a, match: {}, actions: {archive: true}}]", "rules: rule `a` matches everything; give it from, to, subject or query")]
    [InlineData("rules: [{name: a, match: {from: [x]}, actions: {}}]", "rules: rule `a` has no actions")]
    [InlineData("rules: [{name: a, match: {from: [x]}, actions: {label: ''}}]", "rules: rule `a` has no actions")]
    [InlineData("rules: [{name: a, match: {from: [x]}, actions: {archive: true}}, {name: a, match: {from: [y]}, actions: {archive: true}}]", "rules: rule `a` is defined twice")]
    [InlineData("rules: [{name: a, match: {from: [x]}, actions: {archive: true}, older_than: week}]", "rules: rule `a`: older_than must look like 3d, 2m or 1y, not 'week'")]
    [InlineData("rules: [{name: a, match: {from: [x]}, actions: {archive: true}, older_than: \"it's\"}]", "not \"it's\"")]
    [InlineData("rules: [{name: a, match: {from: [x]}, actions: {archive: true}, older_than: \"'\\\"\\t\\n\\r\\\\\\x01\\x7f\"}]", "not '\\'\"\\t\\n\\r\\\\\\x01\\x7f'")]
    [InlineData("rules: [{name: a, match: {form: [x]}, actions: {archive: true}}]", "rules: rules.0.match: unexpected key form; expected one of from, to, subject, query, exclude")]
    [InlineData("rules: [{name: a, match: {1: [x]}, actions: {archive: true}}]", "unexpected key 1;")]
    [InlineData("rules: [{name: a, match: {null: [x]}, actions: {archive: true}}]", "unexpected key None;")]
    [InlineData("rules: [{name: a, match: {[x]: y}, actions: {archive: true}}]", "not valid YAML (found unhashable key)")]
    [InlineData("rules: [{name: a, match: {!!binary aGk=: [x]}, actions: {archive: true}}]", "unexpected key an unsupported value;")]
    [InlineData("rules: [{match: {from: [x]}, actions: {archive: true}}]", "rules: rules.0.name: missing")]
    [InlineData("rules: [{name: 5, match: {from: [x]}, actions: {archive: true}}]", "rules: rules.0.name: expected a string")]
    [InlineData("rules: [{name: a, match: {from: x}, actions: {archive: true}}]", "rules: rules.0.match.from: expected a list")]
    [InlineData("rules: [{name: a, match: {from: [true]}, actions: {archive: true}}]", "rules: rules.0.match.from.0: expected a string")]
    [InlineData("rules: [{name: a, match: [x], actions: {archive: true}}]", "rules: rules.0.match: expected a mapping")]
    [InlineData("rules: [{name: a, match: {from: [x]}, actions: {archive: null}}]", "rules: rules.0.actions.archive: expected a boolean")]
    [InlineData("rules: [{name: a, match: {from: [x]}, actions: {important: 'yes'}}]", "rules: rules.0.actions.important: expected a boolean")]
    [InlineData("rules: [{name: a, match: {from: [x]}, actions: {archive: 1}}]", "rules: rules.0.actions.archive: expected a boolean")]
    [InlineData("rules: {}", "rules: rules: expected a list")]
    [InlineData("", "rules: RuleSet: expected a mapping")]
    [InlineData("rules: [", "rules: not valid YAML (")]
    public void Bad_rules_are_rejected_with_a_reason(string text, string message)
    {
        var error = Assert.Throws<RulesException>(() => MailRuleSet.Parse(text));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Optional_fields_take_null_and_defaults()
    {
        var ruleset = MailRuleSet.Parse("""
            rules:
              - name: a
                match: {to: [x@y.example], query: "has:attachment"}
                actions: {label: null, important: false, trash: true}
                older_than: null
                note: kept
              - name: b
                match: {subject: [s]}
                actions: {star: true}
                older_than: "3d\n"
            """);

        var a = ruleset.Rules[0];
        Assert.Equal((null, false, true, null, "kept"), (a.Actions.Label, a.Actions.Important, a.Actions.Trash, a.OlderThan, a.Note));
        Assert.True(a.Actions.LeavesInbox);
        Assert.Equal("3d\n", ruleset.Rules[1].OlderThan);
        Assert.Empty(MailRuleSet.Parse("{}").Rules);
    }

    [Fact]
    public void Default_path_prefers_the_environment_and_expands_home()
    {
        string Resolve(string? value) => MailRuleSet.DefaultPath(name => name == MailRuleSet.RulesVariable ? value : null, "/home", "/code/personal");

        Assert.Equal(Path.Join("/code/personal", "rig", "temp", "mail", "rules.yaml"), Resolve(null));
        Assert.Equal(Path.Join("/code/personal", "rig", "temp", "mail", "rules.yaml"), Resolve(string.Empty));
        Assert.Equal("/home", Resolve("~"));
        Assert.Equal(Path.Join("/home", "mail/rules.yaml"), Resolve("~/mail/rules.yaml"));
        Assert.Equal("/etc/rules.yaml", Resolve("/etc/rules.yaml"));
    }

    [Fact]
    public void Load_names_the_file_in_its_errors()
    {
        var missing = Path.Join(temp.Path, "missing.yaml");
        var bad = temp.File("bad.yaml");
        File.WriteAllText(bad, "rules: [{name: a, match: {}, actions: {archive: true}}]");
        var good = temp.File("good.yaml");
        File.WriteAllText(good, Rules);

        Assert.Equal(
            $"no rules file at {missing.Replace('\\', '/')} (set --rules or $MAIL_RULES)",
            Assert.Throws<RulesException>(() => MailRuleSet.Load(missing)).Message);
        Assert.StartsWith($"{bad.Replace('\\', '/')}: rule `a`", Assert.Throws<RulesException>(() => MailRuleSet.Load(bad)).Message, StringComparison.Ordinal);
        Assert.Equal(4, MailRuleSet.Load(good).Rules.Count);
    }

    [Fact]
    public void Coverage_counts_per_message_and_flags_partial_senders()
    {
        SampleMessage[] sample =
        [
            new("notes@meetings.example", "Notes: standup", []),
            new("notes@meetings.example", "Notes: retro", []),
            new("ci@build.example", "Nightly job - Failed", []),
            new("peer@work.example", "Pre-read", []),
        ];

        var rows = MailCoverage.Coverage(RuleSet, sample).ToDictionary(row => row.Sender);

        Assert.Equal(2, rows["notes@meetings.example"].Claimed);
        Assert.Equal(["meeting-notes"], rows["notes@meetings.example"].Rules);
        Assert.Equal(["dev-failures"], rows["ci@build.example"].Rules);
        Assert.Equal(0, rows["peer@work.example"].Claimed);
        Assert.Equal("Pre-read", rows["peer@work.example"].UnclaimedSubject);
        Assert.False(rows["peer@work.example"].FullyClaimed);
    }

    [Fact]
    public void Claims_needs_from_to_or_subject_and_matches_recipients()
    {
        var message = new SampleMessage("a@x.example", "Hello", ["Team@Y.example"]);

        Assert.False(MailCoverage.Claims(new MailMatch([], [], [], Query: "anything"), message));
        Assert.True(MailCoverage.Claims(new MailMatch([], ["\"team@y.example\""], []), message));
        Assert.False(MailCoverage.Claims(new MailMatch([], ["other@y.example"], []), message));
        Assert.False(MailCoverage.Claims(new MailMatch([], [], ["bye"]), message));
    }

    [Fact]
    public void Read_sample_takes_connector_pages_leniently()
    {
        var page = temp.File("page.json");
        File.WriteAllText(page, """
            {"threads": [
              {"id": "1", "messages": [{"sender": "notes@meetings.example", "subject": "Notes: x"}]},
              {"id": "2", "messages": [{"sender": "peer@work.example", "subject": "Hi", "toRecipients": ["me@x.example"]}]},
              {"id": "3"},
              {"id": "4", "messages": "none"},
              {"id": "5", "messages": [{"sender": "x", "sender": "last@x.example", "toRecipients": ["a", 1]}]}
            ]}
            """);
        var noThreads = temp.File("empty.json");
        File.WriteAllText(noThreads, """{"threads": [1, 2]}""");

        var sample = MailCoverage.ReadSample([page, noThreads]);

        Assert.Equal(["notes@meetings.example", "peer@work.example", "last@x.example"], sample.Select(m => m.Sender));
        Assert.Equal(["me@x.example"], sample[1].To);
        Assert.Empty(sample[2].To);
        Assert.Equal(string.Empty, sample[2].Subject);
    }

    [Theory]
    [InlineData("{", "not JSON (")]
    [InlineData("[]", "not a search_threads page")]
    public void Read_sample_refuses_what_is_not_a_page(string text, string message)
    {
        var page = temp.File("page.json");
        File.WriteAllText(page, text);

        var error = Assert.Throws<InvalidDataException>(() => MailCoverage.ReadSample([page]));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rules_exception_keeps_its_message_and_cause()
    {
        var cause = new InvalidOperationException("inner");

        Assert.Equal("m", new RulesException("m").Message);
        Assert.Same(cause, new RulesException("m", cause).InnerException);
        Assert.NotNull(new RulesException().Message);
    }
}

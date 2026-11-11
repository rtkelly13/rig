using System.CommandLine;
using System.Text.Json;
using Rig.Cli.Mail;
using Rig.Core.Tests;

namespace Rig.Cli.Tests.Mail;

/// <summary>
/// <c>rig mail</c> through its command line, against a placeholder rules file in a temp folder. The rules are always named
/// by <c>--rules</c> or <c>MAIL_RULES</c>, or found under the temp folder standing in for <c>personal</c>, so the real
/// rules under rig's <c>temp/mail/</c> are never read.
/// </summary>
public sealed class MailCommandTests : IDisposable
{
    private const string Rules = """
        rules:
          - name: notes
            match: {from: [notes@meetings.example]}
            actions: {label: Meeting Notes, archive: true, mark_read: true, star: true, important: true}
          - name: stale
            match: {query: "label:Alerts"}
            older_than: 7d
            actions: {trash: true, important: false}
          - name: quiet
            match: {to: [quiet@x.example]}
            actions: {mark_read: true}
        """;

    private const string Page = """
        {"threads": [
          {"id": "1", "messages": [{"sender": "notes@meetings.example", "subject": "Notes: x"}]},
          {"id": "2", "messages": [{"sender": "peer@work.example", "subject": "Hi"}]},
          {"id": "3", "messages": [{"sender": "peer@work.example", "subject": "Again"}]},
          {"id": "4", "messages": [{"sender": "p@q.example", "subject": "s", "toRecipients": ["Quiet@X.example"]}]}
        ]}
        """;

    private readonly TempDir _temp = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);
    private readonly string _rules;

    public MailCommandTests()
    {
        _rules = _temp.File("rules.yaml");
        File.WriteAllText(_rules, Rules);
        _environment["MAIL_RULES"] = _rules;
    }

    private string Output => _out.ToString().ReplaceLineEndings("\n");

    private string Error => _err.ToString().ReplaceLineEndings("\n");

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task Check_shows_each_rule_with_its_query_actions_and_filter()
    {
        Assert.Equal(0, await Run("mail", "check"));

        Assert.Contains("3 rules", Output, StringComparison.Ordinal);
        Assert.Contains("from:notes@meetings.example", Output, StringComparison.Ordinal);
        Assert.Contains("label:Meeting Notes, archive, read, star, important", Output, StringComparison.Ordinal);
        Assert.Contains("trash, not-important", Output, StringComparison.Ordinal);
        Assert.Contains("sweep only (older_than:7d)", Output, StringComparison.Ordinal);
        Assert.Contains("yes", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_lists_a_rule_with_no_actions_as_empty()
    {
        Write(_rules, "rules:\n  - name: bare\n    match: {to: [a@x.example]}\n    actions: {label: L}\n");

        Assert.Equal(0, await Run("mail", "check", "--rules", _rules));

        Assert.Contains("label:L", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filters_writes_the_xml_to_stdout_stamped_by_the_clock()
    {
        Assert.Equal(0, await Run("mail", "filters"));

        Assert.StartsWith("<?xml version='1.0' encoding='UTF-8'?>\n", Output, StringComparison.Ordinal);
        Assert.Contains("<updated>2023-11-14T22:15:23Z</updated>", Output, StringComparison.Ordinal);
        Assert.Contains("<content>notes</content>", Output, StringComparison.Ordinal);
        Assert.DoesNotContain("<content>stale</content>", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filters_writes_the_xml_to_a_file_and_says_where()
    {
        var target = Path.Join(_temp.Path, "filters.xml");

        Assert.Equal(0, await Run("mail", "filters", "-o", target));

        Assert.Equal($"wrote {target.Replace('\\', '/')}\n", Output);
        Assert.Contains("<content>quiet</content>", Read(target), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sweep_prints_every_rule()
    {
        Assert.Equal(0, await Run("mail", "sweep"));

        using var plan = JsonDocument.Parse(Output);
        Assert.Equal(["notes", "stale", "quiet"], plan.RootElement.EnumerateArray().Select(step => step.GetProperty("rule").GetString()));
    }

    [Fact]
    public async Task Sweep_prints_just_the_named_rule()
    {
        Assert.Equal(0, await Run("mail", "sweep", "--rule", "stale"));

        using var plan = JsonDocument.Parse(Output);
        var step = Assert.Single(plan.RootElement.EnumerateArray());
        Assert.Equal("(label:Alerts) in:inbox older_than:7d", step.GetProperty("query").GetString());
        Assert.False(step.GetProperty("filter_covers_new_mail").GetBoolean());
    }

    [Fact]
    public async Task Sweep_refuses_an_unknown_rule()
    {
        Assert.Equal(MailCommand.FailedExit, await Run("mail", "sweep", "--rule", "nope"));

        Assert.Equal("error: no rule named 'nope'\n", Error);
        Assert.Empty(Output);
    }

    [Fact]
    public async Task Apps_script_goes_to_stdout_or_a_file()
    {
        Assert.Equal(0, await Run("mail", "apps-script"));
        Assert.Contains("\"rule\": \"quiet\"", Output, StringComparison.Ordinal);

        var target = Path.Join(_temp.Path, "sweep.gs");
        Assert.Equal(0, await Run("mail", "apps-script", "-o", target));
        Assert.Contains("\"rule\": \"notes\"", Read(target), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coverage_shows_which_senders_the_rules_claim()
    {
        Assert.Equal(0, await Run("mail", "coverage", PageFile()));

        Assert.Contains("2/4 sampled threads claimed", Output, StringComparison.Ordinal);
        Assert.Contains("0/2", Output, StringComparison.Ordinal);
        Assert.Contains("Hi", Output, StringComparison.Ordinal);
        Assert.Contains("quiet", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coverage_flags_a_claim_resting_on_a_query_as_approximate()
    {
        Write(_rules, "rules:\n  - name: peers\n    match: {from: [peer@work.example], query: has:attachment}\n    actions: {archive: true}\n");

        Assert.Equal(0, await Run("mail", "coverage", PageFile()));

        Assert.Contains("peers ~", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coverage_json_lists_only_the_unclaimed_senders_when_asked()
    {
        Assert.Equal(0, await Run("mail", "coverage", PageFile(), "--json", "--unclaimed"));

        using var rows = JsonDocument.Parse(Output);
        var row = Assert.Single(rows.RootElement.EnumerateArray());
        Assert.Equal("peer@work.example", row.GetProperty("sender").GetString());
        Assert.Equal("Hi", row.GetProperty("unclaimed_subject").GetString());
    }

    [Fact]
    public async Task Coverage_refuses_a_page_that_is_not_json()
    {
        var page = _temp.File("bad.json");
        Write(page, "not json");

        Assert.Equal(MailCommand.FailedExit, await Run("mail", "coverage", page));

        Assert.StartsWith($"error: {page.Replace('\\', '/')}: not JSON (", Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_rules_file_is_an_error()
    {
        var missing = Path.Join(_temp.Path, "missing.yaml");

        Assert.Equal(MailCommand.FailedExit, await Run("mail", "check", "--rules", missing));

        Assert.Equal($"error: no rules file at {missing.Replace('\\', '/')} (set --rules or $MAIL_RULES)\n", Error);
    }

    [Fact]
    public async Task Without_mail_rules_the_rules_come_from_rigs_temp_folder()
    {
        _environment.Clear();
        var rules = Path.Join(_temp.Folder(Path.Join("personal", "rig", "temp", "mail")), "rules.yaml");
        Write(rules, Rules);

        Assert.Equal(0, await Run("mail", "check"));

        Assert.Contains("3 rules", Output, StringComparison.Ordinal);
    }

    private string PageFile()
    {
        var page = _temp.File("page.json");
        Write(page, Page);
        return page;
    }

    private static void Write(string path, string text) => File.WriteAllText(path, text);

    private static string Read(string path) => File.ReadAllText(path);

    private Task<int> Run(params string[] args)
    {
        var host = new MailHost(v => _environment.GetValueOrDefault(v), _temp.Path, Path.Join(_temp.Path, "personal"), new FixedClock());
        var command = new MailCommand(host, _out, _err);
        return command.Build().Parse(args).InvokeAsync(new InvocationConfiguration { Output = _out, Error = _err }, TestContext.Current.CancellationToken);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1700000123);
    }
}

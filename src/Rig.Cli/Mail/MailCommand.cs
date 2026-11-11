using System.CommandLine;
using System.Globalization;
using System.Text;
using Rig.Cli.Vault;
using Rig.Core.Mail;

namespace Rig.Cli.Mail;

/// <summary>What <c>rig mail</c> needs from the machine.</summary>
/// <param name="Environment">Reads an environment variable, or returns null.</param>
/// <param name="Home">The user's home folder.</param>
/// <param name="Personal">The code root's <c>personal</c> folder, which holds the main rig checkout.</param>
/// <param name="Clock">The clock that stamps exported filters.</param>
internal sealed record MailHost(Func<string, string?> Environment, string Home, string Personal, TimeProvider Clock);

/// <summary>
/// <c>rig mail</c>: Gmail triage rules as code. Ported from <c>rig.mail.cli</c>. Nothing here calls Gmail: filters are
/// imported by hand and sweeps run through the Gmail connector or the exported Apps Script.
/// </summary>
internal sealed class MailCommand(MailHost host, TextWriter output, TextWriter error)
{
    /// <summary>A rules file that cannot be loaded, an unknown <c>--rule</c> or an unreadable sample exits with this.</summary>
    public const int FailedExit = 1;

    /// <summary>The <c>mail</c> command.</summary>
    public Command Build()
    {
        var check = new Command("check", "Validate the rules file and show each rule's Gmail query.");
        var checkRules = RulesOption();
        check.Add(checkRules);
        check.SetAction(parsed => WithRules(parsed.GetValue(checkRules), Check));

        var filters = new Command("filters", "Export rules as Gmail filter XML (Settings → Filters → Import filters).");
        var filtersRules = RulesOption();
        var filtersOut = OutputOption("Write the XML here instead of stdout.");
        filters.Add(filtersRules);
        filters.Add(filtersOut);
        filters.SetAction(parsed => WithRules(parsed.GetValue(filtersRules), ruleset =>
            Emit(MailExport.FiltersXml(ruleset, host.Clock.GetUtcNow()), parsed.GetValue(filtersOut))));

        var sweep = new Command("sweep", "Print the sweep plan as JSON: per rule, the query to search and the actions to apply.");
        var sweepRules = RulesOption();
        var only = new Option<string?>("--rule") { Description = "Only this rule." };
        sweep.Add(sweepRules);
        sweep.Add(only);
        sweep.SetAction(parsed => WithRules(parsed.GetValue(sweepRules), ruleset => Sweep(ruleset, parsed.GetValue(only))));

        var script = new Command(
            "apps-script",
            "Export a Google Apps Script that sweeps in bulk and creates the filters (no Cloud project needed). The script embeds real queries: write it under temp/, never into a tracked file.");
        var scriptRules = RulesOption();
        var scriptOut = OutputOption("Write here, not stdout.");
        script.Add(scriptRules);
        script.Add(scriptOut);
        script.SetAction(parsed => WithRules(parsed.GetValue(scriptRules), ruleset =>
            Emit(AppsScript.Render(MailExport.SweepPlan(ruleset), MailExport.ApiFilters(ruleset)), parsed.GetValue(scriptOut))));

        return new Command(
            "mail",
            "Gmail triage rules as code. Nothing here calls Gmail: filters are imported by hand and sweeps run through the Gmail connector (see the gmail-triage skill in the vault).")
        {
            check,
            filters,
            sweep,
            script,
            CoverageCommand(),
        };
    }

    private static Option<FileInfo?> RulesOption() => new("--rules")
    {
        Description = "Rules YAML. Defaults to $MAIL_RULES, else ~/code/personal/rig/temp/mail/rules.yaml.",
    };

    private static Option<FileInfo?> OutputOption(string description) => new("--output", "-o") { Description = description };

    private static string Posix(string path) => path.Replace('\\', '/');

    private Command CoverageCommand()
    {
        var samples = new Argument<FileInfo[]>("samples") { Description = "Saved search_threads JSON pages.", Arity = ArgumentArity.OneOrMore }
            .AcceptExistingOnly();
        var rules = RulesOption();
        var json = new Option<bool>("--json") { Description = "JSON output." };
        var unclaimed = new Option<bool>("--unclaimed") { Description = "Only senders rules miss." };
        var coverage = new Command("coverage", "Show which senders in a saved inbox sample the rules claim, busiest first.") { samples, rules, json, unclaimed };
        coverage.SetAction(parsed => WithRules(parsed.GetValue(rules), ruleset =>
        {
            IReadOnlyList<SampleMessage> sample;
            try
            {
                sample = MailCoverage.ReadSample(parsed.GetRequiredValue(samples).Select(file => file.FullName));
            }
            catch (InvalidDataException e)
            {
                error.WriteLine($"error: {e.Message}");
                return FailedExit;
            }

            var rows = MailCoverage.Coverage(ruleset, sample).Where(row => !parsed.GetValue(unclaimed) || !row.FullyClaimed).ToList();
            if (parsed.GetValue(json))
            {
                output.WriteLine(MailCoverage.CoverageJson(rows));
                return 0;
            }

            var table = new TextTable(
                $"{rows.Sum(row => row.Claimed)}/{rows.Sum(row => row.Threads)} sampled threads claimed",
                [("Threads", true), ("Sender", false), ("Rule", false), ("Unclaimed example", false)]);
            foreach (var row in rows)
            {
                var label = string.Join(", ", row.Rules) + (row.Approximate ? " ~" : string.Empty);
                if (!row.FullyClaimed)
                {
                    label = $"{row.Claimed}/{row.Threads} {label}".TrimEnd();
                }

                table.Add(row.Threads.ToString(CultureInfo.InvariantCulture), row.Sender, label, row.UnclaimedSubject ?? string.Empty);
            }

            output.Write(table.Render());
            return 0;
        }));
        return coverage;
    }

    private int WithRules(FileInfo? rules, Func<MailRuleSet, int> run)
    {
        MailRuleSet ruleset;
        try
        {
            ruleset = MailRuleSet.Load(rules?.FullName ?? MailRuleSet.DefaultPath(host.Environment, host.Home, host.Personal));
        }
        catch (RulesException e)
        {
            error.WriteLine($"error: {e.Message}");
            return FailedExit;
        }

        return run(ruleset);
    }

    private int Check(MailRuleSet ruleset)
    {
        var table = new TextTable($"{ruleset.Rules.Count} rules", [("Rule", false), ("Query", false), ("Actions", false), ("Filter", false)]);
        foreach (var rule in ruleset.Rules)
        {
            var acts = rule.Actions;
            var names = new List<string>();
            if (!string.IsNullOrEmpty(acts.Label))
            {
                names.Add($"label:{acts.Label}");
            }

            names.AddRange(new[] { ("archive", acts.Archive), ("read", acts.MarkRead), ("star", acts.Star), ("trash", acts.Trash) }
                .Where(flag => flag.Item2)
                .Select(flag => flag.Item1));
            if (acts.Important is { } important)
            {
                names.Add(important ? "important" : "not-important");
            }

            table.Add(rule.Name, MailRuleSet.MatchQuery(rule.Match), string.Join(", ", names), rule.Exportable ? "yes" : $"sweep only (older_than:{rule.OlderThan})");
        }

        output.Write(table.Render());
        return 0;
    }

    private int Sweep(MailRuleSet ruleset, string? only)
    {
        var steps = MailExport.SweepPlan(ruleset, only);
        if (only is not null && steps.Count == 0)
        {
            error.WriteLine($"error: no rule named '{only}'");
            return FailedExit;
        }

        output.WriteLine(MailExport.SweepJson(steps));
        return 0;
    }

    // To stdout as it is, or to the file, saying where it went.
    private int Emit(string text, FileInfo? file)
    {
        if (file is null)
        {
            output.Write(text);
            return 0;
        }

        File.WriteAllText(file.FullName, text, new UTF8Encoding(false));
        output.WriteLine($"wrote {Posix(file.ToString())}");
        return 0;
    }
}

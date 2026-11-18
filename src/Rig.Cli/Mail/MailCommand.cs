using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using DotMake.CommandLine;
using Rig.Cli.Rendering;
using Rig.Core;
using Rig.Core.Mail;

namespace Rig.Cli.Mail;

/// <summary>What <c>rig mail</c> needs from the machine.</summary>
/// <param name="Environment">Reads an environment variable, or returns null.</param>
/// <param name="Home">The user's home folder.</param>
/// <param name="Personal">The code root's <c>personal</c> folder, which holds the main rig checkout.</param>
/// <param name="Clock">The clock that stamps exported filters.</param>
internal sealed record MailHost(Func<string, string?> Environment, string Home, string Personal, TimeProvider Clock);

/// <summary>
/// <c>rig mail</c>: Gmail triage rules as code. Nothing here calls Gmail: filters are
/// imported by hand and sweeps run through the Gmail connector or the exported Apps Script.
/// </summary>
/// <param name="host">The machine's rules file, home and clock.</param>
/// <param name="output">Where the reports go.</param>
/// <param name="error">Where the errors go.</param>
[CliCommand(
    Name = "mail",
    Description = "Gmail triage rules as code. Nothing here calls Gmail: filters are imported by hand and sweeps run through the Gmail connector (see the gmail-triage skill in the vault).")]
internal sealed class MailCommand(MailHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal MailCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>A rules file that cannot be loaded, an unknown <c>--rule</c> or an unreadable sample exits with this.</summary>
    public const int FailedExit = 1;

    /// <summary>The <c>mail</c> command.</summary>
    public Command Build()
    {
        var mail = DeclarativeCommand.Build(this);
        mail.Add(DeclarativeCommand.Build(new CheckCommand(host, output, error)));
        mail.Add(DeclarativeCommand.Build(new FiltersCommand(host, output, error)));
        mail.Add(DeclarativeCommand.Build(new SweepCommand(host, output, error)));
        mail.Add(DeclarativeCommand.Build(new AppsScriptCommand(host, output, error)));
        mail.Add(DeclarativeCommand.Build(new CoverageCommand(host, output, error)));
        return mail;
    }

    /// <summary>A group named without one of its verbs is the parser's error.</summary>
    public int Run() => DeclarativeCommand.RequireSubcommand(error);

    private static string Posix(string path) => path.Replace('\\', '/');

    // Loads the named rules, or the default one, and says why when it cannot: a missing or broken file is exit 1.
    private static int WithRules(MailHost host, TextWriter error, FileInfo? rules, Func<MailRuleSet, int> run)
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

    // To stdout as it is, or to the file, saying where it went.
    private static int Emit(TextWriter output, string text, FileInfo? file)
    {
        if (file is null)
        {
            output.Write(text);
            return (int)ExitCode.Clean;
        }

        File.WriteAllText(file.FullName, text, new UTF8Encoding(false));
        output.WriteLine($"wrote {Posix(file.ToString())}");
        return (int)ExitCode.Clean;
    }

    /// <summary><c>rig mail check</c>: each rule's query, actions and filter.</summary>
    [CliCommand(Name = "check", Description = "Validate the rules file and show each rule's Gmail query.")]
    internal sealed class CheckCommand(MailHost host, TextWriter output, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal CheckCommand()
            : this(null!, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>Rules YAML. Defaults to $MAIL_RULES, else ~/code/personal/rig/temp/mail/rules.yaml.</summary>
        [CliOption(Name = "--rules", Required = false, Description = "Rules YAML. Defaults to $MAIL_RULES, else ~/code/personal/rig/temp/mail/rules.yaml.")]
        public FileInfo? Rules { get; set; }

        /// <summary>The table: one row per rule.</summary>
        public int Run() => WithRules(host, error, Rules, ruleset =>
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
            return (int)ExitCode.Clean;
        });
    }

    /// <summary><c>rig mail filters</c>: the filter XML Gmail imports.</summary>
    [CliCommand(Name = "filters", Description = "Export rules as Gmail filter XML (Settings → Filters → Import filters).")]
    internal sealed class FiltersCommand(MailHost host, TextWriter output, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal FiltersCommand()
            : this(null!, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>Rules YAML. Defaults to $MAIL_RULES, else ~/code/personal/rig/temp/mail/rules.yaml.</summary>
        [CliOption(Name = "--rules", Required = false, Description = "Rules YAML. Defaults to $MAIL_RULES, else ~/code/personal/rig/temp/mail/rules.yaml.")]
        public FileInfo? Rules { get; set; }

        /// <summary>Write the XML here instead of stdout.</summary>
        [CliOption(Name = "--output", Aliases = ["-o"], Required = false, Description = "Write the XML here instead of stdout.")]
        public FileInfo? Target { get; set; }

        /// <summary>The XML, stamped by the clock.</summary>
        public int Run() => WithRules(host, error, Rules, ruleset =>
            Emit(output, MailExport.FiltersXml(ruleset, host.Clock.GetUtcNow()), Target));
    }

    /// <summary><c>rig mail sweep</c>: the plan as JSON.</summary>
    [CliCommand(Name = "sweep", Description = "Print the sweep plan as JSON: per rule, the query to search and the actions to apply.")]
    internal sealed class SweepCommand(MailHost host, TextWriter output, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal SweepCommand()
            : this(null!, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>Rules YAML. Defaults to $MAIL_RULES, else ~/code/personal/rig/temp/mail/rules.yaml.</summary>
        [CliOption(Name = "--rules", Required = false, Description = "Rules YAML. Defaults to $MAIL_RULES, else ~/code/personal/rig/temp/mail/rules.yaml.")]
        public FileInfo? Rules { get; set; }

        /// <summary>Only this rule.</summary>
        [CliOption(Name = "--rule", Required = false, Description = "Only this rule.")]
        public string? Only { get; set; }

        /// <summary>The plan for every rule, or for <see cref="Only"/>.</summary>
        public int Run() => WithRules(host, error, Rules, ruleset =>
        {
            var steps = MailExport.SweepPlan(ruleset, Only);
            if (Only is not null && steps.Count == 0)
            {
                error.WriteLine($"error: no rule named '{Only}'");
                return FailedExit;
            }

            output.WriteLine(MailExport.SweepJson(steps));
            return (int)ExitCode.Clean;
        });
    }

    /// <summary><c>rig mail apps-script</c>: the bulk sweeper to paste into Apps Script.</summary>
    [CliCommand(
        Name = "apps-script",
        Description = "Export a Google Apps Script that sweeps in bulk and creates the filters (no Cloud project needed). The script embeds real queries: write it under temp/, never into a tracked file.")]
    internal sealed class AppsScriptCommand(MailHost host, TextWriter output, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal AppsScriptCommand()
            : this(null!, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>Rules YAML. Defaults to $MAIL_RULES, else ~/code/personal/rig/temp/mail/rules.yaml.</summary>
        [CliOption(Name = "--rules", Required = false, Description = "Rules YAML. Defaults to $MAIL_RULES, else ~/code/personal/rig/temp/mail/rules.yaml.")]
        public FileInfo? Rules { get; set; }

        /// <summary>Write here, not stdout.</summary>
        [CliOption(Name = "--output", Aliases = ["-o"], Required = false, Description = "Write here, not stdout.")]
        public FileInfo? Target { get; set; }

        /// <summary>The script, which embeds the real queries.</summary>
        public int Run() => WithRules(host, error, Rules, ruleset =>
            Emit(output, AppsScript.Render(MailExport.SweepPlan(ruleset), MailExport.ApiFilters(ruleset)), Target));
    }

    /// <summary><c>rig mail coverage</c>: which senders the rules claim.</summary>
    [CliCommand(Name = "coverage", Description = "Show which senders in a saved inbox sample the rules claim, busiest first.")]
    internal sealed class CoverageCommand(MailHost host, TextWriter output, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal CoverageCommand()
            : this(null!, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>Saved search_threads JSON pages.</summary>
        [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "DotMake binds a repeated CLI argument to an array.")]
        [CliArgument(Name = "samples", Required = true, Arity = CliArgumentArity.OneOrMore, ValidationRules = CliValidationRules.ExistingFile, Description = "Saved search_threads JSON pages.")]
        public FileInfo[] Samples { get; set; } = [];

        /// <summary>Rules YAML. Defaults to $MAIL_RULES, else ~/code/personal/rig/temp/mail/rules.yaml.</summary>
        [CliOption(Name = "--rules", Required = false, Description = "Rules YAML. Defaults to $MAIL_RULES, else ~/code/personal/rig/temp/mail/rules.yaml.")]
        public FileInfo? Rules { get; set; }

        /// <summary>JSON output.</summary>
        [CliOption(Name = "--json", Description = "JSON output.")]
        public bool Json { get; set; }

        /// <summary>Only senders rules miss.</summary>
        [CliOption(Name = "--unclaimed", Description = "Only senders rules miss.")]
        public bool Unclaimed { get; set; }

        /// <summary>The table, busiest sender first, or the same rows as JSON.</summary>
        public int Run() => WithRules(host, error, Rules, ruleset =>
        {
            IReadOnlyList<SampleMessage> sample;
            try
            {
                sample = MailCoverage.ReadSample(Samples.Select(file => file.FullName));
            }
            catch (InvalidDataException e)
            {
                error.WriteLine($"error: {e.Message}");
                return FailedExit;
            }

            var rows = MailCoverage.Coverage(ruleset, sample).Where(row => !Unclaimed || !row.FullyClaimed).ToList();
            if (Json)
            {
                output.WriteLine(MailCoverage.CoverageJson(rows));
                return (int)ExitCode.Clean;
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
            return (int)ExitCode.Clean;
        });
    }
}

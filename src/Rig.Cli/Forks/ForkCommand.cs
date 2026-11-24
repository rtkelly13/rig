using System.CommandLine;
using System.Globalization;
using DotMake.CommandLine;
using Rig.Cli.Commands;
using Rig.Cli.Rendering;
using Rig.Core;
using Rig.Core.Config;
using Rig.Core.Forks;
using Rig.Core.Processes;

namespace Rig.Cli.Forks;

/// <summary>
/// <c>rig fork assess</c>: what forking a project asks of the person forking it, read from one revision of a clone.
/// Oxbow runs a fork once it exists; this is the step before. Exit codes: 0 no blockers, 1 a blocker, 2 an error.
/// </summary>
/// <param name="git">Runs git, hardened under untrusted folders.</param>
/// <param name="configPath">The default <c>config/fork-assessment.json</c>.</param>
/// <param name="workingDirectory">The folder the command was run from.</param>
/// <param name="output">Where the report goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "fork", Description = "Assess a project before forking it.")]
internal sealed class ForkCommand(Git git, string configPath, string workingDirectory, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal ForkCommand()
        : this(null!, string.Empty, string.Empty, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The <c>rig fork</c> command group.</summary>
    public Command Build()
    {
        var fork = DeclarativeCommand.Build(this);
        fork.Add(DeclarativeCommand.Build(new AssessCommand(git, configPath, workingDirectory, output, error)));
        return fork;
    }

    /// <summary>A group named without one of its verbs is the parser's error.</summary>
    public int Run() => DeclarativeCommand.RequireSubcommand(error);

    /// <summary>The report as machine-safe text: the summary, the findings, then each finding's responsibility.</summary>
    public static string Render(ForkAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        return string.Join('\n', Tables(assessment).Select(t => t.Render()));
    }

    private static IEnumerable<TextTable> Tables(ForkAssessment assessment)
    {
        var people = assessment.People;
        var summary = new TextTable($"Fork assessment of {assessment.Revision[..Math.Min(12, assessment.Revision.Length)]} ({assessment.RevisionDate:yyyy-MM-dd})", [("fact", false), ("value", false)])
            .Add("licence", assessment.Licences.Count == 0 ? "none" : string.Join(", ", assessment.Licences.Select(l => $"{l.Path}: {l.Spdx ?? "unrecognised"}")))
            .Add("commits", $"{people.Commits} (+{people.Merges} merges), {people.Tags} tags, since {people.FirstCommit:yyyy-MM-dd}")
            .Add("contributors", $"{people.Contributors} people from {people.Identities} identities, bus factor {people.BusFactor}")
            .Add("recent", $"{people.RecentCommits} commits, busiest person {ForkAssessment.Percent(people.RecentTopShare)}");
        if (assessment.Ownership is { } ownership)
        {
            _ = summary.Add("lines", $"{ownership.Lines} attributed, " + string.Join(", ", ownership.Leaders.Take(3).Select(l => $"{l.Name} {ForkAssessment.Percent(l.Share)}")));
        }

        yield return summary;

        var frequent = new TextTable("Frequent contributors", [("name", false), ("commits", true), ("share", true), ("years", false), ("identities", true)]);
        foreach (var c in people.Frequent)
        {
            _ = frequent.Add(c.Name, c.Commits.ToString(CultureInfo.InvariantCulture), ForkAssessment.Percent(c.Share), $"{c.FirstYear}-{c.LastYear}", (c.Emails.Count + c.Aliases.Count).ToString(CultureInfo.InvariantCulture));
        }

        yield return frequent;

        var years = new TextTable("Activity by year", [("year", false), ("commits", true), ("people", true), ("busiest", false)]);
        foreach (var y in people.Years)
        {
            _ = years.Add(y.Year.ToString(CultureInfo.InvariantCulture), y.Commits.ToString(CultureInfo.InvariantCulture), y.Contributors.ToString(CultureInfo.InvariantCulture), string.Join(", ", y.Leaders));
        }

        yield return years;

        var findings = new TextTable("Findings", [("severity", false), ("id", false), ("finding", false)]);
        foreach (var f in assessment.Findings)
        {
            _ = findings.Add(f.Severity.ToString().ToLowerInvariant(), f.Id, f.Title);
            foreach (var line in f.Evidence)
            {
                _ = findings.Add(string.Empty, string.Empty, "  " + line);
            }
        }

        yield return findings;

        var duties = new TextTable("Responsibilities", [("id", false), ("what to do", false)]);
        foreach (var f in assessment.Findings)
        {
            _ = duties.Add(f.Id, f.Responsibility);
        }

        yield return duties;
    }

    /// <summary><c>rig fork assess [path]</c>.</summary>
    [CliCommand(Name = "assess", Description = "Report the licence, naming, people and history facts a fork must answer to. Read-only and deterministic.")]
    internal sealed class AssessCommand(Git git, string configPath, string workingDirectory, TextWriter output, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal AssessCommand()
            : this(null!, string.Empty, string.Empty, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>The clone to assess.</summary>
        [CliArgument(Name = "path", Required = false, Arity = CliArgumentArity.ZeroOrOne, Description = "The clone to assess (default: the current folder).")]
        public string? Path { get; set; }

        /// <summary>The revision to assess.</summary>
        [CliOption(Name = "--rev", Required = false, Description = "The revision to assess (default: HEAD). Pin it to make the report reproducible.")]
        public string Revision { get; set; } = "HEAD";

        /// <summary>Attribute every line too.</summary>
        [CliOption(Name = "--blame", Description = "Also attribute every line at the revision (slow on large repositories).")]
        public bool Blame { get; set; }

        /// <summary>An extra mailmap.</summary>
        [CliOption(Name = "--mailmap", Required = false, Description = "A mailmap file merging identities the repository's own does not.")]
        public string? Mailmap { get; set; }

        /// <summary>The refs searched for unmerged work.</summary>
        [CliOption(Name = "--refs", Required = false, AllowMultipleArgumentsPerToken = true, Description = "Refs to search for unmerged work, as git for-each-ref patterns (default: every branch). Use refs/remotes/upstream when the clone is already a fork.")]
        public string[] Refs { get; set; } = [];

        /// <summary>Another configuration.</summary>
        [CliOption(Name = "--config", Required = false, Description = "The assessment configuration (default: rig's config/fork-assessment.json).")]
        public string? Config { get; set; }

        /// <summary>JSON output.</summary>
        [CliOption(Name = "--json", Description = "Machine-readable output.")]
        public bool Json { get; set; }

        /// <summary>The handler: read, assess, report.</summary>
        public async Task<int> RunAsync(CliContext context)
        {
            var repo = System.IO.Path.GetFullPath(Path ?? workingDirectory, workingDirectory);
            try
            {
                var config = ForkAssessmentConfig.Load(Config is null ? configPath : System.IO.Path.GetFullPath(Config, workingDirectory));
                var mailmap = Mailmap is null ? null : System.IO.Path.GetFullPath(Mailmap, workingDirectory);
                var facts = await new ForkFactsReader(git).ReadAsync(repo, Revision, config, mailmap, Blame, Refs, context.CancellationToken).ConfigureAwait(false);
                var assessment = ForkAssessment.Assess(config, facts);
                if (Json)
                {
                    await output.WriteAsync(assessment.ToJson() + "\n").ConfigureAwait(false);
                }
                else
                {
                    var console = RigConsole.Create(output);
                    console.RenderVerdict(
                        assessment.Blocked ? "Blocked" : "No blockers",
                        !assessment.Blocked,
                        $"{assessment.Findings.Count} findings; read each responsibility before forking");
                    foreach (var table in Tables(assessment))
                    {
                        table.RenderTo(console);
                    }
                }

                return (int)(assessment.Blocked ? ExitCode.Drift : ExitCode.Clean);
            }
            catch (Exception e) when (e is ConfigLoadException or ProcException)
            {
                await error.WriteLineAsync($"rig fork assess: {e.Message}").ConfigureAwait(false);
                return (int)ExitCode.Error;
            }
        }
    }
}

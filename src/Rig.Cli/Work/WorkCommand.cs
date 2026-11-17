using System.CommandLine;
using System.Globalization;
using DotMake.CommandLine;
using Rig.Cli.Rendering;
using Rig.Core;
using Rig.Core.WorkItems;

namespace Rig.Cli.Work;

/// <summary>Where the work list looks when the caller names no root, and whose home a <c>~</c> stands for.</summary>
/// <param name="CodeRoot">The code root holding every clone.</param>
/// <param name="Home">The user's home folder.</param>
internal sealed record WorkHost(string CodeRoot, string Home);

/// <summary>Collects the estate's work items; the real one reads git, GitHub and Claude's session files.</summary>
/// <param name="options">What to collect and how.</param>
/// <param name="cancellationToken">Stops the collection.</param>
internal delegate Task<Collected> CollectWork(WorkCollectOptions options, CancellationToken cancellationToken);

/// <summary><c>rig work</c>: work items joining branches, worktrees, PRs and Claude sessions. Read-only.</summary>
/// <param name="host">The default root and the user's home.</param>
/// <param name="collect">Collects the estate.</param>
/// <param name="time">The clock <c>--since</c> counts back from.</param>
/// <param name="output">Where the list goes.</param>
/// <param name="error">Where warnings, timings and refusals go.</param>
[CliCommand(Name = "work", Description = "Work items: one row per (repo, branch), with its worktrees, PR and Claude sessions.")]
internal sealed class WorkCommand(WorkHost host, CollectWork collect, TimeProvider time, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal WorkCommand()
        : this(null!, null!, TimeProvider.System, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The complete work CLI.</summary>
    public Command Build()
    {
        var work = DeclarativeCommand.Build(this);
        work.Add(DeclarativeCommand.Build(new ListCommand(host, collect, time, output, error)));
        return work;
    }

    /// <summary>A group named without its verb is the parser's error.</summary>
    public int Run() => DeclarativeCommand.RequireSubcommand(error);

    /// <summary><c>rig work list</c>: branches across the estate joined with PRs and Claude sessions.</summary>
    [CliCommand(Name = "list", Description = "List work items: branches across the estate joined with PRs and Claude sessions.")]
    internal sealed class ListCommand(WorkHost host, CollectWork collect, TimeProvider time, TextWriter output, TextWriter error)
    {
        private static readonly Dictionary<string, WorkState> States =
            Enum.GetValues<WorkState>().ToDictionary(state => state.Wire(), StringComparer.OrdinalIgnoreCase);

        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal ListCommand()
            : this(null!, null!, TimeProvider.System, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>Only repos whose name contains this text.</summary>
        [CliOption(Name = "--repo", Required = false, Description = "Only repos whose name contains this text.")]
        public string? Repo { get; set; }

        /// <summary>Only these states.</summary>
        [CliOption(Name = "--state", Required = false, Description = "Only these states (repeatable): active, merged, closed, stale, trunk, unattached.")]
        public string[]? State { get; set; }

        /// <summary>Only items active since this moment.</summary>
        [CliOption(Name = "--since", Required = false, Description = "Only items active since: 7d, 24h, 2w or an ISO date.")]
        public string? Since { get; set; }

        /// <summary>Include quiet trunk items.</summary>
        [CliOption(Name = "--all", Description = "Include quiet trunk items (clean default branch, no sessions).")]
        public bool All { get; set; }

        /// <summary>Machine-readable output.</summary>
        [CliOption(Name = "--json", Description = "Machine-readable output (schema in docs/desktop-manager/work-items.md).")]
        public bool Json { get; set; }

        /// <summary>List each item's sessions under it.</summary>
        [CliOption(Name = "--sessions", Description = "List each item's sessions under it.")]
        public bool Sessions { get; set; }

        /// <summary>Skip GitHub.</summary>
        [CliOption(Name = "--offline", Description = "Skip GitHub; PR columns read '-'.")]
        public bool Offline { get; set; }

        /// <summary>Bypass the caches.</summary>
        [CliOption(Name = "--refresh", Description = "Bypass the PR and clone-list caches.")]
        public bool Refresh { get; set; }

        /// <summary>Skip git status per checkout.</summary>
        [CliOption(Name = "--no-status", Description = "Skip git status per checkout (faster; dirty reads unknown).")]
        public bool NoStatus { get; set; }

        /// <summary>Days without activity before an item is stale.</summary>
        [CliOption(Name = "--stale-days", Description = "Days without activity before 'stale'.")]
        public int StaleDays { get; set; } = 14;

        /// <summary>How far back to read Claude sessions.</summary>
        [CliOption(Name = "--session-days", Description = "How far back to read Claude sessions.")]
        public int SessionDays { get; set; } = 30;

        /// <summary>Code root holding every clone.</summary>
        [CliOption(Name = "--root", Required = false, Description = "Code root holding every clone.")]
        public string? Root { get; set; }

        /// <summary>Print seconds per phase.</summary>
        [CliOption(Name = "--timings", Description = "Print seconds per phase to stderr.")]
        public bool Timings { get; set; }

        /// <summary>Collects the estate and writes the filtered list.</summary>
        public async Task<int> RunAsync(CliContext context)
        {
            // Refused before anything is collected: a typo should not cost a walk of the estate.
            if (Filter() is not { } filter)
            {
                return (int)ExitCode.Error;
            }

            var options = new WorkCollectOptions(Path.GetFullPath(Expand(Root ?? host.CodeRoot)))
            {
                SessionWindow = TimeSpan.FromDays(SessionDays),
                StaleAfter = TimeSpan.FromDays(StaleDays),
                Offline = Offline,
                Refresh = Refresh,
                Probe = !NoStatus,
            };
            var collected = await collect(options, context.CancellationToken).ConfigureAwait(false);
            var report = collected.Report;
            var items = WorkJoin.FilterItems(report.Items, filter);
            if (Json)
            {
                await output.WriteAsync(WorkJson.Render(report, items) + "\n").ConfigureAwait(false);
            }
            else
            {
                await output.WriteAsync(Table(report, items) + WorkText.Summary(report.Items.Count, items) + "\n").ConfigureAwait(false);
                foreach (var warning in report.Warnings)
                {
                    await error.WriteAsync($"warning: {Line(warning)}\n").ConfigureAwait(false);
                }
            }

            if (Timings)
            {
                var phases = string.Join("  ", collected.Timings.Phases.Select(phase => string.Create(CultureInfo.InvariantCulture, $"{phase.Key}={phase.Value:F2}s")));
                await error.WriteAsync(string.Create(CultureInfo.InvariantCulture, $"timings: {phases}  wall={collected.Total:F2}s\n")).ConfigureAwait(false);
            }

            return (int)ExitCode.Clean;
        }

        // A cell is one line: a session title or a branch name can carry a newline or an escape sequence.
        private static string Line(string text) =>
            string.Concat(TextSanitizer.StripAnsi(text).Select(character => char.IsControl(character) ? ' ' : character));

        private ItemFilter? Filter()
        {
            var states = new HashSet<WorkState>();
            foreach (var name in State ?? [])
            {
                if (!States.TryGetValue(name, out var state))
                {
                    error.Write($"rig work list: --state: '{name}' is not one of {string.Join(", ", States.Keys)}.\n");
                    return null;
                }

                states.Add(state);
            }

            DateTimeOffset? since = null;
            if (Since is { Length: > 0 } text)
            {
                try
                {
                    since = WorkText.ParseSince(text, time.GetUtcNow());
                }
                catch (FormatException e)
                {
                    error.Write($"rig work list: --since: {e.Message}\n");
                    return null;
                }
            }

            return new ItemFilter { Repo = Repo, States = states, Since = since, IncludeQuietTrunk = All };
        }

        private string Table(WorkReport report, IReadOnlyList<WorkItem> items)
        {
            var table = new TextTable(null, [.. WorkText.Columns.Select(heading => (heading, false))]);
            foreach (var item in items)
            {
                table.Add([.. WorkText.Cells(item, report.GeneratedAt).Select(Line)]);
                foreach (var session in Sessions ? item.Sessions : [])
                {
                    table.Add([.. WorkText.SessionCells(session, report.GeneratedAt).Select(Line)]);
                }
            }

            return table.Render();
        }

        private string Expand(string path) =>
            path == "~" ? host.Home
            : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal) ? Path.Join(host.Home, path[2..])
            : path;
    }
}

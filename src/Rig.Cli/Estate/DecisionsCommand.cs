using System.CommandLine;
using DotMake.CommandLine;
using Rig.Cli.Rendering;
using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Workspace;

namespace Rig.Cli.Estate;

/// <summary>
/// <c>rig estate decisions</c>: the worktrees and directories only a human can decide about, as a table, as JSON, or
/// written to the feed the web console reads. The
/// scheduled <c>estate-decisions</c> job runs it with <c>--write</c>.
/// </summary>
/// <param name="host">The default roots, <c>~</c> in a path, and where the feed lives.</param>
/// <param name="git">Reads each clone and worktree.</param>
/// <param name="github">Asks GitHub for each idle worktree's newest PR.</param>
/// <param name="time">The clock idleness is measured by.</param>
/// <param name="windows">Paths compare without case when the roots overlap.</param>
/// <param name="output">Where the table or the JSON goes.</param>
/// <param name="error">Where skipped roots and the written feed are reported, so <c>--json</c> stays clean.</param>
[CliCommand(
    Name = "decisions",
    Description = "Worktrees and directories only a human can decide about: dubious ownership, unpushed or uncommitted work, branches "
        + "whose PR was closed or never opened, and directories an earlier delete left behind. Read-only: each decision "
        + "carries the command that would act on it.")]
internal sealed class DecisionsCommand(EstateHost host, Git git, IGitHubApi github, TimeProvider time, bool windows, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal DecisionsCommand()
        : this(null!, null!, null!, TimeProvider.System, false, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>Code roots to scan.</summary>
    [CliOption(Name = "--root", Required = false, Description = "Code root to scan; repeat for several")]
    public string[] Roots { get; set; } = [];

    /// <summary>Write the feed as JSON to stdout.</summary>
    [CliOption(Name = "--json", Description = "Write the feed as JSON to stdout")]
    public bool Json { get; set; }

    /// <summary>The file the feed is also written to.</summary>
    [CliOption(Name = "--write", Required = false, Description = "Also write the feed JSON to this file")]
    public string? Write { get; set; }

    /// <summary>Worktree idle threshold, in days.</summary>
    [CliOption(Name = "--idle-days", Description = "Worktree idle threshold")]
    public int IdleDays { get; set; } = Trim.DefaultIdleDays;

    /// <summary>The <c>decisions</c> subcommand.</summary>
    public Command Build() => DeclarativeCommand.Build(this);

    /// <summary>The handler: the decisions.</summary>
    public Task<int> RunAsync(CliContext context) => RunAsync(Roots, Json, Write, IdleDays, context.CancellationToken);

    /// <summary>The personal folder, its org folders and this machine's work folders, where they exist.</summary>
    internal IReadOnlyList<string> DefaultRoots()
    {
        var personal = PathResolution.Resolve(host.PersonalDir);
        var others = EstateScopes.Load(host.ScopesFile).OrgDirs(personal)
            .Concat(MachineConfig.WorkDirs(host.MachineConfigFile, Path.GetDirectoryName(personal) ?? personal, host.Home));
        return [personal, .. others.Where(Directory.Exists)];
    }

    private async Task<int> RunAsync(string[] roots, bool json, string? write, int idleDays, CancellationToken cancellationToken)
    {
        var paths = roots.Length > 0 ? [.. roots.Select(root => PathResolution.Resolve(host.ExpandHome(root)))] : DefaultRoots();
        foreach (var path in paths.Where(path => !Directory.Exists(path)))
        {
            await error.WriteLineAsync($"skipping {path}: not a directory".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        var decisions = new Decisions(git, time, Decisions.GitHub(new PullRequests(git, github)), windows);
        var found = await decisions.CollectRootsAsync(paths, idleDays, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(write))
        {
            var target = host.ExpandHome(write);
            Decisions.WriteFeed(target, found);
            await error.WriteLineAsync($"wrote {found.Count} decision(s) to {target}".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        if (json)
        {
            await output.WriteLineAsync(Decisions.DumpFeed(found).AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var table = new TextTable(
            $"{found.Count} decision(s)",
            [("kind", false), ("repo", false), ("path", false), ("size", true), ("reason", false), ("command", false)]);
        foreach (var item in found)
        {
            _ = table.Add(item.Kind, item.Repo, item.Path, Bytes.Human(item.SizeBytes), item.Reason, item.Command);
        }

        await output.WriteAsync(table.Render().AsMemory(), cancellationToken).ConfigureAwait(false);
        return 0;
    }
}

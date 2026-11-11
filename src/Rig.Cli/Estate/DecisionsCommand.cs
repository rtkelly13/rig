using System.CommandLine;
using Rig.Cli.Vault;
using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Workspace;

namespace Rig.Cli.Estate;

/// <summary>
/// <c>rig estate decisions</c>: the worktrees and directories only a human can decide about, as a table, as JSON, or
/// written to the feed the web console reads. Ported from the Python <c>tiers_cli.decisions</c>; the
/// scheduled <c>estate-decisions</c> job runs it with <c>--write</c>.
/// </summary>
/// <param name="host">The default roots, <c>~</c> in a path, and where the feed lives.</param>
/// <param name="git">Reads each clone and worktree.</param>
/// <param name="github">Asks GitHub for each idle worktree's newest PR.</param>
/// <param name="time">The clock idleness is measured by.</param>
/// <param name="windows">Paths compare without case when the roots overlap.</param>
/// <param name="output">Where the table or the JSON goes.</param>
/// <param name="error">Where skipped roots and the written feed are reported, so <c>--json</c> stays clean.</param>
internal sealed class DecisionsCommand(EstateHost host, Git git, IGitHubApi github, TimeProvider time, bool windows, TextWriter output, TextWriter error)
{
    /// <summary>The <c>decisions</c> subcommand.</summary>
    public Command Build()
    {
        var roots = new Option<string[]>("--root") { Description = "Code root to scan; repeat for several", AllowMultipleArgumentsPerToken = false };
        var json = new Option<bool>("--json") { Description = "Write the feed as JSON to stdout" };
        var write = new Option<string?>("--write") { Description = "Also write the feed JSON to this file" };
        var idleDays = new Option<int>("--idle-days") { Description = "Worktree idle threshold", DefaultValueFactory = _ => Trim.DefaultIdleDays };
        var command = new Command(
            "decisions",
            "Worktrees and directories only a human can decide about: dubious ownership, unpushed or uncommitted work, branches "
            + "whose PR was closed or never opened, and directories an earlier delete left behind. Read-only: each decision "
            + "carries the command that would act on it.")
        {
            roots, json, write, idleDays,
        };
        command.SetAction((parsed, cancellationToken) => RunAsync(
            parsed.GetValue(roots) ?? [], parsed.GetValue(json), parsed.GetValue(write), parsed.GetValue(idleDays), cancellationToken));
        return command;
    }

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

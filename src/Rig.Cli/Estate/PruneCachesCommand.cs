using System.CommandLine;
using System.Text.Json.Nodes;
using DotMake.CommandLine;
using Rig.Cli.Rendering;
using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Text;

namespace Rig.Cli.Estate;

/// <summary>
/// <c>rig estate prune-caches</c>: prune the caches that refill themselves and say what each gave back. The scheduled <c>estate-prune-caches</c> job runs it with
/// <c>--apply</c>.
/// </summary>
/// <param name="host">Expands <c>~</c> in <c>--claude-temp</c>.</param>
/// <param name="proc">Runs uv, pnpm, dotnet and docker, and icacls on Windows when a session folder is removed.</param>
/// <param name="findTool">Finds each tool's executable.</param>
/// <param name="time">The clock a Claude session's idleness is measured by.</param>
/// <param name="output">Where the report goes.</param>
/// <param name="error">Where a refused option goes.</param>
[CliCommand(
    Name = "prune-caches",
    Description = "Prune the caches that refill themselves: uv, pnpm, NuGet http, Claude temp, Docker build. A tool that is not "
        + "installed, or a Docker daemon that is not running, is skipped with the reason. Exits 1 when any category failed.")]
internal sealed class PruneCachesCommand(EstateHost host, IProc proc, Func<string, string?> findTool, TimeProvider time, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal PruneCachesCommand()
        : this(null!, null!, static _ => null, TimeProvider.System, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>Actually prune.</summary>
    [CliOption(Name = "--apply", Description = "Actually prune. Off by default.")]
    public bool Apply { get; set; }

    /// <summary>Claude temp sessions idle this many days are pruned.</summary>
    [CliOption(Name = "--idle-days", Description = "Claude temp sessions idle this long")]
    public int IdleDays { get; set; } = Trim.DefaultIdleDays;

    /// <summary>Claude Code temp root.</summary>
    [CliOption(Name = "--claude-temp", Required = false, Description = "Claude Code temp root (default: <temp>/claude)")]
    public string? ClaudeTemp { get; set; }

    /// <summary>Write the results as JSON to stdout.</summary>
    [CliOption(Name = "--json", Description = "Write the results as JSON to stdout")]
    public bool Json { get; set; }

    /// <summary>The <c>prune-caches</c> subcommand.</summary>
    public Command Build() => DeclarativeCommand.Build(this);

    /// <summary>The handler: prunes, or reports what would be.</summary>
    public Task<int> RunAsync(CliContext context) => RunAsync(Apply, IdleDays, ClaudeTemp, Json, context.CancellationToken);

    private async Task<int> RunAsync(bool apply, int idleDays, string? claudeTemp, bool json, CancellationToken cancellationToken)
    {
        // A negative period puts the cutoff in the future, so every session, even a live one, would read as idle.
        if (idleDays < 0)
        {
            await error.WriteLineAsync($"--idle-days must be 0 or more, not {idleDays}.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 2;
        }

        var root = string.IsNullOrEmpty(claudeTemp) ? Caches.DefaultClaudeTemp() : host.ExpandHome(claudeTemp);
        var caches = new Caches(proc, findTool, new TreeRemoval(proc), time);
        var results = await caches.PruneAllAsync(apply, root, idleDays, cancellationToken).ConfigureAwait(false);

        if (json)
        {
            var payload = new JsonArray([.. results.Select(item => (JsonNode)new JsonObject
            {
                ["category"] = item.Category,
                ["command"] = item.Command,
                ["status"] = item.StatusName,
                ["reason"] = item.Reason,
                ["size_before"] = item.SizeBefore,
                ["freed"] = item.Freed,
            })]);
            await output.WriteLineAsync(payload.ToJsonString(JsonOutputOptions.IndentedUnicode).AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var table = new TextTable(
                "Caches" + (apply ? string.Empty : " (dry run — pass --apply)"),
                [("category", false), ("status", false), ("size", true), ("frees", true), ("detail", false)]);
            foreach (var item in results)
            {
                _ = table.Add(item.Category, item.StatusName, Shown(item.SizeBefore), Shown(item.Freed), item.Reason);
            }

            var freed = results.Sum(item => item.Freed ?? 0);
            await output.WriteAsync(table.Render().AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"\n{(apply ? "Freed" : "Known to free")} {Bytes.Human(freed)}".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return results.Any(item => item.Status == PruneStatus.Failed) ? 1 : 0;
    }

    private static string Shown(long? size) => size is { } known ? Bytes.Human(known) : "?";
}

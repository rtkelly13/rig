using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Cli.Vault;
using Rig.Core.Estate;
using Rig.Core.Processes;

namespace Rig.Cli.Estate;

/// <summary>
/// <c>rig estate prune-caches</c>: prune the caches that refill themselves and say what each gave back. Ported from
/// <c>src/rig/estate/tiers_cli.py</c> <c>prune_caches</c>; the scheduled <c>estate-prune-caches</c> job runs it with
/// <c>--apply</c>.
/// </summary>
/// <param name="host">Expands <c>~</c> in <c>--claude-temp</c>.</param>
/// <param name="proc">Runs uv, pnpm, dotnet and docker, and icacls on Windows when a session folder is removed.</param>
/// <param name="findTool">Finds each tool's executable.</param>
/// <param name="time">The clock a Claude session's idleness is measured by.</param>
/// <param name="output">Where the report goes.</param>
internal sealed class PruneCachesCommand(EstateHost host, IProc proc, Func<string, string?> findTool, TimeProvider time, TextWriter output)
{
    // As Python's json writes it: a `+` or a non-ASCII path stays as it is, not escaped.
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The <c>prune-caches</c> subcommand.</summary>
    public Command Build()
    {
        var apply = new Option<bool>("--apply") { Description = "Actually prune. Off by default." };
        var idleDays = new Option<int>("--idle-days")
        {
            Description = "Claude temp sessions idle this long",
            DefaultValueFactory = _ => Trim.DefaultIdleDays,
        };
        var claudeTemp = new Option<string?>("--claude-temp") { Description = "Claude Code temp root (default: <temp>/claude)" };
        var json = new Option<bool>("--json") { Description = "Write the results as JSON to stdout" };
        var command = new Command(
            "prune-caches",
            "Prune the caches that refill themselves: uv, pnpm, NuGet http, Claude temp, Docker build. A tool that is not "
            + "installed, or a Docker daemon that is not running, is skipped with the reason. Exits 1 when any category failed.")
        {
            apply, idleDays, claudeTemp, json,
        };
        command.SetAction((parsed, cancellationToken) => RunAsync(
            parsed.GetValue(apply), parsed.GetValue(idleDays), parsed.GetValue(claudeTemp), parsed.GetValue(json), cancellationToken));
        return command;
    }

    private async Task<int> RunAsync(bool apply, int idleDays, string? claudeTemp, bool json, CancellationToken cancellationToken)
    {
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
            await output.WriteLineAsync(payload.ToJsonString(Indented).AsMemory(), cancellationToken).ConfigureAwait(false);
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

using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Vault;
using Rig.Core.Estate;
using Rig.Core.Policy;

namespace Rig.Cli.Estate;

/// <summary>
/// <c>rig estate downloads</c>: the large items in Downloads, what kind of thing each is, and which Google Drive already
/// holds byte for byte. Ported from the Python <c>estate downloads</c>. Read-only: nothing is moved or deleted.
/// </summary>
/// <param name="downloads">Scans the folder.</param>
/// <param name="folder">The Downloads folder.</param>
/// <param name="cloudRoots">The Google Drive folders a copy may already be in.</param>
/// <param name="output">Where the table or the JSON goes.</param>
internal sealed class DownloadsCommand(Downloads downloads, string folder, Func<IReadOnlyList<string>> cloudRoots, TextWriter output)
{
    /// <summary>The <c>downloads</c> subcommand.</summary>
    public Command Build()
    {
        var min = new Option<string>("--min") { Description = "Ignore files smaller than this", DefaultValueFactory = _ => "20MB" };
        var json = new Option<bool>("--json") { Description = "Emit for a machine" };
        var command = new Command(
            "downloads",
            "Audit ~/Downloads directory, categorizing large files and checking for Google Drive duplicates.")
        {
            min, json,
        };
        command.SetAction((parsed, cancellationToken) => RunAsync(parsed.GetValue(min)!, parsed.GetValue(json), cancellationToken));
        return command;
    }

    private async Task<int> RunAsync(string min, bool json, CancellationToken cancellationToken)
    {
        var items = await downloads.ScanAsync(folder, cloudRoots(), Bytes.ParseSize(min), cancellationToken).ConfigureAwait(false);

        if (json)
        {
            var payload = new JsonArray([.. items.Select(item => (JsonNode)new JsonObject
            {
                ["name"] = item.Name,
                ["path"] = item.Path,
                ["bytes"] = item.Bytes,
                ["category"] = item.Category,
                ["is_dir"] = item.IsDir,
                ["cloud_duplicate"] = item.CloudDuplicate,
            })]);
            await output.WriteLineAsync(PyJson.Dumps(payload).AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var table = new TextTable("Downloads Triage", [("Item", false), ("Category", false), ("Size", true), ("Cloud Backup Status", true)]);
        foreach (var item in items)
        {
            var status = item.CloudDuplicate is { } copy ? $"duplicate in Drive ({Path.GetFileName(copy)})" : "unique local file";
            _ = table.Add(item.Name, item.Category, Bytes.Human(item.Bytes), status);
        }

        var backedUp = items.Where(item => item.CloudDuplicate is not null).Sum(item => item.Bytes);
        await output.WriteAsync(table.Render().AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(
            $"\n{Bytes.Human(items.Sum(item => item.Bytes))} in large downloads · {Bytes.Human(backedUp)} already backed up to Google Drive".AsMemory(),
            cancellationToken).ConfigureAwait(false);
        return 0;
    }
}

using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Cli.Vault;
using Rig.Core.Estate;
using Rig.Core.Policy;

namespace Rig.Cli.Estate;

/// <summary>A drive's capacity and free space.</summary>
internal readonly record struct DriveSpace(long Total, long Free)
{
    /// <summary>What is in use.</summary>
    public long Used => Total - Free;

    /// <summary>The drive named by its letter, as <c>shutil.disk_usage</c> reads it.</summary>
    public static DriveSpace Of(string drive)
    {
        var info = new DriveInfo(drive.Trim(':') + ":\\");
        return new DriveSpace(info.TotalSize, info.TotalFreeSpace);
    }
}

/// <summary>
/// <c>rig estate volume</c> (Windows): where a whole drive's space went, from a WizTree MFT export. A drill-down of the
/// largest folders, the dev artefacts under the code root with each subtree counted once, and the known Windows hogs
/// with their remedies. Ported from the Python <c>estate volume</c>. Read-only.
/// </summary>
/// <param name="host">The default <c>--code-root</c>.</param>
/// <param name="export">Runs WizTree when no <c>--csv</c> is given.</param>
/// <param name="environment">Expands the <c>%VAR%</c> in a hog's path.</param>
/// <param name="space">Reads a drive's capacity and free space.</param>
/// <param name="output">Where the tables or the JSON go.</param>
/// <param name="error">Where the scan's progress and a failure go.</param>
internal sealed class VolumeCommand(
    EstateHost host, WizTreeExport export, Func<string, string?> environment, Func<string, DriveSpace> space, TextWriter output, TextWriter error)
{
    // Folders under a mebibyte are left out of the export as it loads: they cannot matter at this scale.
    private const long LoadFloor = 1024 * 1024;

    // A hog smaller than this is not worth a row.
    private const long HogFloor = 100 * 1024 * 1024;

    /// <summary>The <c>volume</c> subcommand.</summary>
    public Command Build()
    {
        var drive = new Option<string>("--drive") { Description = "Drive letter to scan", DefaultValueFactory = _ => "C" };
        var csv = new Option<string?>("--csv") { Description = "Reuse an existing WizTree folder export instead of scanning" };
        var keepCsv = new Option<bool>("--keep-csv") { Description = "Keep the export (it is ~400 MB) for a later --csv" };
        var codeRoot = new Option<string?>("--code-root") { Description = "Clone root for the dev-artefact partition" };
        var min = new Option<string>("--min") { Description = "Smallest folder shown in the drill-down", DefaultValueFactory = _ => "5GB" };
        var depth = new Option<int>("--depth") { Description = "Drill-down depth below the drive root", DefaultValueFactory = _ => 4 };
        var json = new Option<bool>("--json") { Description = "Emit for a machine" };
        var command = new Command("volume", "(Windows) Where a whole drive's space went: drill-down, dev-artefact partition, known hogs.")
        {
            drive, csv, keepCsv, codeRoot, min, depth, json,
        };
        command.SetAction((parsed, cancellationToken) => RunAsync(
            new Request(
                parsed.GetValue(drive)!,
                parsed.GetValue(csv),
                parsed.GetValue(keepCsv),
                parsed.GetValue(codeRoot) is { Length: > 0 } root ? root : host.CodeRoot,
                parsed.GetValue(min)!,
                parsed.GetValue(depth)),
            parsed.GetValue(json),
            cancellationToken));
        return command;
    }

    private sealed record Request(string Drive, string? Csv, bool KeepCsv, string CodeRoot, string Min, int Depth);

    private async Task<int> RunAsync(Request request, bool json, CancellationToken cancellationToken)
    {
        IReadOnlyList<Folder> folders;
        DriveSpace usage;
        try
        {
            folders = await LoadAsync(request, cancellationToken).ConfigureAwait(false);
            usage = space(request.Drive);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or InvalidDataException)
        {
            await error.WriteLineAsync(e.Message.AsMemory(), cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var drill = Volume.Tree(folders, Bytes.ParseSize(request.Min), request.Depth);
        var parts = Volume.Partition([.. folders], request.CodeRoot);
        var found = Volume.Hogs([.. folders], Volume.Catalogue, environment);
        var atRoot = Volume.RootFiles([.. folders]);

        if (json)
        {
            var partition = new JsonObject();
            foreach (var (name, size) in parts)
            {
                partition[name] = size;
            }

            var payload = new JsonObject
            {
                ["drive"] = request.Drive,
                ["total"] = usage.Total,
                ["free"] = usage.Free,
                ["tree"] = new JsonArray([.. drill.Select(row => (JsonNode)new JsonObject
                {
                    ["level"] = row.Level,
                    ["path"] = row.Folder.Path,
                    ["allocated"] = row.Folder.Allocated,
                    ["size"] = row.Folder.Size,
                })]),
                ["code_root"] = request.CodeRoot,
                ["partition"] = partition,
                ["hogs"] = new JsonArray([.. found.Select(hit => (JsonNode)new JsonObject
                {
                    ["label"] = hit.Hog.Label,
                    ["allocated"] = hit.Allocated,
                    ["paths"] = new JsonArray([.. hit.Paths.Select(path => (JsonNode)path)]),
                    ["remedy"] = hit.Hog.Remedy,
                    ["safety"] = hit.Hog.Safety,
                })]),
                ["root_files"] = atRoot,
            };
            await output.WriteLineAsync(PyJson.Dumps(payload).AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var share = usage.Total > 0 ? ((double)usage.Free / usage.Total).ToString("0.0%", CultureInfo.InvariantCulture) : "?";
        await output.WriteLineAsync(
            $"{request.Drive.Trim(':')}: {Bytes.Human(usage.Used)} used of {Bytes.Human(usage.Total)} · {Bytes.Human(usage.Free)} free ({share})\n".AsMemory(),
            cancellationToken).ConfigureAwait(false);

        var largest = new TextTable($"Largest folders (>= {request.Min})", [("Folder", false), ("Allocated", true), ("Logical", true)]);
        foreach (var row in drill)
        {
            var label = row.Level == 0 ? row.Folder.Path : new string(' ', 2 * row.Level) + row.Folder.Path[(row.Folder.Path.LastIndexOf('\\') + 1)..];
            _ = largest.Add(label, Bytes.Human(row.Folder.Allocated), Bytes.Human(row.Folder.Size));
        }

        var artefacts = new TextTable($"Dev artefacts under {request.CodeRoot} (outermost match, no overlap)", [("Category", false), ("Allocated", true)]);
        foreach (var (name, size) in parts)
        {
            _ = artefacts.Add(name, Bytes.Human(size));
        }

        var hogs = new TextTable("Known Windows hogs", [("Hog", false), ("Allocated", true), ("Remedy", false), ("Safety", false)]);
        foreach (var hit in found.Where(hit => hit.Allocated >= HogFloor))
        {
            _ = hogs.Add(hit.Hog.Label, Bytes.Human(hit.Allocated), hit.Hog.Remedy, hit.Hog.Safety);
        }

        if (atRoot > 0)
        {
            _ = hogs.Add("Files at drive root", Bytes.Human(atRoot), "powercfg /h off frees hiberfil.sys", "review");
        }

        await output.WriteAsync(string.Join("\n", largest.Render(), artefacts.Render(), hogs.Render()).AsMemory(), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private async Task<IReadOnlyList<Folder>> LoadAsync(Request request, CancellationToken cancellationToken)
    {
        if (request.Csv is { Length: > 0 } csv)
        {
            return Volume.Load(host.ExpandHome(csv), LoadFloor);
        }

        // Resolved, as the elevated WizTree child may not expand a short 8.3 name.
        var target = Path.Join(LongPath(Path.GetTempPath()), $"wiztree-{request.Drive.Trim(':').ToLowerInvariant()}.csv");
        await error.WriteLineAsync($"scanning {request.Drive}: with WizTree (approve the UAC prompt) -> {target}".AsMemory(), cancellationToken).ConfigureAwait(false);
        await export.RunAsync(request.Drive, target, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            return Volume.Load(target, LoadFloor);
        }
        finally
        {
            if (!request.KeepCsv)
            {
                File.Delete(target);
            }
        }
    }

    // The long form of a path written with 8.3 short names (RYANKE~1): a search by the short name answers with the long one.
    private static string LongPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || !full.Contains('~', StringComparison.Ordinal))
        {
            return full;
        }

        var current = Path.GetPathRoot(full) ?? full;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Directory.EnumerateFileSystemEntries(current, part).FirstOrDefault() ?? Path.Join(current, part);
        }

        return current;
    }
}

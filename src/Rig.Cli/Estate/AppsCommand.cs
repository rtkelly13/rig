using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Cli.Vault;
using Rig.Core.Estate;
using Rig.Core.Policy;

namespace Rig.Cli.Estate;

/// <summary>
/// <c>rig estate apps</c>: installed applications by size, version and when each was last touched, the dormant ones
/// marked. Ported from the Python <c>estate apps</c>. Read-only.
/// </summary>
/// <param name="apps">Scans the bundles on macOS, the Uninstall keys on Windows.</param>
/// <param name="appDirs">The folders a Mac keeps applications in.</param>
/// <param name="registry">The Uninstall keys on Windows; null elsewhere, where the bundles are walked instead.</param>
/// <param name="time">When "now" is, for dormancy.</param>
/// <param name="output">Where the table or the JSON goes.</param>
internal sealed class AppsCommand(InstalledApps apps, IReadOnlyList<string> appDirs, IUninstallRegistry? registry, TimeProvider time, TextWriter output)
{
    /// <summary>The <c>apps</c> subcommand.</summary>
    public Command Build()
    {
        var min = new Option<string>("--min") { Description = "Ignore apps smaller than this", DefaultValueFactory = _ => "100MB" };
        var json = new Option<bool>("--json") { Description = "Emit for a machine" };
        var command = new Command("apps", "Audit installed desktop applications by size, version, and last-modified date.") { min, json };
        command.SetAction((parsed, cancellationToken) => RunAsync(parsed.GetValue(min)!, parsed.GetValue(json), cancellationToken));
        return command;
    }

    private async Task<int> RunAsync(string min, bool json, CancellationToken cancellationToken)
    {
        var floor = Bytes.ParseSize(min);
        var items = registry is null
            ? await apps.ScanBundlesAsync(appDirs, floor, cancellationToken).ConfigureAwait(false)
            : await apps.ScanRegistryAsync(registry, floor, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();

        if (json)
        {
            var payload = new JsonArray([.. items.Select(app => (JsonNode)new JsonObject
            {
                ["name"] = app.Name,
                ["path"] = app.Path,
                ["bytes"] = app.Bytes,
                ["version"] = app.Version,
                ["modified"] = PythonTime.IsoFormat(app.Modified),
                ["accessed"] = PythonTime.IsoFormat(app.Accessed),
                ["is_dormant"] = app.IsDormant(now),
            })]);
            await output.WriteLineAsync(PyJson.Dumps(payload).AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var table = new TextTable(
            "Installed Applications",
            [("Application", false), ("Size", true), ("Version", false), ("Last Modified", false), ("Status", true)]);
        foreach (var app in items)
        {
            _ = table.Add(
                app.Name,
                Bytes.Human(app.Bytes),
                app.Version,
                app.Modified.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                app.IsDormant(now) ? "dormant" : "active");
        }

        var dormant = items.Where(app => app.IsDormant(now)).Sum(app => app.Bytes);
        await output.WriteAsync(table.Render().AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(
            $"\n{Bytes.Human(items.Sum(app => app.Bytes))} across {items.Count} applications · {Bytes.Human(dormant)} in dormant apps (>{InstalledApp.DormantDays} days untouched)".AsMemory(),
            cancellationToken).ConfigureAwait(false);
        return 0;
    }
}

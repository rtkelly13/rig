using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using DotMake.CommandLine;
using Rig.Cli.Rendering;
using Rig.Core.Estate;
using Rig.Core.Text;

namespace Rig.Cli.Estate;

/// <summary>
/// <c>rig estate apps</c>: installed applications by size, version and when each was last touched, the dormant ones
/// marked. Read-only.
/// </summary>
/// <param name="apps">Scans the bundles on macOS, the Uninstall keys on Windows.</param>
/// <param name="appDirs">The folders a Mac keeps applications in.</param>
/// <param name="registry">The Uninstall keys on Windows; null elsewhere, where the bundles are walked instead.</param>
/// <param name="time">When "now" is, for dormancy.</param>
/// <param name="output">Where the table or the JSON goes.</param>
[CliCommand(Name = "apps", Description = "Audit installed desktop applications by size, version, and last-modified date.")]
internal sealed class AppsCommand(InstalledApps apps, IReadOnlyList<string> appDirs, IUninstallRegistry? registry, TimeProvider time, TextWriter output)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal AppsCommand()
        : this(null!, [], null, TimeProvider.System, TextWriter.Null)
    {
    }

    /// <summary>Ignore apps smaller than this.</summary>
    [CliOption(Name = "--min", Description = "Ignore apps smaller than this")]
    public string Min { get; set; } = "100MB";

    /// <summary>Emit for a machine.</summary>
    [CliOption(Name = "--json", Description = "Emit for a machine")]
    public bool Json { get; set; }

    /// <summary>The <c>apps</c> subcommand.</summary>
    public Command Build() => DeclarativeCommand.Build(this);

    /// <summary>The handler: the installed applications.</summary>
    public Task<int> RunAsync(CliContext context) => RunAsync(Min, Json, context.CancellationToken);

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
                ["modified"] = IsoTime.Format(app.Modified),
                ["accessed"] = IsoTime.Format(app.Accessed),
                ["is_dormant"] = app.IsDormant(now),
            })]);
            await output.WriteLineAsync(JsonOutput.Format(payload).AsMemory(), cancellationToken).ConfigureAwait(false);
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

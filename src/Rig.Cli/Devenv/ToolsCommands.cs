#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using Rig.Core.Devenv;
using Rig.Core.Processes;

namespace Rig.Cli.Devenv;

/// <summary>
/// <c>rig devenv tools</c>, <c>runtimes</c> and <c>update</c>: the developer tool catalog, checked and installed through
/// the platform's package manager.
/// </summary>
/// <param name="host">Where it looks.</param>
/// <param name="proc">Runs the package manager, uv and fnm.</param>
/// <param name="output">Where progress goes.</param>
/// <param name="error">Where failures go.</param>
internal sealed class ToolsCommands(DevenvHost host, IProc proc, TextWriter output, TextWriter error)
{
    private static readonly string[] Modes = ["full", "express", "agent"];

    /// <summary>The three subcommands, sharing <c>devenv</c>'s <c>--dry-run</c> and <c>--home</c>.</summary>
    public IEnumerable<Command> Build(Option<bool> dryRun, Option<string> home)
    {
        var interactive = new Option<bool>("--interactive") { Description = "Let installers prompt (winget's interactive mode) and show their output." };
        var mode = new Option<string>("--mode") { Description = "full installs every missing tool; express and agent only the required ones.", DefaultValueFactory = _ => "full" };
        mode.AcceptOnlyFromAmong(Modes);
        var check = new Option<bool>("--check") { Description = "List each tool and whether it is installed, changing nothing; exit 1 when a required one is missing." };
        var outdated = new Option<bool>("--outdated")
        {
            Description = "With --check, also ask the package manager (brew outdated / winget upgrade) which installed catalog tools have a newer version, and compare every installed gh extension with its latest GitHub release (needs the network). Informational: an outdated tool or extension does not change the exit code.",
        };
        var upgrade = new Option<bool>("--update", "-u") { Description = "Also upgrade every package the platform package manager owns (brew upgrade / winget upgrade --all), then every gh extension." };
        var tools = new Command("tools", "Check the developer tool catalog and install the missing tools.") { dryRun, home, mode, interactive, check, outdated, upgrade };
        tools.Validators.Add(result =>
        {
            if (result.GetValue(outdated) && !result.GetValue(check))
            {
                result.AddError("--outdated needs --check.");
            }
        });
        tools.SetAction((parsed, cancellationToken) => ToolsAsync(
            parsed.GetValue(home)!,
            parsed.GetValue(mode)!,
            parsed.GetValue(dryRun),
            !parsed.GetValue(interactive),
            parsed.GetValue(check),
            parsed.GetValue(outdated),
            parsed.GetValue(upgrade),
            cancellationToken));
        var runtimes = new Command("runtimes", "Install uv and fnm, then the folder's Python through uv and the Node LTS through fnm.") { dryRun, interactive };
        runtimes.SetAction((parsed, cancellationToken) => RuntimesAsync(parsed.GetValue(dryRun), !parsed.GetValue(interactive), cancellationToken));
        var update = new Command("update", "Upgrade every package the platform package manager owns, then every gh extension (gh extension upgrade --all). Skills are updated by `rig skills update`.");
        update.SetAction((_, cancellationToken) => UpdateAsync(cancellationToken));
        return [tools, runtimes, update];
    }

    private string WorkingDirectory => host.WorkingDirectory ?? host.Home;

    private DevTools Tools() => new(PackageManager.For(host.Windows, host.Find, proc), host.Find);

    private async Task<int> ToolsAsync(string home, string mode, bool dryRun, bool quiet, bool check, bool outdated, bool upgrade, CancellationToken cancellationToken)
    {
        IReadOnlyList<DevTool> catalog;
        try
        {
            catalog = ToolCatalog.Load(home, WorkingDirectory);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Could not read the tool catalog: {e.Message}", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var tools = Tools();
        var missing = new List<DevTool>();
        var present = new List<DevTool>();
        await output.WriteLineAsync("Development tools:", cancellationToken).ConfigureAwait(false);
        foreach (var tool in catalog)
        {
            var installed = await tools.IsInstalledAsync(tool, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"  {tool.Name,-28} {tool.Category,-10} {(installed ? "installed" : tool.Required ? "missing (required)" : "missing"),-18} {tool.Description}", cancellationToken).ConfigureAwait(false);
            (installed ? present : missing).Add(tool);
        }

        if (check)
        {
            if (outdated)
            {
                await ReportOutdatedAsync(tools, present, cancellationToken).ConfigureAwait(false);
            }

            return missing.Any(tool => tool.Required) ? 1 : 0;
        }

        var failed = await InstallAsync(tools, mode == "full" ? missing : [.. missing.Where(tool => tool.Required)], dryRun, quiet, cancellationToken).ConfigureAwait(false);
        if (upgrade && !dryRun && !await UpgradeAsync(tools, quiet: false, cancellationToken).ConfigureAwait(false))
        {
            return 1;
        }

        return failed ? 1 : 0;
    }

    // True when an install failed. A tool with no package on this platform's manager is named and skipped, not failed.
    private async Task<bool> InstallAsync(DevTools tools, List<DevTool> missing, bool dryRun, bool quiet, CancellationToken cancellationToken)
    {
        var wanted = new List<DevTool>();
        foreach (var tool in missing)
        {
            if (tools.Manager is { } manager && manager.IdOf(tool.Install) is null)
            {
                await output.WriteLineAsync($"- {tool.Name}: no {manager.Name} package; install it by hand.", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                wanted.Add(tool);
            }
        }

        if (wanted.Count == 0)
        {
            await output.WriteLineAsync("✓ All target tools are already installed.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (dryRun)
        {
            await output.WriteLineAsync($"Dry run: would install {wanted.Count} tool(s): {string.Join(", ", wanted.Select(tool => tool.Name))}.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        var failed = false;
        foreach (var tool in wanted)
        {
            await output.WriteLineAsync($"Installing {tool.Name}...", cancellationToken).ConfigureAwait(false);
            if (await tools.InstallAsync(tool, quiet, cancellationToken).ConfigureAwait(false))
            {
                await output.WriteLineAsync($"✓ Installed {tool.Name}", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await error.WriteLineAsync($"! Failed to install {tool.Name} via {tools.Manager?.Name ?? "a package manager (none found)"}.", cancellationToken).ConfigureAwait(false);
                failed = true;
            }
        }

        return failed;
    }

    // Catalog tools through the package manager, then every installed gh extension against its latest release.
    private async Task ReportOutdatedAsync(DevTools tools, List<DevTool> present, CancellationToken cancellationToken)
    {
        await ReportOutdatedToolsAsync(tools, present, cancellationToken).ConfigureAwait(false);
        await ReportOutdatedExtensionsAsync(cancellationToken).ConfigureAwait(false);
    }

    // Only installed tools are asked about; a lookup that fails is said, so it never reads as up to date.
    private async Task ReportOutdatedToolsAsync(DevTools tools, List<DevTool> present, CancellationToken cancellationToken)
    {
        await output.WriteLineAsync("Outdated tools:", cancellationToken).ConfigureAwait(false);
        if (tools.Manager is null)
        {
            await output.WriteLineAsync($"  ? Could not check: {(host.Windows ? "winget" : "brew")} is not on the PATH.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await tools.OutdatedAsync(present, cancellationToken).ConfigureAwait(false) is not { } outdated)
        {
            await output.WriteLineAsync($"  ? Could not check: {tools.Manager.Name} did not list its upgrades.", cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var (tool, package) in outdated)
        {
            await output.WriteLineAsync($"  {tool.Name,-28} {package.Installed} -> {package.Available}", cancellationToken).ConfigureAwait(false);
        }

        await output.WriteLineAsync(
            outdated.Count > 0 ? $"  {outdated.Count} tool(s) can be upgraded. Run 'rig devenv update' to upgrade them." : "  ✓ Every installed catalog tool is up to date.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ReportOutdatedExtensionsAsync(CancellationToken cancellationToken)
    {
        await GhExtensionReleaseReport.HeaderAsync(output, cancellationToken).ConfigureAwait(false);
        if (host.Find("gh") is null)
        {
            await output.WriteLineAsync("  ? Could not check: gh is not on the PATH.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var extensions = new GhExtensionManager(proc, new GhApi(proc));
        var (installed, problem) = await extensions.ListAsync(cancellationToken).ConfigureAwait(false);
        if (installed is null)
        {
            await output.WriteLineAsync($"  ? Could not check: gh extension list failed: {problem}", cancellationToken).ConfigureAwait(false);
            return;
        }

        await GhExtensionReleaseReport.RowsAsync(output, extensions, installed, cancellationToken).ConfigureAwait(false);
    }

    // The package manager first, then gh's extensions, which brew and winget do not own.
    private async Task<bool> UpgradeAsync(DevTools tools, bool quiet, CancellationToken cancellationToken)
    {
        if (tools.Manager is null)
        {
            await error.WriteLineAsync($"Package upgrade did not run: {(host.Windows ? "winget" : "brew")} is not on the PATH.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!await tools.Manager.UpgradeAllAsync(quiet, cancellationToken).ConfigureAwait(false))
        {
            await error.WriteLineAsync($"Package upgrade failed: {tools.Manager.Name} exited non-zero.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        return await UpgradeExtensionsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> UpgradeExtensionsAsync(CancellationToken cancellationToken)
    {
        if (host.Find("gh") is null)
        {
            await output.WriteLineAsync("- gh is not on the PATH; skipped gh extension upgrades.", cancellationToken).ConfigureAwait(false);
            return true;
        }

        await output.WriteLineAsync("Upgrading gh extensions...", cancellationToken).ConfigureAwait(false);
        if (!await new GhExtensionManager(proc, new GhApi(proc)).UpgradeAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await error.WriteLineAsync("gh extension upgrade failed: gh extension upgrade --all exited non-zero.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        await output.WriteLineAsync("✓ gh extensions are up to date.", cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<int> UpdateAsync(CancellationToken cancellationToken)
    {
        await output.WriteLineAsync("Updating development tools...", cancellationToken).ConfigureAwait(false);
        if (!await UpgradeAsync(Tools(), quiet: false, cancellationToken).ConfigureAwait(false))
        {
            return 1;
        }

        await output.WriteLineAsync("✓ Environment update complete.", cancellationToken).ConfigureAwait(false);
        return 0;
    }

    // uv then fnm: each is installed from the catalog when missing, then asked for the folder's Python or the Node LTS.
    private async Task<int> RuntimesAsync(bool dryRun, bool quiet, CancellationToken cancellationToken)
    {
        var tools = Tools();
        var bundled = ToolCatalog.Parse(ToolCatalog.BundledText, "the bundled catalog");
        var python = Runtimes.PythonFor(WorkingDirectory);
        var failed = false;
        foreach (var (id, label, argv) in new (string, string, string[])[]
        {
            ("uv", $"Python {python}", ["uv", "python", "install", python]),
            ("fnm", "the Node LTS", ["fnm", "install", "--lts"]),
        })
        {
            var tool = bundled.Single(t => t.Id == id);
            if (!await tools.IsInstalledAsync(tool, cancellationToken).ConfigureAwait(false))
            {
                await output.WriteLineAsync(dryRun ? $"Dry run: would install {tool.Name}" : $"Installing {tool.Name}...", cancellationToken).ConfigureAwait(false);
                if (!dryRun && !await tools.InstallAsync(tool, quiet, cancellationToken).ConfigureAwait(false))
                {
                    await error.WriteLineAsync($"! Failed to install {tool.Name}.", cancellationToken).ConfigureAwait(false);
                    failed = true;
                    continue;
                }
            }

            if (dryRun)
            {
                await output.WriteLineAsync($"Dry run: would ensure {label} with: {string.Join(' ', argv)}", cancellationToken).ConfigureAwait(false);
                continue;
            }

            await output.WriteLineAsync($"Ensuring {label} with {argv[0]}...", cancellationToken).ConfigureAwait(false);
            var ran = await proc.RunAsync(new ProcRequest(argv) { Timeout = TimeSpan.FromMinutes(10) }, cancellationToken).ConfigureAwait(false);
            if (ran.Ok)
            {
                await output.WriteLineAsync($"✓ {label} is available.", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await error.WriteLineAsync($"! {string.Join(' ', argv)} exited {ran.Code}: {ran.Err.Trim()}", cancellationToken).ConfigureAwait(false);
                failed = true;
            }
        }

        return failed ? 1 : 0;
    }
}

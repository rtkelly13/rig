using System.CommandLine;
using Rig.Core.Devenv;
using Rig.Core.Processes;

namespace Rig.Cli.Devenv;

/// <summary>
/// <c>rig devenv tools</c>, <c>runtimes</c> and <c>update</c>: the developer tool catalog, checked and installed through
/// the platform's package manager. Ported from devenv's <c>steps/tools.py</c>, <c>steps/runtimes.py</c>,
/// <c>tool_manager.py</c> and <c>providers</c>.
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
        var upgrade = new Option<bool>("--update", "-u") { Description = "Also upgrade every package the platform package manager owns (brew upgrade / winget upgrade --all)." };
        var tools = new Command("tools", "Check the developer tool catalog and install the missing tools.") { dryRun, home, mode, interactive, check, upgrade };
        tools.SetAction((parsed, cancellationToken) => ToolsAsync(
            parsed.GetValue(home)!,
            parsed.GetValue(mode)!,
            parsed.GetValue(dryRun),
            !parsed.GetValue(interactive),
            parsed.GetValue(check),
            parsed.GetValue(upgrade),
            cancellationToken));
        var runtimes = new Command("runtimes", "Install uv and fnm, then the folder's Python through uv and the Node LTS through fnm.") { dryRun, interactive };
        runtimes.SetAction((parsed, cancellationToken) => RuntimesAsync(parsed.GetValue(dryRun), !parsed.GetValue(interactive), cancellationToken));
        var update = new Command("update", "Upgrade every package the platform package manager owns. Skills are updated by `rig skills update`.");
        update.SetAction((_, cancellationToken) => UpdateAsync(cancellationToken));
        return [tools, runtimes, update];
    }

    private string WorkingDirectory => host.WorkingDirectory ?? host.Home;

    private DevTools Tools() => new(PackageManager.For(host.Windows, host.Find, proc), host.Find);

    private async Task<int> ToolsAsync(string home, string mode, bool dryRun, bool quiet, bool check, bool upgrade, CancellationToken cancellationToken)
    {
        IReadOnlyList<DevTool> catalog;
        try
        {
            catalog = ToolCatalog.Load(home, WorkingDirectory);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Could not read the tool catalog: {e.Message}").ConfigureAwait(false);
            return 1;
        }

        var tools = Tools();
        var missing = new List<DevTool>();
        await output.WriteLineAsync("Development tools:").ConfigureAwait(false);
        foreach (var tool in catalog)
        {
            var installed = await tools.IsInstalledAsync(tool, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"  {tool.Name,-28} {tool.Category,-10} {(installed ? "installed" : tool.Required ? "missing (required)" : "missing"),-18} {tool.Description}").ConfigureAwait(false);
            if (!installed)
            {
                missing.Add(tool);
            }
        }

        if (check)
        {
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
                await output.WriteLineAsync($"- {tool.Name}: no {manager.Name} package; install it by hand.").ConfigureAwait(false);
            }
            else
            {
                wanted.Add(tool);
            }
        }

        if (wanted.Count == 0)
        {
            await output.WriteLineAsync("✓ All target tools are already installed.").ConfigureAwait(false);
            return false;
        }

        if (dryRun)
        {
            await output.WriteLineAsync($"Dry run: would install {wanted.Count} tool(s): {string.Join(", ", wanted.Select(tool => tool.Name))}.").ConfigureAwait(false);
            return false;
        }

        var failed = false;
        foreach (var tool in wanted)
        {
            await output.WriteLineAsync($"Installing {tool.Name}...").ConfigureAwait(false);
            if (await tools.InstallAsync(tool, quiet, cancellationToken).ConfigureAwait(false))
            {
                await output.WriteLineAsync($"✓ Installed {tool.Name}").ConfigureAwait(false);
            }
            else
            {
                await error.WriteLineAsync($"! Failed to install {tool.Name} via {tools.Manager?.Name ?? "a package manager (none found)"}.").ConfigureAwait(false);
                failed = true;
            }
        }

        return failed;
    }

    private async Task<bool> UpgradeAsync(DevTools tools, bool quiet, CancellationToken cancellationToken)
    {
        if (tools.Manager is null)
        {
            await error.WriteLineAsync($"Package upgrade did not run: {(host.Windows ? "winget" : "brew")} is not on the PATH.").ConfigureAwait(false);
            return false;
        }

        if (!await tools.Manager.UpgradeAllAsync(quiet, cancellationToken).ConfigureAwait(false))
        {
            await error.WriteLineAsync($"Package upgrade failed: {tools.Manager.Name} exited non-zero.").ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private async Task<int> UpdateAsync(CancellationToken cancellationToken)
    {
        await output.WriteLineAsync("Updating development tools...").ConfigureAwait(false);
        if (!await UpgradeAsync(Tools(), quiet: false, cancellationToken).ConfigureAwait(false))
        {
            return 1;
        }

        await output.WriteLineAsync("✓ Environment update complete.").ConfigureAwait(false);
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
                await output.WriteLineAsync(dryRun ? $"Dry run: would install {tool.Name}" : $"Installing {tool.Name}...").ConfigureAwait(false);
                if (!dryRun && !await tools.InstallAsync(tool, quiet, cancellationToken).ConfigureAwait(false))
                {
                    await error.WriteLineAsync($"! Failed to install {tool.Name}.").ConfigureAwait(false);
                    failed = true;
                    continue;
                }
            }

            if (dryRun)
            {
                await output.WriteLineAsync($"Dry run: would ensure {label} with: {string.Join(' ', argv)}").ConfigureAwait(false);
                continue;
            }

            await output.WriteLineAsync($"Ensuring {label} with {argv[0]}...").ConfigureAwait(false);
            var ran = await proc.RunAsync(new ProcRequest(argv) { Timeout = TimeSpan.FromMinutes(10) }, cancellationToken).ConfigureAwait(false);
            if (ran.Ok)
            {
                await output.WriteLineAsync($"✓ {label} is available.").ConfigureAwait(false);
            }
            else
            {
                await error.WriteLineAsync($"! {string.Join(' ', argv)} exited {ran.Code}: {ran.Err.Trim()}").ConfigureAwait(false);
                failed = true;
            }
        }

        return failed ? 1 : 0;
    }
}

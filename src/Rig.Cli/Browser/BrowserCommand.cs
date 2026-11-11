using System.CommandLine;
using Rig.Core.Browser;
using Rig.Core.Processes;

namespace Rig.Cli.Browser;

/// <summary>Where <c>rig browser</c> sets things up.</summary>
/// <param name="Home">The default home folder.</param>
/// <param name="Shell">The login shell (<c>SHELL</c>).</param>
/// <param name="Windows">Whether this is Windows.</param>
/// <param name="ProcessEnvironment">Reads this process's environment.</param>
/// <param name="Store">The persisted user-scope store for a home; the registry on Windows, else the shell profile.</param>
internal sealed record BrowserHost(string Home, string? Shell, bool Windows, Func<string, string?> ProcessEnvironment, Func<BrowserPlaces, IUserEnvironment> Store);

/// <summary>
/// <c>rig browser</c>: sets up what the upstream <c>agent-browser</c> CLI reads. Only setup lives here; run the upstream
/// binary directly. Ported from <c>rig.agent_browser.cli</c> and devenv's agent-browser step. Always exits 0.
/// </summary>
/// <param name="host">Where it sets up.</param>
/// <param name="proc">Runs the daemon probe.</param>
/// <param name="output">Where profile progress goes.</param>
/// <param name="error">Where environment progress goes, as the Python printed it.</param>
internal sealed class BrowserCommand(BrowserHost host, IProc proc, TextWriter output, TextWriter error)
{
    /// <summary>The <c>browser</c> command.</summary>
    public Command Build()
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "Show planned changes without writing." };
        var force = new Option<bool>("--force") { Description = "Overwrite already-set variables (ignores never-clobber)." };
        var home = new Option<string>("--home") { Description = "Home folder to set up (default: yours).", DefaultValueFactory = _ => host.Home };
        var setup = new Command(
            "setup",
            "Provision user-scoped agent-browser environment variables (AGENT_BROWSER_PROFILE, AGENT_BROWSER_AUTO_CONNECT, AGENT_BROWSER_HEADED), never clobbering one already set.")
        {
            dryRun, force, home,
        };
        setup.SetAction((parsed, ct) => SetupAsync(parsed.GetValue(dryRun), parsed.GetValue(force), parsed.GetValue(home)!, ct));

        var profiles = new Command("profiles", "Create the dedicated agent-browser profile folders and per-profile shell shortcuts.") { dryRun, home };
        profiles.SetAction(parsed => Profiles(parsed.GetValue(dryRun), parsed.GetValue(home)!));

        return new Command("browser", "Set up the environment the upstream agent-browser CLI reads.") { setup, profiles };
    }

    private BrowserSetup For(string home)
    {
        var places = new BrowserPlaces(home, host.Shell, host.Windows);
        return new BrowserSetup(places, host.ProcessEnvironment, host.Store(places), proc);
    }

    private async Task<int> SetupAsync(bool dryRun, bool force, string home, CancellationToken cancellationToken)
    {
        var outcome = await For(home).ApplyEnvironmentAsync(dryRun, force, error.WriteLine, cancellationToken).ConfigureAwait(false);
        if (outcome is { Skipped: > 0, Applied: > 0 })
        {
            await error.WriteLineAsync($"{outcome.Skipped} var(s) already set — left untouched.").ConfigureAwait(false);
        }

        return 0;
    }

    private int Profiles(bool dryRun, string home)
    {
        For(home).ProvisionProfiles(dryRun, output.WriteLine);
        return 0;
    }
}

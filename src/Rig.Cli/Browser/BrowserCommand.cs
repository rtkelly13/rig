using System.CommandLine;
using DotMake.CommandLine;
using Rig.Core;
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
/// binary directly. Always exits 0.
/// </summary>
/// <param name="host">Where it sets up.</param>
/// <param name="proc">Runs the daemon probe.</param>
/// <param name="output">Where profile progress goes.</param>
/// <param name="error">Where environment progress goes.</param>
[CliCommand(Name = "browser", Description = "Set up the environment the upstream agent-browser CLI reads.")]
internal sealed class BrowserCommand(BrowserHost host, IProc proc, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal BrowserCommand()
        : this(null!, null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The <c>browser</c> command.</summary>
    public Command Build()
    {
        var browser = DeclarativeCommand.BuildGroup(this);
        browser.Add(DeclarativeCommand.Build(new SetupCommand(host, proc, error)).WithDefault("--home", () => host.Home));
        browser.Add(DeclarativeCommand.Build(new ProfilesCommand(host, proc, output)).WithDefault("--home", () => host.Home));
        return browser;
    }

    private static BrowserSetup For(BrowserHost host, IProc proc, string home)
    {
        var places = new BrowserPlaces(home, host.Shell, host.Windows);
        return new BrowserSetup(places, host.ProcessEnvironment, host.Store(places), proc);
    }

    /// <summary><c>rig browser setup</c>.</summary>
    [CliCommand(
        Name = "setup",
        Description = "Provision user-scoped agent-browser environment variables (AGENT_BROWSER_PROFILE, AGENT_BROWSER_AUTO_CONNECT, AGENT_BROWSER_HEADED), never clobbering one already set.")]
    internal sealed class SetupCommand(BrowserHost host, IProc proc, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal SetupCommand()
            : this(null!, null!, TextWriter.Null)
        {
        }

        /// <summary>Show planned changes without writing.</summary>
        [CliOption(Name = "--dry-run", Description = "Show planned changes without writing.")]
        public bool DryRun { get; set; }

        /// <summary>Overwrite already-set variables.</summary>
        [CliOption(Name = "--force", Description = "Overwrite already-set variables (ignores never-clobber).")]
        public bool Force { get; set; }

        /// <summary>The home folder to set up.</summary>
        [CliOption(Name = "--home", Required = false, Description = "Home folder to set up (default: yours).")]
        public string Home { get; set; } = null!;

        /// <summary>Applies the environment, saying what it left alone.</summary>
        public async Task<int> RunAsync(CliContext context)
        {
            var outcome = await For(host, proc, Home).ApplyEnvironmentAsync(DryRun, Force, error.WriteLine, context.CancellationToken).ConfigureAwait(false);
            if (outcome is { Skipped: > 0, Applied: > 0 })
            {
                await error.WriteLineAsync($"{outcome.Skipped} var(s) already set — left untouched.").ConfigureAwait(false);
            }

            return (int)ExitCode.Clean;
        }
    }

    /// <summary><c>rig browser profiles</c>.</summary>
    [CliCommand(Name = "profiles", Description = "Create the dedicated agent-browser profile folders and per-profile shell shortcuts.")]
    internal sealed class ProfilesCommand(BrowserHost host, IProc proc, TextWriter output)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal ProfilesCommand()
            : this(null!, null!, TextWriter.Null)
        {
        }

        /// <summary>Show planned changes without writing.</summary>
        [CliOption(Name = "--dry-run", Description = "Show planned changes without writing.")]
        public bool DryRun { get; set; }

        /// <summary>The home folder to set up.</summary>
        [CliOption(Name = "--home", Required = false, Description = "Home folder to set up (default: yours).")]
        public string Home { get; set; } = null!;

        /// <summary>Creates the profile folders.</summary>
        public int Run()
        {
            For(host, proc, Home).ProvisionProfiles(DryRun, output.WriteLine);
            return (int)ExitCode.Clean;
        }
    }
}

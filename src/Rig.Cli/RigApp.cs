using System.CommandLine;
using Rig.Cli.Agent;
using Rig.Cli.Forwarding;
using Rig.Cli.Gh;
using Rig.Cli.Gh.Threads;
using Rig.Core;
using Rig.Core.Processes;

namespace Rig.Cli;

/// <summary>
/// The rig command line. An agent launch (a bare <c>rig</c>, a shortcut flag such as <c>-ccc</c>, or <c>rig agent</c>)
/// goes to the launcher unparsed, so the agent gets its arguments untouched. A line the Python CLI still owns is handed
/// to it whole; everything else (help, version and every ported command) is parsed here. Exit codes follow <see cref="ExitCode"/>: a line that does not parse is 2.
/// </summary>
internal sealed class RigApp(
    PythonRig python,
    IGitHubApi github,
    OriginReader origin,
    TextWriter output,
    TextWriter error,
    IReadOnlyList<Func<Command>>? ported = null,
    AgentLauncher? agent = null,
    IReadOnlyList<Func<Command>>? gh = null)
{
    /// <summary>What <c>rig --help</c> says rig is.</summary>
    public const string Description =
        "Developer rig: workspace management, AI agents and estate tooling. Commands marked (Python) still run in Python.";

    /// <summary>The command, and the <c>help</c> entry point, that print the overview.</summary>
    public const string HelpCommand = "help";

    /// <summary>The overview's last line: the desktop tools are a separate CLI with their own help.</summary>
    public const string DesktopPointer = "Desktop windows, sessions, tabs and jobs: run 'desktop-manager --help'.";

    /// <summary>The root command: the ported commands, plus the forwarded ones so help lists them all.</summary>
    public RootCommand BuildRoot()
    {
        var root = new RootCommand(Description);
        var group = new Command("gh", "Fill gaps in the GitHub CLI: wait for a pull request to land, handle its review threads.");
        group.Subcommands.Add(new ThreadsCommand(new ThreadsGitHub(github), origin, output, error).Build());
        foreach (var build in gh ?? [])
        {
            group.Subcommands.Add(build());
        }

        root.Subcommands.Add(group);
        foreach (var build in ported ?? [])
        {
            root.Subcommands.Add(build());
        }

        if (agent is not null)
        {
            // Listed for help only: RunAsync sends every agent line to the launcher before parsing.
            root.Subcommands.Add(new Command(AgentLauncher.CommandName, AgentLauncher.Description));
        }

        var help = new Command(HelpCommand, "The rig overview: every command, then where the desktop tools are listed.");
        help.SetAction(async (_, cancellationToken) =>
        {
            var code = await root.Parse(["--help"]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, cancellationToken)
                .ConfigureAwait(false);
            await output.WriteLineAsync(DesktopPointer).ConfigureAwait(false);
            return code;
        });
        root.Subcommands.Add(help);

        // A group partly ported is already registered: the rest of its lines are handed over by Forwarded.Owns.
        foreach (var (name, description, _) in Forwarded.Commands.Where(c => !root.Subcommands.Any(s => s.Name == c.Name)))
        {
            root.Subcommands.Add(new Command(name, $"{description} (Python)"));
        }

        return root;
    }

    /// <summary>Whether <paramref name="args"/> go to another process sharing this console: the Python CLI or an agent.</summary>
    public bool HandsOver(IReadOnlyList<string> args) =>
        Forwarded.Owns(args, BuildRoot()) || (agent is not null && AgentLauncher.LauncherArgs(args) is not null);

    /// <summary>Runs one command line and returns its exit code.</summary>
    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (agent is not null && AgentLauncher.LauncherArgs(args) is { } launch)
        {
            // agent models lists the models the agents run: the ported models command.
            return launch.Count > 0 && launch[0] is "models" or "--models"
                ? await RunAsync(["models", .. launch.Skip(1)], cancellationToken).ConfigureAwait(false)
                : await agent.RunAsync(launch).ConfigureAwait(false);
        }

        var root = BuildRoot();
        if (Forwarded.Owns(args, root))
        {
            return await python.RunAsync(args, error).ConfigureAwait(false);
        }

        var parsed = root.Parse(args);
        if (parsed.Errors.Count > 0)
        {
            foreach (var problem in parsed.Errors)
            {
                await error.WriteLineAsync($"rig: {problem.Message}").ConfigureAwait(false);
            }

            await error.WriteLineAsync("Run 'rig --help' for the commands.").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        var configuration = new InvocationConfiguration { Output = output, Error = error };
        return await parsed.InvokeAsync(configuration, cancellationToken).ConfigureAwait(false);
    }
}

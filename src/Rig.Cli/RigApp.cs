#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using System.Diagnostics;
using Rig.Cli.Agent;
using Rig.Cli.Gh;
using Rig.Cli.Gh.Threads;
using Rig.Core;
using Rig.Core.Processes;

namespace Rig.Cli;

/// <summary>
/// The rig command line. An agent launch (a bare <c>rig</c>, a shortcut flag such as <c>-ccc</c>, or <c>rig agent</c>)
/// goes to the launcher unparsed, so the agent gets its arguments untouched; everything else (help, version and every
/// command) is parsed here. Exit codes follow <see cref="ExitCode"/>: a line that does not parse is 2.
/// </summary>
internal sealed class RigApp(
    IGitHubApi github,
    OriginReader origin,
    TextWriter output,
    TextWriter error,
    IReadOnlyList<Func<Command>>? commands = null,
    AgentLauncher? agent = null,
    IReadOnlyList<Func<Command>>? gh = null)
{
    /// <summary>The trace source the root command span is recorded under.</summary>
    public static readonly ActivitySource Source = new("Rig.Cli");

    /// <summary>What <c>rig --help</c> says rig is.</summary>
    public const string Description =
        "Developer rig: workspace management, AI agents and estate tooling.";

    /// <summary>The command, and the <c>help</c> entry point, that print the overview.</summary>
    public const string HelpCommand = "help";

    /// <summary>The root command: every command, plus the agent launcher so help lists it.</summary>
    public RootCommand BuildRoot()
    {
        var root = new RootCommand(Description);
        NotedCommand.AddToHelp(root);
        var group = new Command("gh", "Fill gaps in the GitHub CLI: land a pull request and wait for it (pr), handle its review threads (threads).");
        group.Subcommands.Add(new ThreadsCommand(new ThreadsGitHub(github), origin, output, error).Build());
        foreach (var build in gh ?? [])
        {
            group.Subcommands.Add(build());
        }

        root.Subcommands.Add(group);
        foreach (var build in commands ?? [])
        {
            root.Subcommands.Add(build());
        }

        if (agent is not null)
        {
            // Listed for help only: RunAsync sends every agent line to the launcher before parsing.
            root.Subcommands.Add(new Command(AgentLauncher.CommandName, AgentLauncher.Description));
        }

        var help = new Command(HelpCommand, "The rig overview: every command and what it does.");
        help.SetAction((_, cancellationToken) =>
            root.Parse(["--help"]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, cancellationToken));
        root.Subcommands.Add(help);
        return root;
    }

    /// <summary>Whether <paramref name="args"/> go to an agent, another process sharing this console.</summary>
    public bool HandsOver(IReadOnlyList<string> args) => agent is not null && AgentLauncher.LauncherArgs(args) is not null;

    /// <summary>Runs one command line and returns its exit code, under a root span naming the command.</summary>
    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        using var activity = Source.StartActivity($"rig {CommandName(args)}".Trim());
        activity?.SetTag("cli.argv", args.ToArray());
        activity?.SetTag("cli.cwd", Environment.CurrentDirectory);
        activity?.SetTag("process.cwd", Environment.CurrentDirectory);

        var code = await RunCommandAsync(args, cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            activity?.SetTag("process.exit.code", code);
            activity?.SetStatus(ActivityStatusCode.Error);
        }

        return code;
    }

    // A bare rig, an agent shortcut flag or `rig agent` names the launcher's span; every other line names its command.
    private static string CommandName(IReadOnlyList<string> args) =>
        args.Count == 0 || args[0] == AgentLauncher.CommandName || AgentLauncher.ShortcutFlags.Contains(args[0])
            ? AgentLauncher.CommandName
            : args[0];

    private async Task<int> RunCommandAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (agent is not null && AgentLauncher.LauncherArgs(args) is { } launch)
        {
            // agent models lists the models the agents run: the models command.
            return launch.Count > 0 && launch[0] is "models" or "--models"
                ? await RunCommandAsync(["models", .. launch.Skip(1)], cancellationToken).ConfigureAwait(false)
                : await agent.RunAsync(launch).ConfigureAwait(false);
        }

        var parsed = BuildRoot().Parse(args);
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

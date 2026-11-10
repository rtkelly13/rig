using System.CommandLine;
using Rig.Cli.Forwarding;
using Rig.Cli.Gh.Threads;
using Rig.Core;
using Rig.Core.Processes;

namespace Rig.Cli;

/// <summary>
/// The rig command line. A line the Python CLI still owns is handed to it whole; everything else (help, version and
/// every ported command) is parsed here. Exit codes follow <see cref="ExitCode"/>: a line that does not parse is 2.
/// </summary>
internal sealed class RigApp(
    PythonRig python, IGitHubApi github, OriginReader origin, TextWriter output, TextWriter error, IReadOnlyList<Func<Command>>? ported = null)
{
    /// <summary>What <c>rig --help</c> says rig is.</summary>
    public const string Description =
        "Developer rig: workspace management, AI agents and estate tooling. Commands marked (Python) still run on the Python CLI.";

    /// <summary>The root command: the ported commands, plus the forwarded ones so help lists them all.</summary>
    public RootCommand BuildRoot()
    {
        var root = new RootCommand(Description);
        root.Subcommands.Add(new ThreadsCommand(new ThreadsGitHub(github), origin, output, error).Build());
        foreach (var build in ported ?? [])
        {
            root.Subcommands.Add(build());
        }

        foreach (var (name, description) in Forwarded.Commands)
        {
            root.Subcommands.Add(new Command(name, $"{description} (Python)"));
        }

        return root;
    }

    /// <summary>Runs one command line and returns its exit code.</summary>
    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (Forwarded.Owns(args))
        {
            return await python.RunAsync(args, error).ConfigureAwait(false);
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

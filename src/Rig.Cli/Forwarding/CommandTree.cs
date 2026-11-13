using System.CommandLine;
using System.Text.Json;
using Rig.Cli.Agent;
using Rig.Core.Jobs;

namespace Rig.Cli.Forwarding;

/// <summary>Exports the composed, executable command tree without invoking any actions.</summary>
internal static class CommandTree
{
    /// <summary>One actual registration; groups with actions are included as independently callable capabilities.</summary>
    internal sealed record Registration(string Path, bool Action, string[] Aliases, bool Forwarded);

    /// <summary>Inspect the same tree and forwarding decision used by production dispatch.</summary>
    public static Registration[] Read(Command root) => Walk(root, []).ToArray();

    /// <summary>Machine-readable inspection, reserved for development/CI.</summary>
    public static string Export(Command root, IReadOnlyList<string[]> probes, bool windows) => JsonSerializer.Serialize(new
    {
        commands = Read(root),
        forwarding = Forwarded.Commands.Select(c => new { c.Name, c.Launch }),
        bare = Forwarded.RunsBare,
        dispatch = probes.Select(args => new { Path = string.Join(' ', args), Forwarded = Forwarded.Owns(args, root) }),
        rawAliases = AgentLauncher.ShortcutFlags.Where(flag => AgentLauncher.LauncherArgs([flag]) is not null)
            .Select(flag => new { Path = flag, Target = "agent" })
            .Concat([new { Path = "<bare>", Target = "agent" }, new { Path = "agent models", Target = "models" }, new { Path = "agent --models", Target = "models" }]),
        agentTargets = AgentLauncher.Agents.Select(agent => $"{agent.Id}={agent.Executable}"),
        jobs = JobRegistry.Default(new JobsEnvironment("/scripts", "/plan", "/workspace", "/code/personal", [], "/feed", "/orca", "/chrome.log", windows))
            .Select(j => $"{j.Name}={string.Join(' ', j.Argv)}"),
    });

    private static IEnumerable<Registration> Walk(Command command, string[] parents)
    {
        foreach (var child in command.Subcommands)
        {
            var path = parents.Append(child.Name).ToArray();
            yield return new Registration(string.Join(' ', path), child.Action is not null, child.Aliases.ToArray(), Forwarded.Owns(path, Root(command)));
            foreach (var row in Walk(child, path))
            {
                yield return row;
            }
        }
    }

    private static Command Root(Command command) => command.Parents.OfType<Command>().FirstOrDefault() is { } parent ? Root(parent) : command;
}

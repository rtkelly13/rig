#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ModelContextProtocol.Server;
using Rig.Cli.Agent;
using Rig.Core.Jobs;
using Rig.Web.Mcp;

namespace Rig.Cli.Forwarding;

/// <summary>Exports the composed, executable command tree without invoking any actions.</summary>
internal static class CommandTree
{
    /// <summary>One actual registration; groups with actions are included as independently callable capabilities.</summary>
    internal sealed record Registration(string Path, bool Action, string[] Aliases);

    /// <summary>Inspect the same tree production dispatch parses.</summary>
    public static Registration[] Read(Command root) => Walk(root, []).ToArray();

    /// <summary>Machine-readable inspection, reserved for development/CI.</summary>
    public static string Export(Command root, OSPlatform platform) => JsonSerializer.Serialize(new
    {
        commands = Read(root),
        rawAliases = AgentLauncher.ShortcutFlags.Where(flag => AgentLauncher.LauncherArgs([flag]) is not null)
            .Select(flag => new { Path = flag, Target = "agent" })
            .Concat([new { Path = "<bare>", Target = "agent" }, new { Path = "agent models", Target = "models" }, new { Path = "agent --models", Target = "models" }]),
        agentTargets = AgentLauncher.Agents.Select(agent => $"{agent.Id}={agent.Executable}"),
        jobs = JobRegistry.Default(new JobsEnvironment("/scripts", "/plan", "/workspace", "/code/personal", [], "/feed", "/orca") { Platform = platform })
            .Select(j => $"{j.Name}={string.Join(' ', j.Argv)}"),
        mcpTools = typeof(RigOpsTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .Order(StringComparer.Ordinal),
    });

    private static IEnumerable<Registration> Walk(Command command, string[] parents)
    {
        foreach (var child in command.Subcommands)
        {
            var path = parents.Append(child.Name).ToArray();
            yield return new Registration(string.Join(' ', path), child.Action is not null, child.Aliases.ToArray());
            foreach (var row in Walk(child, path))
            {
                yield return row;
            }
        }
    }
}

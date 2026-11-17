#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;

namespace Rig.Cli.Forwarding;

/// <summary>
/// The commands still written in Python. Each row names the Python program that runs it, started inside the rig
/// checkout's uv environment. Porting a command registers it in C# and removes its row; when the table is empty the
/// forwarding goes too.
/// </summary>
/// <remarks>
/// A row never launches the <c>rig</c> script: that is this CLI, so forwarding to it would loop.
/// </remarks>
internal static class Forwarded
{
    /// <summary>Each Python command, the one-line help it shows in <c>rig --help</c>, and the program that runs it.</summary>
    public static readonly IReadOnlyList<(string Name, string Description, string[] Launch)> Commands =
    [
    ];

    private static readonly Dictionary<string, string[]> Launches = Commands.ToDictionary(c => c.Name, c => c.Launch, StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="args"/> belong to the Python CLI: they start with a command it still owns, and do not go
    /// on to name a subcommand C# runs. The whole line goes over unparsed, so its own help and flags work.
    /// </summary>
    public static bool Owns(IReadOnlyList<string> args, Command root)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(root);
        if (args.Count == 0 || !Launches.ContainsKey(args[0]))
        {
            return false;
        }

        var registered = root.Subcommands.FirstOrDefault(c => c.Name == args[0]);
        var ported = registered?.Subcommands ?? [];
        return args.Count == 1 || !ported.Any(c => c.Name == args[1] || c.Aliases.Contains(args[1]));
    }

    /// <summary>The Python program and arguments that run <paramref name="args"/>, a line <see cref="Owns"/> accepted.</summary>
    public static IReadOnlyList<string> Launch(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return [.. Launches[args[0]], .. args.Skip(1)];
    }
}

using System.Diagnostics.CodeAnalysis;

namespace Rig.Launcher;

/// <summary>
/// <c>rigw</c>: runs any rig command in-process with its arguments unchanged. Built as a Windows app, it opens no
/// console, so a scheduled task that starts it flashes nothing.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "A process entry point that only hands off to rig's, which is tested.")]
internal static class Launcher
{
    private static Task<int> Main(string[] args) => Cli.Program.Main(args);
}

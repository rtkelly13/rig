using System.CommandLine;
using DotMake.CommandLine;
using Rig.Cli.Rendering;
using Rig.Core;
using Rig.Core.Processes;
using Spectre.Console;

namespace Rig.Cli.Status;

/// <summary>One line of the <c>rig status</c> report.</summary>
/// <param name="Name">The check.</param>
/// <param name="Passed">Whether it passed.</param>
/// <param name="Detail">What it found: the branch, the first change, or the last line a failing gate printed.</param>
internal sealed record StatusResult(string Name, bool Passed, string Detail = "");

/// <summary>
/// <c>rig status</c> (and the <c>status</c> entry point): checks the repo is well managed — git state, and the C#
/// tests when asked. CI runs the C# analyzers and format checks. Exit codes: 0 every check passed, 1
/// something did not.
/// </summary>
/// <param name="proc">Runs the tests.</param>
/// <param name="git">Runs git, hardened under untrusted folders.</param>
/// <param name="workingDirectory">The folder the command was run from; the repo is the one around it.</param>
/// <param name="console">Where the verdict and check table go.</param>
[CliCommand(Name = "status", Description = "Check the repo is well-managed: git state, and the tests with --tests.")]
internal sealed class StatusCommand(IProc proc, Git git, string workingDirectory, IAnsiConsole console)
{
    /// <summary>The instance DotMake binds the options onto; rig always builds the wired one above instead.</summary>
    internal StatusCommand()
        : this(null!, null!, string.Empty, TextWriter.Null)
    {
    }

    /// <summary>The same command over a plain writer, wrapped in the machine-safe console.</summary>
    /// <param name="proc">Runs the tests.</param>
    /// <param name="git">Runs git, hardened under untrusted folders.</param>
    /// <param name="workingDirectory">The folder the command was run from.</param>
    /// <param name="output">Where the report goes.</param>
    public StatusCommand(IProc proc, Git git, string workingDirectory, TextWriter output)
        : this(proc, git, workingDirectory, RigConsole.Create(output))
    {
    }

    private const int DetailLength = 100;

    /// <summary>Also run the test suite.</summary>
    [CliOption(Name = "--tests", Aliases = ["-t"], Description = "Also run the C# test suite (dotnet test).")]
    public bool Tests { get; set; }

    /// <summary>The <c>rig status</c> command.</summary>
    public Command Build() => DeclarativeCommand.Build(this);

    /// <summary>The last non-blank line of <paramref name="text"/>, cut to 100 characters.</summary>
    public static string LastLine(string text)
    {
        var last = text.ReplaceLineEndings("\n").Split('\n').LastOrDefault(line => line.Trim().Length > 0) ?? string.Empty;
        return last[..Math.Min(last.Length, DetailLength)];
    }

    /// <summary>The report as machine-safe text: a verdict line, then one row per check.</summary>
    public static string Render(IReadOnlyList<StatusResult> results)
    {
        return $"{(results.All(r => r.Passed) ? "Healthy" : "Issues Found")}\n{Table(results).Render()}";
    }

    /// <summary>The handler: the repo report.</summary>
    public async Task<int> RunAsync(CliContext context)
    {
        var cancellationToken = context.CancellationToken;
        var top = (await git.RunAsync(workingDirectory, ["rev-parse", "--show-toplevel"], cancellationToken: cancellationToken).ConfigureAwait(false)).OutOrEmpty;
        var root = top.Length > 0 ? top : workingDirectory;

        List<StatusResult> results = [.. await GitResultsAsync(root, cancellationToken).ConfigureAwait(false)];
        if (Tests)
        {
            var (ok, text) = await RunInAsync(root, ["dotnet", "test", "--solution", "rig.slnx"], cancellationToken).ConfigureAwait(false);
            results.Add(new StatusResult("Tests", ok, LastLine(text)));
        }

        var healthy = results.All(r => r.Passed);
        console.RenderVerdict(
            healthy ? "Healthy" : "Issues Found",
            healthy,
            healthy ? $"{results.Count} checks passed" : $"{results.Count(r => !r.Passed)} of {results.Count} checks need attention");
        Table(results).RenderTo(console);
        return (int)(healthy ? ExitCode.Clean : ExitCode.Drift);
    }

    private static TextTable Table(IReadOnlyList<StatusResult> results)
    {
        var table = new TextTable(null, [("", false), ("check", false), ("detail", false)]);
        foreach (var result in results)
        {
            _ = table.Add(result.Passed ? "OK" : "!!", result.Name, result.Detail);
        }

        return table;
    }

    private async Task<IReadOnlyList<StatusResult>> GitResultsAsync(string root, CancellationToken cancellationToken)
    {
        List<StatusResult> results = [new("Branch", true, await GitTextAsync(root, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false))];

        var porcelain = await GitTextAsync(root, ["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
        var dirty = porcelain.Length > 0;
        results.Add(new StatusResult("Clean worktree", !dirty, dirty ? porcelain.Split('\n')[0].Trim() : string.Empty));

        var counts = await GitTextAsync(root, ["rev-list", "--count", "--left-right", "HEAD...@{u}"], cancellationToken).ConfigureAwait(false);
        if (counts.Split('\t', 2) is [var ahead, var behind])
        {
            var synced = ahead == "0" && behind == "0";
            results.Add(new StatusResult("Remote sync", synced, synced ? "in sync" : $"+{ahead}/-{behind}"));
        }

        return results;
    }

    // A failing git command still reports what it printed: stdout then stderr, trimmed.
    private async Task<string> GitTextAsync(string root, string[] args, CancellationToken cancellationToken)
    {
        var ran = await git.RunAsync(root, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        return (ran.Out + ran.Err).Trim();
    }

    private async Task<(bool Ok, string Text)> RunInAsync(string root, string[] argv, CancellationToken cancellationToken)
    {
        var ran = await proc.RunAsync(new ProcRequest(argv) { WorkingDirectory = root }, cancellationToken).ConfigureAwait(false);
        return (ran.Ok, (ran.Out + ran.Err).Trim());
    }
}

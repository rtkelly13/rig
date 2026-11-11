using System.CommandLine;
using Rig.Core;
using Rig.Core.Processes;

namespace Rig.Cli.Status;

/// <summary>One line of the <c>rig status</c> report.</summary>
/// <param name="Name">The check.</param>
/// <param name="Passed">Whether it passed.</param>
/// <param name="Detail">What it found: the branch, the first change, or the last line a failing gate printed.</param>
internal sealed record StatusResult(string Name, bool Passed, string Detail = "");

/// <summary>
/// <c>rig status</c> (and the <c>status</c> entry point): checks the repo is well managed — git state, then each
/// quality gate, and the tests when asked. Ported from <c>repo_health.cli</c>. Exit codes: 0 every check passed, 1
/// something did not.
/// </summary>
/// <param name="proc">Runs the gates.</param>
/// <param name="git">Runs git, hardened under untrusted folders.</param>
/// <param name="workingDirectory">The folder the command was run from; the repo is the one around it.</param>
/// <param name="output">Where the report goes.</param>
internal sealed class StatusCommand(IProc proc, Git git, string workingDirectory, TextWriter output)
{
    /// <summary>The quality gates, in report order.</summary>
    public static readonly IReadOnlyList<(string Name, string[] Argv)> Gates =
    [
        ("Lockfile", ["uv", "lock", "--check"]),
        ("Lint", ["uv", "run", "ruff", "check", "src/", "--quiet"]),
        ("Format", ["uv", "run", "ruff", "format", "src/", "--check", "--quiet"]),
        ("Types", ["uv", "run", "basedpyright"]),
        ("ty", ["uv", "run", "ty", "check", "src", "packages/type-ratchet/src"]),
        ("Ratchet", ["uv", "run", "ratchet"]),
        ("Imports", ["uv", "run", "lint-imports"]),
        ("Security", ["uv", "run", "bandit", "-r", "src/", "-q", "-ll"]),
    ];

    private const int DetailLength = 100;

    /// <summary>The <c>rig status</c> command.</summary>
    public Command Build()
    {
        var tests = new Option<bool>("--tests", "-t") { Description = "Also run the test suite." };
        var ciOnly = new Option<bool>("--ci") { Description = "Run only tests marked @pytest.mark.ci." };
        var status = new Command("status", "Check the repo is well-managed: git state, lint, types, and security.") { tests, ciOnly };
        status.SetAction((parsed, ct) => RunAsync(parsed.GetValue(tests), parsed.GetValue(ciOnly), ct));
        return status;
    }

    /// <summary>The last non-blank line of <paramref name="text"/>, cut to 100 characters.</summary>
    public static string LastLine(string text)
    {
        var last = text.ReplaceLineEndings("\n").Split('\n').LastOrDefault(line => line.Trim().Length > 0) ?? string.Empty;
        return last[..Math.Min(last.Length, DetailLength)];
    }

    /// <summary>The report: a verdict, then one line per check.</summary>
    public static string Render(IReadOnlyList<StatusResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var width = results.Max(r => r.Name.Length);
        var lines = results.Select(r => $"  {(r.Passed ? "OK" : "!!")}  {r.Name.PadRight(width)}  {r.Detail}".TrimEnd());
        return $"{(results.All(r => r.Passed) ? "Healthy" : "Issues Found")}\n{string.Join('\n', lines)}\n";
    }

    private async Task<int> RunAsync(bool tests, bool ciOnly, CancellationToken cancellationToken)
    {
        var top = (await git.RunAsync(workingDirectory, ["rev-parse", "--show-toplevel"], cancellationToken: cancellationToken).ConfigureAwait(false)).OutOrEmpty;
        var root = top.Length > 0 ? top : workingDirectory;

        List<StatusResult> results = [.. await GitResultsAsync(root, cancellationToken).ConfigureAwait(false)];
        foreach (var (name, argv) in Gates)
        {
            var (ok, text) = await RunInAsync(root, argv, cancellationToken).ConfigureAwait(false);
            results.Add(new StatusResult(name, ok, ok ? string.Empty : LastLine(text)));
        }

        if (tests || ciOnly)
        {
            string[] argv = ciOnly ? ["uv", "run", "pytest", "-q", "--tb=no", "-m", "ci"] : ["uv", "run", "pytest", "-q", "--tb=no"];
            var (ok, text) = await RunInAsync(root, argv, cancellationToken).ConfigureAwait(false);
            results.Add(new StatusResult("Tests", ok, LastLine(text)));
        }

        await output.WriteAsync(Render(results)).ConfigureAwait(false);
        return (int)(results.All(r => r.Passed) ? ExitCode.Clean : ExitCode.Drift);
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

    // Python's (out + err).strip(): a failing git command still reports what it printed.
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

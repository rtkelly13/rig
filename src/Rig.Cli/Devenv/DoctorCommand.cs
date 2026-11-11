using System.CommandLine;
using Rig.Core.GitHub;
using Rig.Core.Processes;

namespace Rig.Cli.Devenv;

/// <summary>
/// <c>rig doctor</c> and <c>rig devenv doctor</c>: each devenv area's <c>--check</c>, run in this process through the
/// root command, plus the toolchain, the repository folder and the Claude settings file. Exits 1 when a required part
/// is missing or stale. Ported from devenv's <c>doctor/core.py</c>; the toolchain section now reports the .NET SDK
/// and uv instead of Python, ruff, pyright and pytest.
/// </summary>
/// <param name="host">Where it looks: the home folder, the working directory and the PATH.</param>
/// <param name="proc">Runs git, gh, dotnet and uv.</param>
/// <param name="time">The clock the repository name cache is aged by.</param>
/// <param name="output">Where the report goes.</param>
internal sealed class DoctorCommand(DevenvHost host, IProc proc, TimeProvider time, TextWriter output)
{
    /// <summary>What <c>rig doctor</c> is.</summary>
    public const string Description = "Validate environment health across tools, runtimes, git, fonts, and AI assistants.";

    // The toolchain rig builds and runs with: label and program, asked for --version.
    private static readonly (string Label, string Program)[] Toolchain = [(".NET SDK", "dotnet"), ("uv (Python tools)", "uv")];

    /// <summary>The <c>doctor</c> command.</summary>
    public Command Build()
    {
        var doctor = new Command("doctor", Description);
        doctor.SetAction((parsed, cancellationToken) => RunAsync(parsed.RootCommandResult.Command, cancellationToken));
        return doctor;
    }

    private async Task<int> RunAsync(Command root, CancellationToken cancellationToken)
    {
        var platform = host.Windows ? "windows" : host.MacOS ? "macos" : "linux";
        var issues = 0;
        await output.WriteLineAsync($"\n=== Development Environment Doctor ({platform}) ===\n").ConfigureAwait(false);

        await output.WriteLineAsync("1. Tools & Utilities").ConfigureAwait(false);
        // tools --check exits 1 when a required tool is missing; terminal --check when snapToGrid risks jitter.
        issues += await FailsAsync(root, ["devenv", "tools", "--check"], cancellationToken).ConfigureAwait(false);
        if (host.Windows)
        {
            issues += await FailsAsync(root, ["devenv", "terminal", "--check"], cancellationToken).ConfigureAwait(false);
        }

        await output.WriteLineAsync("\n2. Toolchain").ConfigureAwait(false);
        foreach (var (label, program) in Toolchain)
        {
            await output.WriteLineAsync(await ToolchainRowAsync(label, program, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }

        await output.WriteLineAsync("\n3. Git & SSH Configuration").ConfigureAwait(false);
        issues += await FailsAsync(root, ["devenv", "git", "--check"], cancellationToken).ConfigureAwait(false);
        var resolver = new RepoResolver(new GhApi(proc), RepoResolver.DefaultCacheFile(host.Home), time);
        var folder = await RepoFolderCheck.CheckAsync(new Git(proc, host.CodeRoot), resolver, host.WorkingDirectory ?? host.Home, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"  {"Repository Folder",-20} {FolderRow(folder)}").ConfigureAwait(false);
        if (folder.Status is RepoFolderStatus.FolderRenamed or RepoFolderStatus.FolderMismatch or RepoFolderStatus.RemoteOutdated)
        {
            issues++;
        }

        if (await FailsAsync(root, ["devenv", "gh", "--check"], cancellationToken).ConfigureAwait(false) > 0)
        {
            await output.WriteLineAsync("  Run 'rig devenv gh' to install it.").ConfigureAwait(false);
            issues++;
        }

        // Informational: a missing font is reported but is not an issue.
        await output.WriteLineAsync("\n4. Developer Fonts").ConfigureAwait(false);
        _ = await FailsAsync(root, ["devenv", "fonts", "--check"], cancellationToken).ConfigureAwait(false);

        await output.WriteLineAsync("\n5. AI Assistants & Config").ConfigureAwait(false);
        var seeded = File.Exists(Path.Join(host.Home, ".claude", "settings.json"));
        await output.WriteLineAsync($"  {"Claude Code Settings",-20} {(seeded ? "✓ Configured" : "- Not seeded")}").ConfigureAwait(false);
        if (await FailsAsync(root, ["devenv", "claude", "--check"], cancellationToken).ConfigureAwait(false) > 0)
        {
            await output.WriteLineAsync("  Run 'rig devenv claude' to install the session-ledger hook.").ConfigureAwait(false);
            issues++;
        }

        _ = await FailsAsync(root, ["devenv", "agents"], cancellationToken).ConfigureAwait(false);

        if (issues == 0)
        {
            await output.WriteLineAsync("\n✓ All required development components are healthy!").ConfigureAwait(false);
            return 0;
        }

        await output.WriteLineAsync($"\n! Found {issues} missing required component(s). Run 'rig devenv setup' to resolve.").ConfigureAwait(false);
        return 1;
    }

    // 1 when the line exits non-zero, so the caller can add it to the issues.
    private async Task<int> FailsAsync(Command root, string[] line, CancellationToken cancellationToken) =>
        await root.Parse(line).InvokeAsync(new InvocationConfiguration { Output = output, Error = output }, cancellationToken).ConfigureAwait(false) == 0 ? 0 : 1;

    private async Task<string> ToolchainRowAsync(string label, string program, CancellationToken cancellationToken)
    {
        if (host.Find(program) is not { } path)
        {
            return $"  {label,-20} ✗ Missing";
        }

        var version = await proc.RunAsync(new ProcRequest([program, "--version"]) { Timeout = TimeSpan.FromSeconds(15) }, cancellationToken).ConfigureAwait(false);
        var first = version.OutOrEmpty.Split('\n', 2)[0].Trim();
        return $"  {label,-20} ✓ {(first.Length > 0 ? $"{first} " : string.Empty)}({path})";
    }

    private static string FolderRow(RepoFolderCheck check) => check.Status switch
    {
        RepoFolderStatus.Ok => $"✓ Canonical   {check.FolderName} (matches upstream {check.Canonical!.Owner}/{check.Canonical.CanonicalName})",
        RepoFolderStatus.FolderRenamed => $"! Renamed upstream   folder '{check.FolderName}' -> canonical '{check.Canonical!.CanonicalName}'",
        RepoFolderStatus.FolderMismatch => $"! Mismatch   folder '{check.FolderName}' != upstream '{check.Canonical!.CanonicalName}'",
        RepoFolderStatus.RemoteOutdated => $"! Remote outdated   remote '{check.Remote!.Name}' -> canonical '{check.Canonical!.CanonicalName}'",
        RepoFolderStatus.LocalOnly => $"- Local/non-GitHub   {check.Detail}",
        _ => $"- Unresolved   {check.Detail}",
    };
}

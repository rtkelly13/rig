using System.CommandLine;
using Rig.Cli.Devenv;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Devenv;

/// <summary>
/// <c>rig doctor</c>, over a root of recording stand-ins for the <c>--check</c> lines it runs, a fake process runner
/// and a temp home. Ported from <c>tests/devenv/test_doctor.py</c>.
/// </summary>
public sealed class DoctorCommandTests : IDisposable
{
    private static readonly string[] Stubbed = ["tools", "terminal", "git", "gh", "fonts", "claude", "agents"];

    private readonly TempDir _home = new();
    private readonly List<string> _ran = [];
    private readonly Dictionary<string, int> _exits = new(StringComparer.Ordinal);
    private readonly FakeProc _proc = new();

    public void Dispose() => _home.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Command Recorder(string name)
    {
        var command = new Command(name) { TreatUnmatchedTokensAsErrors = false };
        command.SetAction(parsed =>
        {
            _ran.Add(string.Join(' ', parsed.Tokens.Skip(1).Select(token => token.Value)));
            return _exits.GetValueOrDefault(name);
        });
        return command;
    }

    private async Task<(int Code, string Out)> RunAsync(bool windows = false, Func<string, string?>? find = null, string? workingDirectory = null)
    {
        using var output = new StringWriter();
        var host = new DevenvHost(_home.Path, "/none.json", windows, "/rig", Path.Join(_home.Path, "code"), "/bin/zsh", find ?? (_ => null), WorkingDirectory: workingDirectory ?? _home.Path);
        var devenv = new Command("devenv");
        foreach (var name in Stubbed)
        {
            devenv.Subcommands.Add(Recorder(name));
        }

        var root = new RootCommand { devenv, new DoctorCommand(host, _proc, TimeProvider.System, output).Build() };
        var code = await root.Parse(["doctor"]).InvokeAsync(new InvocationConfiguration { Output = output, Error = output }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task A_healthy_machine_runs_each_check_and_exits_0()
    {
        var (code, output) = await RunAsync();

        Assert.Equal(0, code);
        Assert.Equal(["tools --check", "git --check", "gh --check", "fonts --check", "claude --check", "agents"], _ran);
        Assert.Contains("=== Development Environment Doctor (linux) ===", output, StringComparison.Ordinal);
        Assert.Contains("Repository Folder    - Local/non-GitHub   No remote origin URL configured or not a git repository", output, StringComparison.Ordinal);
        Assert.Contains("Claude Code Settings - Not seeded", output, StringComparison.Ordinal);
        Assert.EndsWith("✓ All required development components are healthy!\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Required_checks_that_fail_are_counted_and_informational_ones_are_not()
    {
        foreach (var name in new[] { "tools", "terminal", "git", "gh", "claude", "fonts", "agents" })
        {
            _exits[name] = 1;
        }

        var (code, output) = await RunAsync(windows: true);

        Assert.Equal(1, code);
        Assert.Contains("terminal --check", _ran);
        Assert.Contains("Run 'rig devenv gh' to install it.", output, StringComparison.Ordinal);
        Assert.Contains("Run 'rig devenv claude' to install the session-ledger hook.", output, StringComparison.Ordinal);
        Assert.EndsWith("! Found 5 missing required component(s). Run 'rig devenv setup' to resolve.\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_toolchain_reports_each_version_and_what_is_missing()
    {
        _ = _proc.Answer(new(0, ".NET SDK 10.0.100\nmore\n", string.Empty), "dotnet", "--version");
        _ = Directory.CreateDirectory(Path.Join(_home.Path, ".claude"));
        await File.WriteAllTextAsync(Path.Join(_home.Path, ".claude", "settings.json"), "{}", Token);

        var (code, output) = await RunAsync(find: program => program == "dotnet" ? "/usr/bin/dotnet" : null);

        Assert.Equal(0, code);
        Assert.Contains("  .NET SDK             ✓ .NET SDK 10.0.100 (/usr/bin/dotnet)\n", output, StringComparison.Ordinal);
        Assert.Contains("  uv (Python tools)    ✗ Missing\n", output, StringComparison.Ordinal);
        Assert.Contains("Claude Code Settings ✓ Configured", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rig", "rig", 0, "✓ Canonical   rig (matches upstream rtkelly13/rig)")]
    [InlineData("shared-utilities", "shared-utilities", 1, "! Renamed upstream   folder 'shared-utilities' -> canonical 'rig'")]
    [InlineData("tripletail", "rig", 1, "! Mismatch   folder 'tripletail' != upstream 'rig'")]
    [InlineData("rig", "shared-utilities", 1, "! Remote outdated   remote 'shared-utilities' -> canonical 'rig'")]
    public async Task The_repository_folder_is_checked_against_its_github_name(string folder, string remote, int code, string row)
    {
        var clone = _home.Folder(folder);
        _ = _proc
            .Answer(new(0, ".git\n", string.Empty), "git", "rev-parse", "--git-common-dir")
            .Answer(new(0, $"git@github.com:rtkelly13/{remote}.git\n", string.Empty), "git", "config", "--get", "remote.origin.url")
            .Answer(new(0, """{"data": {"r0": {"name": "rig", "nameWithOwner": "rtkelly13/rig", "isArchived": false, "url": ""}}}""", string.Empty), "gh", "api");

        var (exit, output) = await RunAsync(workingDirectory: clone);

        Assert.Equal(code, exit);
        Assert.Contains($"Repository Folder    {row}\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repository_github_cannot_resolve_is_reported_but_not_counted()
    {
        _ = _proc
            .Answer(new(0, ".git\n", string.Empty), "git", "rev-parse")
            .Answer(new(0, "https://github.com/rtkelly13/gone\n", string.Empty), "git", "config")
            .Answer(new(1, string.Empty, "Could not resolve"), "gh", "api");

        var (code, output) = await RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("- Unresolved   Could not resolve repository 'rtkelly13/gone' on GitHub", output, StringComparison.Ordinal);
        Assert.Contains(_proc.Requests, request => request.Argv.Take(2).SequenceEqual(["gh", "api"]));
        Assert.DoesNotContain(_proc.Requests, request => request.Argv[0] == "uv" || request.Argv[0] == "dotnet");
    }
}

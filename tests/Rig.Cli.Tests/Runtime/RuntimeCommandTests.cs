using System.CommandLine;
using Rig.Cli.Runtime;
using Rig.Core.Runtime;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Runtime;

/// <summary><c>rig runtime status|install|rollback</c> over a scratch runtime folder and a fake process runner.</summary>
public sealed class RuntimeCommandTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly StringWriter _out = new() { NewLine = "\n" };
    private readonly StringWriter _error = new() { NewLine = "\n" };

    public void Dispose()
    {
        _temp.Dispose();
        _out.Dispose();
        _error.Dispose();
    }

    private string Root => Path.Join(_temp.Path, "runtime");

    [Fact]
    public async Task Status_without_an_install_names_the_command_that_installs_one()
    {
        var exit = await Run(new FakeProc(), "status");

        Assert.Equal(1, exit);
        Assert.Equal($"Nothing installed in {Root}: run `rig runtime install`.\n", _out.ToString());
    }

    [Fact]
    public async Task Status_shows_both_copies_the_link_and_the_distance_to_the_ref()
    {
        _ = Directory.CreateDirectory(Path.Join(Root, "aaaaaaaaaaaa"));
        _ = Directory.CreateDirectory(Path.Join(Root, "bbbbbbbbbbbb"));
        await File.WriteAllTextAsync(
            Path.Join(Root, RuntimeManager.StateName),
            $$"""{"current": "aaaaaaaaaaaa", "previous": "bbbbbbbbbbbb", "repo": {{System.Text.Json.JsonSerializer.Serialize(_temp.Path)}}}""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Join(Root, "aaaaaaaaaaaa", RuntimeManager.InstalledName),
            """{"sha": "aaaaaaaaaaaa", "ref": "origin/main", "repo": "r", "installed_at": "2026-11-14T10:00:00+00:00"}""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Join(Root, "bbbbbbbbbbbb", RuntimeManager.InstalledName),
            """{"sha": "bbbbbbbbbbbb", "ref": "origin/main", "repo": "r", "installed_at": "2026-11-13T10:00:00+00:00"}""",
            TestContext.Current.CancellationToken);
        var proc = new FakeProc().Answer(new(0, "2", ""), "git");

        var exit = await Run(proc, "status");

        Assert.Equal(0, exit);
        Assert.Equal(
            "Current:  aaaaaaaaaaaa  origin/main  2026-11-14T10:00:00+00:00\n"
            + "Previous: bbbbbbbbbbbb  origin/main  2026-11-13T10:00:00+00:00\n"
            + $"Path:     {Path.Join(Root, RuntimeManager.LinkName)}\n"
            + "Behind origin/main: 2 commit(s)\n",
            _out.ToString());
        Assert.Equal(["git", "-C", _temp.Path, "rev-list", "--count", "aaaaaaaaaaaa..origin/main"], Assert.Single(proc.Requests).Argv);
    }

    [Fact]
    public async Task Rollback_without_a_previous_copy_reports_why()
    {
        var exit = await Run(new FakeProc(), "rollback");

        Assert.Equal(1, exit);
        Assert.Equal("Rollback failed: no previous copy to roll back to\n", _out.ToString());
    }

    [Fact]
    public async Task Install_without_a_checkout_names_the_option_to_pass()
    {
        var exit = await Run(new FakeProc(), "install");

        Assert.Equal(2, exit);
        Assert.Equal("No repo to install from: pass --repo <rig checkout>.\n", _out.ToString());
    }

    [Fact]
    public async Task Install_reports_the_step_that_failed()
    {
        var repo = _temp.Folder("repo");

        var exit = await Run(new FakeProc(), "install", "--repo", repo, "--ref", "HEAD", "--no-fetch");

        Assert.Equal(1, exit);
        Assert.StartsWith("Install failed: git -C ", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("rev-parse", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_fails_when_state_is_damaged()
    {
        _ = Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(Path.Join(Root, RuntimeManager.StateName), "not valid json {", TestContext.Current.CancellationToken);

        var exit = await Run(new FakeProc(), "status");

        Assert.Equal(1, exit);
        Assert.Contains("Status failed:", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_succeeds_and_reports_swapped_copies()
    {
        _ = Directory.CreateDirectory(Path.Join(Root, "curr"));
        _ = Directory.CreateDirectory(Path.Join(Root, "prev"));
        await File.WriteAllTextAsync(Path.Join(Root, "curr", RuntimeManager.InstalledName), "{}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(Root, "prev", RuntimeManager.InstalledName), "{}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Join(Root, RuntimeManager.StateName),
            """{"current": "curr", "previous": "prev", "repo": "/repo"}""",
            TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(RuntimeManager.CurrentLink(Root), Path.Join(Root, "curr"));

        var exit = await Run(new FakeProc(), "rollback");

        Assert.Equal(0, exit);
        Assert.Equal("Current: prev (previous: curr)\n", _out.ToString());
    }

    [Fact]
    public async Task Install_already_current_reports_already_current()
    {
        var sha = "1111222233334444555566667777888899990000";
        var shortSha = sha[..RuntimeManager.ShortShaLength];
        var copy = Path.Join(Root, shortSha);
        _ = Directory.CreateDirectory(copy);
        await File.WriteAllTextAsync(Path.Join(copy, RuntimeManager.InstalledName), "{}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Join(Root, RuntimeManager.StateName),
            $$"""{"current": "{{shortSha}}", "previous": null, "repo": "/repo"}""",
            TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(RuntimeManager.CurrentLink(Root), copy);

        var repo = _temp.Folder("repo");
        var proc = new FakeProc().Answer(new(0, sha + "\n", ""), "git");

        var exit = await Run(proc, "install", "--repo", repo, "--ref", "HEAD", "--no-fetch");

        Assert.Equal(0, exit);
        Assert.Contains($"Already current: {shortSha}", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FindCheckout_locates_repo_root_or_returns_null()
    {
        var repo = _temp.Folder("sample-rig-repo");
        var nested = Path.Join(repo, "src", "sub", "deep");
        _ = Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Join(repo, "pyproject.toml"), "[project]\nname = \"rig\"\n");

        var found = RuntimeCommand.FindCheckout(nested);
        Assert.Equal(repo, found);

        var empty = _temp.Folder("not-a-repo");
        Assert.Null(RuntimeCommand.FindCheckout(empty));
    }

    [Fact]
    public async Task Install_discovers_checkout_and_reports_previous_and_removed_copies()
    {
        var repo = _temp.Folder("auto-repo");
        await File.WriteAllTextAsync(Path.Join(repo, "pyproject.toml"), "[project]\nname = \"rig\"\n", TestContext.Current.CancellationToken);
        var sha = "abcdef1234567890abcdef1234567890abcdef12";
        var shortSha = sha[..RuntimeManager.ShortShaLength];

        _ = Directory.CreateDirectory(Path.Join(Root, "prev-copy"));
        _ = Directory.CreateDirectory(Path.Join(Root, "old-copy"));
        await File.WriteAllTextAsync(Path.Join(Root, "prev-copy", RuntimeManager.InstalledName), "{}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(Root, "old-copy", RuntimeManager.InstalledName), "{}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Join(Root, RuntimeManager.StateName),
            """{"current": "prev-copy", "previous": "old-copy", "repo": "/repo"}""",
            TestContext.Current.CancellationToken);

        var proc = new FakeProc()
            .Answer(new(0, sha + "\n", ""), "git", "-C", repo, "rev-parse")
            .Answer(new(0, "", ""), "git", "-C", repo, "archive")
            .Answer(new(0, "", ""), "uv")
            .Answer(new(0, "", ""), "dotnet")
            .Answer(new(0, "", ""));

        var host = new RuntimeHost(Root, repo, _temp.Folder("home"), false, proc, TimeProvider.System);
        var root = new RootCommand
        {
            new RuntimeCommand(host, _out).Build(),
        };

        var exit = await root.Parse(["runtime", "install", "--ref", "HEAD", "--no-fetch"]).InvokeAsync(
            new InvocationConfiguration { Output = _out, Error = _error },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        var output = _out.ToString();
        Assert.Contains($"Current: {shortSha}", output, StringComparison.Ordinal);
        Assert.Contains("Previous kept for rollback: prev-copy", output, StringComparison.Ordinal);
        Assert.Contains("Removed old copy: old-copy", output, StringComparison.Ordinal);
        Assert.Contains("rig on PATH:", output, StringComparison.Ordinal);
    }

    private async Task<int> Run(FakeProc proc, params string[] args)
    {
        var root = new RootCommand
        {
            new RuntimeCommand(new RuntimeHost(Root, _temp.Path, _temp.Path, false, proc, TimeProvider.System), _out).Build(),
        };
        return await root.Parse(["runtime", .. args]).InvokeAsync(
            new InvocationConfiguration { Output = _out, Error = _error },
            TestContext.Current.CancellationToken);
    }
}

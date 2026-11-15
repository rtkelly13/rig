using System.CommandLine;
using System.Formats.Tar;
using Rig.Cli.Runtime;
using Rig.Core.Roots;
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
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symlinks needs developer mode on Windows.");
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
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symlinks needs developer mode on Windows.");
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
        var repo = PathResolution.Resolve(_temp.Folder("auto-repo"));
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

        var proc = new FakeProc
        {
            Unanswered = new(0, "", ""),
            BeforeRun = req =>
            {
                if (req.Argv.Contains("archive"))
                {
                    var outputArg = req.Argv.FirstOrDefault(a => a.StartsWith("--output=", StringComparison.Ordinal));
                    if (outputArg is not null)
                    {
                        var tarPath = outputArg["--output=".Length..];
                        var emptyDir = _temp.Folder("empty-tar-source");
                        _ = Directory.CreateDirectory(Path.GetDirectoryName(tarPath)!);
                        TarFile.CreateFromDirectory(emptyDir, tarPath, false);
                    }
                }
            },
        }
            .Answer(new(0, sha + "\n", ""), "git", "-C", repo, "rev-parse");

        var host = new RuntimeHost(Root, repo, _temp.Folder("home"), false, proc, TimeProvider.System);
        var root = new RootCommand
        {
            new RuntimeCommand(host, _out).Build(),
        };

        var exit = await root.Parse(["runtime", "install", "--ref", "HEAD", "--no-fetch"]).InvokeAsync(
            new InvocationConfiguration { Output = _out, Error = _error },
            TestContext.Current.CancellationToken);

        Assert.True(exit == 0, $"Exit was {exit}. Output:\n{_out}\nError:\n{_error}");
        var output = _out.ToString();
        Assert.Contains($"Current: {shortSha}", output, StringComparison.Ordinal);
        Assert.Contains("Previous kept for rollback: prev-copy", output, StringComparison.Ordinal);
        Assert.Contains("Removed old copy: old-copy", output, StringComparison.Ordinal);
        Assert.Contains("rig on PATH:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FindCheckout_matches_shared_utilities_name()
    {
        var repo = _temp.Folder("shared-utils-repo");
        File.WriteAllText(Path.Join(repo, "pyproject.toml"), "[project]\nname = \"shared-utilities\"\n");
        var found = RuntimeCommand.FindCheckout(repo);
        Assert.Equal(repo, found);
    }

    [Fact]
    public async Task Status_shows_current_without_previous_or_behind()
    {
        _ = Directory.CreateDirectory(Path.Join(Root, "c111c111c111"));
        await File.WriteAllTextAsync(
            Path.Join(Root, RuntimeManager.StateName),
            """{"current": "c111c111c111", "previous": null, "repo": null}""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Join(Root, "c111c111c111", RuntimeManager.InstalledName),
            """{"sha": "c111c111c111", "ref": null, "repo": null, "installed_at": null}""",
            TestContext.Current.CancellationToken);

        var exit = await Run(new FakeProc(), "status");

        Assert.Equal(0, exit);
        Assert.Contains("Current:  c111c111c111  ?  ?", _out.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Previous:", _out.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Behind", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_running_copy_falls_back_to_recorded_repo()
    {
        var repo = PathResolution.Resolve(_temp.Folder("recorded-repo"));
        await File.WriteAllTextAsync(Path.Join(repo, "pyproject.toml"), "[project]\nname = \"rig\"\n", TestContext.Current.CancellationToken);
        _ = Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(
            Path.Join(Root, RuntimeManager.StateName),
            $$"""{"current": null, "previous": null, "repo": {{System.Text.Json.JsonSerializer.Serialize(repo)}}}""",
            TestContext.Current.CancellationToken);

        var sha = "9999999999999999999999999999999999999999";
        var proc = new FakeProc
        {
            Unanswered = new(0, "", ""),
            BeforeRun = req =>
            {
                if (req.Argv.Contains("archive"))
                {
                    var outputArg = req.Argv.FirstOrDefault(a => a.StartsWith("--output=", StringComparison.Ordinal));
                    if (outputArg is not null)
                    {
                        var tarPath = outputArg["--output=".Length..];
                        var emptyDir = _temp.Folder("empty-tar-source-2");
                        _ = Directory.CreateDirectory(Path.GetDirectoryName(tarPath)!);
                        TarFile.CreateFromDirectory(emptyDir, tarPath, false);
                    }
                }
            },
        }
            .Answer(new(0, sha + "\n", ""), "git", "-C", repo, "rev-parse");

        var runningCopyFolder = Path.Join(Root, "running-copy");
        _ = Directory.CreateDirectory(runningCopyFolder);
        var host = new RuntimeHost(Root, runningCopyFolder, _temp.Folder("home"), false, proc, TimeProvider.System);
        var root = new RootCommand
        {
            new RuntimeCommand(host, _out).Build(),
        };

        var exit = await root.Parse(["runtime", "install", "--ref", "HEAD", "--no-fetch"]).InvokeAsync(
            new InvocationConfiguration { Output = _out, Error = _error },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Contains("Current: 999999999999", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_leaves_foreign_shim_alone()
    {
        var repo = PathResolution.Resolve(_temp.Folder("repo-for-shim-test"));
        await File.WriteAllTextAsync(Path.Join(repo, "pyproject.toml"), "[project]\nname = \"rig\"\n", TestContext.Current.CancellationToken);
        var home = _temp.Folder("home-foreign");
        var shimDir = Path.Join(home, ".local", "bin");
        _ = Directory.CreateDirectory(shimDir);
        await File.WriteAllTextAsync(Path.Join(shimDir, "rig"), "#!/bin/sh\necho foreign\n", TestContext.Current.CancellationToken);

        var sha = "8888888888888888888888888888888888888888";
        var proc = new FakeProc
        {
            Unanswered = new(0, "", ""),
            BeforeRun = req =>
            {
                if (req.Argv.Contains("archive"))
                {
                    var outputArg = req.Argv.FirstOrDefault(a => a.StartsWith("--output=", StringComparison.Ordinal));
                    if (outputArg is not null)
                    {
                        var tarPath = outputArg["--output=".Length..];
                        var emptyDir = _temp.Folder("empty-tar-source-3");
                        _ = Directory.CreateDirectory(Path.GetDirectoryName(tarPath)!);
                        TarFile.CreateFromDirectory(emptyDir, tarPath, false);
                    }
                }
            },
        }
            .Answer(new(0, sha + "\n", ""), "git", "-C", repo, "rev-parse");

        var host = new RuntimeHost(Root, repo, home, false, proc, TimeProvider.System);
        var root = new RootCommand
        {
            new RuntimeCommand(host, _out).Build(),
        };

        var exit = await root.Parse(["runtime", "install", "--ref", "HEAD", "--no-fetch"]).InvokeAsync(
            new InvocationConfiguration { Output = _out, Error = _error },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Contains("Left ", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("alone: rig did not write it", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_reports_unchanged_shim_when_already_written()
    {
        var repo = PathResolution.Resolve(_temp.Folder("repo-unchanged-shim"));
        await File.WriteAllTextAsync(Path.Join(repo, "pyproject.toml"), "[project]\nname = \"rig\"\n", TestContext.Current.CancellationToken);
        var home = _temp.Folder("home-unchanged");
        var shimPath = RigShim.PathFor(home, false);
        RigShim.Install(shimPath, Root, false);

        var sha = "9999999999999999999999999999999999999999";
        var proc = new FakeProc
        {
            Unanswered = new(0, "", ""),
            BeforeRun = req =>
            {
                if (req.Argv.Contains("archive"))
                {
                    var outputArg = req.Argv.FirstOrDefault(a => a.StartsWith("--output=", StringComparison.Ordinal));
                    if (outputArg is not null)
                    {
                        var tarPath = outputArg["--output=".Length..];
                        var emptyDir = _temp.Folder("empty-tar-source-unchanged");
                        _ = Directory.CreateDirectory(Path.GetDirectoryName(tarPath)!);
                        TarFile.CreateFromDirectory(emptyDir, tarPath, false);
                    }
                }
            },
        }
            .Answer(new(0, sha + "\n", ""), "git", "-C", repo, "rev-parse");

        var host = new RuntimeHost(Root, repo, home, false, proc, TimeProvider.System);
        var root = new RootCommand
        {
            new RuntimeCommand(host, _out).Build(),
        };

        var exit = await root.Parse(["runtime", "install", "--ref", "HEAD", "--no-fetch"]).InvokeAsync(
            new InvocationConfiguration { Output = _out, Error = _error },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Contains("rig on PATH: ", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_and_Rollback_handle_runtime_exceptions()
    {
        var repo = _temp.Folder("invalid-repo");
        // Nonexistent repo directory to trigger RuntimeInstallException
        var host = new RuntimeHost(Root, repo, _temp.Path, false, new FakeProc(), TimeProvider.System);
        var root = new RootCommand { new RuntimeCommand(host, _out).Build() };

        var exit = await root.Parse(["runtime", "install", "--repo", Path.Join(repo, "does-not-exist")]).InvokeAsync(
            new InvocationConfiguration { Output = _out, Error = _error },
            TestContext.Current.CancellationToken);
        Assert.Equal(1, exit);
        Assert.Contains("Install failed: ", _out.ToString(), StringComparison.Ordinal);

        _out.GetStringBuilder().Clear();
        exit = await root.Parse(["runtime", "rollback"]).InvokeAsync(
            new InvocationConfiguration { Output = _out, Error = _error },
            TestContext.Current.CancellationToken);
        Assert.Equal(1, exit);
        Assert.Contains("Rollback failed: ", _out.ToString(), StringComparison.Ordinal);
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

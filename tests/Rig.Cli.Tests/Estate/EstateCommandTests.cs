using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests;
using Rig.Core.Tests.Estate;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;

namespace Rig.Cli.Tests.Estate;

/// <summary>
/// <c>rig estate audit|risk|doctor|sync|clones</c> end to end over a small estate of real clones in a temp folder:
/// <c>safe</c> has everything on its origin; <c>risky</c> holds an unpushed branch and an uncommitted file.
/// </summary>
public sealed class EstateCommandTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeGitHub _github = new();

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Code => PathResolution.Resolve(Path.Join(_root.Path, "code"));

    private string Safe => Path.Join(Code, "safe");

    private string Risky => Path.Join(Code, "risky");

    private async Task BuildEstateAsync()
    {
        _ = await CloneAsync("safe");
        var risky = await CloneAsync("risky");
        _ = await Repos.GitAsync(risky, "switch", "-q", "-c", "wip");
        await Repos.CommitAsync(risky, "wip");
        await File.WriteAllTextAsync(Path.Join(risky, "first.txt"), "edited", Token);
    }

    // A clone of a fresh bare origin, with main pushed.
    private async Task<string> CloneAsync(string name)
    {
        var origin = Path.Join(_root.Path, "origins", $"{name}.git");
        _ = await Repos.GitAsync(_root.Path, "init", "-q", "--bare", "-b", "main", origin);
        var clone = await Repos.InitAsync(Path.Join(_root.Path, "code", name), origin);
        _ = await Repos.GitAsync(clone, "push", "-q", "-u", "origin", "main");
        return PathResolution.Resolve(clone);
    }

    private Task<(int Code, string Out, string Err)> RunAsync(params string[] args) => RunWithAsync(Repos.Proc, args);

    private async Task<(int Code, string Out, string Err)> RunWithAsync(IProc proc, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new EstateCommand(
            new EstateHost(Code, Path.Join(_root.Path, "home"), Risky),
            proc,
            _github,
            _clock,
            output,
            error);
        var code = await new RootCommand { command.Build() }.Parse(["estate", .. args]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Audit_json_inventories_every_clone()
    {
        await BuildEstateAsync();

        var (code, output, error) = await RunAsync("audit", "--json", "--branches", "--root", Code);

        Assert.Equal(0, code);
        Assert.Empty(error);
        var payload = JsonNode.Parse(output)!;
        Assert.Equal(Code, payload["root"]!.GetValue<string>());
        Assert.Empty(payload["duplicate_clones"]!.AsArray());
        var rows = payload["clones"]!.AsArray().ToDictionary(c => Path.GetFileName(c!["repo"]!.GetValue<string>()), c => c!);
        var risky = rows["risky"];
        Assert.Equal(Risky, risky["repo"]!.GetValue<string>());
        Assert.Equal(1, risky["local_only_commits"]!.GetValue<int>());
        Assert.Equal(["wip"], risky["unpushed_branches"]!.AsArray().Select(b => b!.GetValue<string>()));
        Assert.Equal(1, risky["dirty_files"]!.GetValue<int>());
        Assert.Equal(0, risky["stashes"]!.GetValue<int>());
        Assert.True(risky["has_remote"]!.GetValue<bool>());
        Assert.Equal(0, risky["reclaimable_bytes"]!.GetValue<long>());
        Assert.Empty(risky["reclaimable_by_category"]!.AsObject());
        Assert.Equal(0, rows["safe"]["local_only_commits"]!.GetValue<int>());
    }

    [Fact]
    public async Task Audit_table_puts_the_riskiest_clone_first_and_names_duplicates()
    {
        await BuildEstateAsync();
        _ = await Repos.GitAsync(_root.Path, "clone", "-q", Path.Join(_root.Path, "origins", "safe.git"), Path.Join(Code, "twin"));

        var (code, output, error) = await RunAsync("audit", "--root", Code);

        Assert.Equal(0, code);
        Assert.Contains($"3 clones under {Code} (freshly scanned)", error, StringComparison.Ordinal);
        Assert.True(output.IndexOf("risky", StringComparison.Ordinal) < output.IndexOf("safe", StringComparison.Ordinal));
        Assert.Contains("1 commits exist on no remote · 0B regenerates from a command", output, StringComparison.Ordinal);
        Assert.Contains("Duplicate clones", output, StringComparison.Ordinal);
        Assert.Contains("safe (0 local-only), twin (0 local-only)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Risk_json_lists_only_clones_holding_work_found_nowhere_else()
    {
        await BuildEstateAsync();

        var (code, output, _) = await RunAsync("risk", "--json", "--root", Code);

        Assert.Equal(0, code);
        var row = Assert.Single(JsonNode.Parse(output)!.AsArray())!;
        Assert.Equal(Risky, row["repo"]!.GetValue<string>());
        Assert.Equal(["repo", "local_only_commits", "unpushed_branches", "dirty_files", "stashes", "has_remote"], row.AsObject().Select(p => p.Key));
    }

    [Fact]
    public async Task Risk_names_the_push_that_saves_each_branch()
    {
        await BuildEstateAsync();

        var (code, output, _) = await RunAsync("risk", "--root", Code);

        Assert.Equal(0, code);
        Assert.Contains($"cd {Risky} && git push -u origin wip", output, StringComparison.Ordinal);
        Assert.Contains("1 uncommitted file(s)", output, StringComparison.Ordinal);
        Assert.Contains("1 commits across 1 clones exist only here.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Risk_reports_a_clone_with_no_remote_and_its_stash_and_says_when_nothing_is_at_risk()
    {
        var safe = await CloneAsync("safe");

        Assert.Equal("✓ Nothing at risk — every commit is on a remote, every tree clean.\n", (await RunAsync("risk", "--root", Code)).Out);

        _ = await Repos.GitAsync(safe, "remote", "remove", "origin");
        await File.WriteAllTextAsync(Path.Join(safe, "first.txt"), "stashed", Token);
        _ = await Repos.GitAsync(safe, "stash", "push", "-q", "-m", "test-stash");

        var (_, output, _) = await RunAsync("risk", "--root", Code);

        Assert.Contains("no remote at all", output, StringComparison.Ordinal);
        Assert.Contains("1 stash entry (one stack", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_refuses_a_folder_that_is_not_a_checkout()
    {
        var folder = _root.Folder("plain");

        var (code, output, error) = await RunAsync("doctor", folder);

        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.Contains("is not a git checkout.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_json_reports_the_clones_exposure_and_asks_github_nothing_for_a_non_github_origin()
    {
        await BuildEstateAsync();

        var (code, output, _) = await RunAsync("doctor", "--json", "--merged");

        Assert.Equal(0, code);
        var payload = JsonNode.Parse(output)!;
        Assert.Equal(Risky, payload["repo"]!.GetValue<string>());
        Assert.Equal(1, payload["local_only_commits"]!.GetValue<int>());
        Assert.Equal(1, payload["dirty_files"]!.GetValue<int>());
        Assert.Equal(["wip"], payload["unpushed_branches"]!.AsArray().Select(b => b!.GetValue<string>()));
        Assert.All(payload["findings"]!.AsArray(), f => Assert.Matches("^(error|warn|info)$", f!["severity"]!.GetValue<string>()));
        Assert.Empty(_github.Calls);
    }

    [Fact]
    public async Task Doctor_on_a_clean_clone_says_so_and_tabulates_findings_otherwise()
    {
        await BuildEstateAsync();

        var (_, clean, _) = await RunAsync("doctor", Safe);
        var (_, findings, _) = await RunAsync("doctor", Risky);

        Assert.Contains("0 worktree(s) · 1 local branch(es) · 0 local-only commit(s) · 0B reclaimable", clean, StringComparison.Ordinal);
        Assert.Contains("✓ Clean.", clean, StringComparison.Ordinal);
        Assert.Contains("risky", findings, StringComparison.Ordinal);
        Assert.DoesNotContain("✓ Clean.", findings, StringComparison.Ordinal);
        Assert.Contains("severity", findings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_fetches_every_clone_and_names_the_ones_that_fail()
    {
        await BuildEstateAsync();
        _ = await Repos.GitAsync(Risky, "remote", "set-url", "origin", Path.Join(_root.Path, "gone.git"));

        var (code, output, _) = await RunAsync("sync", "--root", Code);

        Assert.Equal(0, code);
        Assert.Contains("✓ Fetched 1 of 2 clones.", output, StringComparison.Ordinal);
        Assert.Contains("  risky: ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Git_in_a_clone_under_the_code_roots_external_folder_runs_hardened()
    {
        var external = PathResolution.Resolve(await Repos.InitAsync(Path.Join(Code, "external", "upstream")));
        var proc = new RecordingProc(Repos.Proc);

        var (code, _, _) = await RunWithAsync(proc, "risk", "--json", "--root", Code);

        Assert.Equal(0, code);
        var there = proc.Requests.Where(r => r.Argv[0] == "git" && r.WorkingDirectory == external).ToList();
        Assert.NotEmpty(there);
        Assert.All(there, r => Assert.Contains("core.fsmonitor=false", r.Argv));
    }

    private sealed class RecordingProc(IProc inner) : IProc
    {
        public List<ProcRequest> Requests { get; } = [];

        public Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return inner.RunAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task Clones_lists_the_cached_scan_and_its_age()
    {
        await BuildEstateAsync();

        var (_, first, _) = await RunAsync("clones", "--root", Code);
        _clock.Advance(TimeSpan.FromHours(3));
        var (code, second, _) = await RunAsync("clones", "--root", Code);

        Assert.Contains("freshly scanned", first, StringComparison.Ordinal);
        Assert.Equal(0, code);
        Assert.Contains($"2 clones under {Code}", second, StringComparison.Ordinal);
        Assert.Contains($"cached, scanned 3h ago · {Path.Join(_root.Path, "home", ".estate-repos.json")}", second, StringComparison.Ordinal);
        Assert.Contains($"  {Safe}", second, StringComparison.Ordinal);
    }
}

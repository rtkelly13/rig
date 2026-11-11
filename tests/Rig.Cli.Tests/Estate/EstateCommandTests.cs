using System.CommandLine;
using System.Runtime.Versioning;
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
/// <c>rig estate audit|risk|doctor|sync|clones|reclaim|trim|tidy</c> end to end over a small estate of real clones in a temp folder:
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

    // ---- reclaim -------------------------------------------------------------------------------------------------

    private static string NodeModules(string clone)
    {
        var modules = Path.Join(clone, "node_modules");
        _ = Directory.CreateDirectory(Path.Join(modules, "left-pad"));
        File.WriteAllText(Path.Join(modules, "left-pad", "index.js"), new string('x', 4096));
        return modules;
    }

    // A pushed worktree at safe/temp/wt: clean, and every commit on origin.
    private async Task<string> TempWorktreeAsync()
    {
        var path = Path.Join(Safe, "temp", "wt");
        _ = await Repos.GitAsync(Safe, "worktree", "add", "-q", "-b", "wt", path);
        _ = await Repos.GitAsync(path, "push", "-q", "-u", "origin", "wt");
        return path;
    }

    [Fact]
    public async Task Reclaim_dry_run_keeps_everything_and_notes_clones_at_risk()
    {
        await BuildEstateAsync();
        var modules = NodeModules(Risky);

        var (code, output, _) = await RunAsync("reclaim", "--min", "0", "--root", Code);

        Assert.Equal(0, code);
        Assert.Contains("Reclaimable (dry run — pass --apply)", output, StringComparison.Ordinal);
        Assert.Contains("would remove", output, StringComparison.Ordinal);
        Assert.Contains("Would free", output, StringComparison.Ordinal);
        Assert.Contains("risky — 1 local-only commit(s), 2 uncommitted file(s)", output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(modules));
    }

    [Fact]
    public async Task Reclaim_apply_removes_regenerable_output()
    {
        await BuildEstateAsync();
        var modules = NodeModules(Safe);

        var (code, output, _) = await RunAsync("reclaim", "--min", "0", "--apply", "--root", Code);

        Assert.Equal(0, code);
        Assert.Contains("removed", output, StringComparison.Ordinal);
        Assert.Contains("Freed", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(modules));
    }

    [Fact]
    public async Task Reclaim_refuses_a_dirty_worktree_unless_forced()
    {
        await BuildEstateAsync();
        var worktree = await TempWorktreeAsync();
        await File.WriteAllTextAsync(Path.Join(worktree, "first.txt"), "edited", Token);

        var (_, refused, _) = await RunAsync("reclaim", "--min", "0", "--apply", "--root", Code);
        var (_, forced, _) = await RunAsync("reclaim", "--min", "0", "--force", "--root", Code);

        Assert.Contains("dirty worktree", refused, StringComparison.Ordinal);
        Assert.True(Directory.Exists(worktree));
        Assert.Contains("would remove", forced, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reclaim_refuses_a_worktree_holding_the_only_copy_of_a_commit()
    {
        await BuildEstateAsync();
        var worktree = await TempWorktreeAsync();
        await Repos.CommitAsync(worktree, "local");

        var (_, output, _) = await RunAsync("reclaim", "--min", "0", "--apply", "--root", Code);

        Assert.Contains("only copy", output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(worktree));
    }

    [Fact]
    public async Task Reclaim_removes_a_clean_pushed_worktree_through_git()
    {
        await BuildEstateAsync();
        var worktree = await TempWorktreeAsync();

        var (_, output, _) = await RunAsync("reclaim", "--min", "0", "--apply", "--root", Code);

        Assert.Contains("removed", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(worktree));
        Assert.DoesNotContain("temp/wt", await Repos.GitAsync(Safe, "worktree", "list"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reclaim_never_removes_an_entry_resolving_outside_its_clone()
    {
        await BuildEstateAsync();
        var outside = NodeModules(_root.Folder("elsewhere"));
        _ = Directory.CreateSymbolicLink(Path.Join(Safe, "packages"), Path.GetDirectoryName(outside)!);

        var (_, output, _) = await RunAsync("reclaim", "--min", "0", "--apply", "--root", Code);

        Assert.Contains("outside clone", output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public async Task Reclaim_judges_each_worktree_in_a_temp_folder_of_worktrees_on_its_own()
    {
        await BuildEstateAsync();
        var group = Path.Join(Safe, "temp", "group");
        var kept = Path.Join(group, "kept");
        var gone = Path.Join(group, "gone");
        _ = await Repos.GitAsync(Safe, "worktree", "add", "-q", "-b", "kept", kept);
        await Repos.CommitAsync(kept, "local");
        _ = await Repos.GitAsync(Safe, "worktree", "add", "-q", "-b", "gone", gone);
        _ = await Repos.GitAsync(gone, "push", "-q", "-u", "origin", "gone");

        var (_, output, _) = await RunAsync("reclaim", "--min", "0", "--apply", "--root", Code);

        Assert.Contains("only copy", output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(kept));
        Assert.False(Directory.Exists(gone));
    }

    [Fact]
    public async Task Reclaim_never_removes_a_worktree_with_another_nested_inside_it()
    {
        await BuildEstateAsync();
        var outer = await TempWorktreeAsync();
        var inner = Path.Join(outer, "temp", "inner");
        _ = await Repos.GitAsync(Safe, "worktree", "add", "-q", "-b", "inner", inner);
        _ = await Repos.GitAsync(inner, "push", "-q", "-u", "origin", "inner");

        var (_, output, _) = await RunAsync("reclaim", "--min", "0", "--apply", "--force", "--root", Code);

        Assert.Contains("holds a worktree", output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(outer));
    }

    [Fact]
    public async Task Reclaim_refuses_temp_scratch_holding_a_checkout_git_does_not_list()
    {
        await BuildEstateAsync();
        var scratch = Path.Join(Safe, "temp", "scratch");
        _ = await Repos.GitAsync(Safe, "clone", "-q", Safe, Path.Join(scratch, "clone"));

        var (_, output, _) = await RunAsync("reclaim", "--min", "0", "--apply", "--root", Code);

        Assert.Contains("holds a git checkout", output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Join(scratch, "clone", ".git")));
    }

    [Fact]
    public async Task Reclaim_never_removes_a_worktree_holding_a_separate_clone()
    {
        await BuildEstateAsync();
        var worktree = await TempWorktreeAsync();
        var clone = Path.Join(worktree, "temp", "clone");
        _ = await Repos.GitAsync(Safe, "clone", "-q", Safe, clone);

        var (_, output, _) = await RunAsync("reclaim", "--min", "0", "--apply", "--force", "--root", Code);

        Assert.Contains("holds a git checkout", output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Join(clone, ".git")));
    }

    [Fact]
    public async Task Reclaim_removes_a_plain_file_directly_under_temp()
    {
        await BuildEstateAsync();
        var file = Path.Join(Safe, "temp", "scratch.log");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "log", Token);

        var (_, output, _) = await RunAsync("reclaim", "--min", "0", "--apply", "--root", Code);

        Assert.Contains("removed", output, StringComparison.Ordinal);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Reclaim_reports_a_worktree_git_will_not_remove_as_failed()
    {
        await BuildEstateAsync();
        var worktree = await TempWorktreeAsync();
        _ = await Repos.GitAsync(Safe, "worktree", "lock", worktree);

        var (_, output, _) = await RunAsync("reclaim", "--min", "0", "--apply", "--root", Code);

        Assert.Contains("failed", output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(worktree));
    }

    // ---- trim ----------------------------------------------------------------------------------------------------

    private async Task IgnoreNodeModulesAsync()
    {
        await File.WriteAllTextAsync(Path.Join(Safe, ".gitignore"), "node_modules/\n", Token);
        _ = await Repos.GitAsync(Safe, "add", ".gitignore");
        _ = await Repos.GitAsync(Safe, "commit", "-q", "-m", "ignore");
        _ = await Repos.GitAsync(Safe, "push", "-q");
    }

    [Fact]
    public async Task Trim_reports_idle_worktree_output_and_refuses_what_git_does_not_ignore()
    {
        await BuildEstateAsync();
        await IgnoreNodeModulesAsync();
        var worktree = await TempWorktreeAsync();
        var modules = NodeModules(worktree);
        _ = Directory.CreateDirectory(Path.Join(worktree, "dist"));
        await File.WriteAllTextAsync(Path.Join(worktree, "dist", "out.js"), new string('y', 4096), Token);
        _clock.Advance(TimeSpan.FromDays(400));

        var (code, output, _) = await RunAsync("trim", "--min", "0", "--root", Code);

        Assert.Equal(0, code);
        Assert.Contains("Idle worktrees (dry run", output, StringComparison.Ordinal);
        Assert.Contains("would remove", output, StringComparison.Ordinal);
        Assert.Contains("refused: not gitignored", output, StringComparison.Ordinal);
        Assert.Contains("Would free 4.0KB", output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(modules));
    }

    [Fact]
    public async Task Trim_apply_removes_the_output_and_keeps_the_worktree()
    {
        await BuildEstateAsync();
        await IgnoreNodeModulesAsync();
        var worktree = await TempWorktreeAsync();
        var modules = NodeModules(worktree);
        _clock.Advance(TimeSpan.FromDays(400));

        var (code, output, _) = await RunAsync("trim", "--min", "0", "--apply", "--root", Code);

        Assert.Equal(0, code);
        Assert.Contains("removed", output, StringComparison.Ordinal);
        Assert.Contains("Freed 4.0KB", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(modules));
        Assert.True(Directory.Exists(worktree));
    }

    [Fact]
    public async Task Trim_leaves_a_worktree_in_use_alone()
    {
        await BuildEstateAsync();
        await IgnoreNodeModulesAsync();
        var modules = NodeModules(await TempWorktreeAsync());
        _clock.Advance(TimeSpan.FromDays(400));

        var (_, output, _) = await RunAsync("trim", "--min", "0", "--idle-days", "100000", "--apply", "--root", Code);

        Assert.Contains("Freed 0B", output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(modules));
    }

    // ---- tidy ----------------------------------------------------------------------------------------------------

    // Answers lsof itself, so a run never depends on what this machine's processes are doing; the rest runs for real.
    private sealed class LsofProc(IProc inner, Ran lsof) : IProc
    {
        public List<ProcRequest> Requests { get; } = [];

        public Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return request.Argv[0] == "lsof" ? Task.FromResult(lsof) : inner.RunAsync(request, cancellationToken);
        }
    }

    private static LsofProc NothingBusy() => new(Repos.Proc, new Ran(0, string.Empty, string.Empty));

    // The clones a run looked at, in the order it first ran git in each.
    private static List<string> Inspected(LsofProc proc, params string[] clones) =>
        [.. proc.Requests.Select(r => r.WorkingDirectory).OfType<string>().Where(clones.Contains).Distinct()];

    [Theory]
    [InlineData]
    [InlineData("--all", "--repo", "safe")]
    public async Task Tidy_needs_exactly_one_explicit_scope(params string[] scope)
    {
        await BuildEstateAsync();
        var proc = NothingBusy();

        var (code, output, error) = await RunWithAsync(proc, ["tidy", "--root", Code, .. scope]);

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, output);
        Assert.Contains("Choose a scope: --repo <path|name> (repeatable), or --all for every clone.", error, StringComparison.Ordinal);
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public async Task Tidy_rejects_a_check_it_never_acts_on()
    {
        var (code, _, error) = await RunWithAsync(NothingBusy(), "tidy", "--root", Code, "--all", "--check", "worktree-dirty");

        Assert.Equal(2, code);
        Assert.Contains("Unknown --check worktree-dirty. Choose from: worktree-prunable, branch-upstream-gone", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tidy_merged_on_a_tidy_clone_fetches_only_it_and_has_nothing_to_do()
    {
        await BuildEstateAsync();
        var proc = NothingBusy();

        var (code, output, error) = await RunWithAsync(proc, "tidy", "--root", Code, "--repo", Safe, "--merged");

        Assert.Equal(0, code);
        Assert.StartsWith("Tidy (dry run — pass --apply)\n", output, StringComparison.Ordinal);
        Assert.Contains("\n✓ Nothing to tidy.\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("clones under", error, StringComparison.Ordinal);
        Assert.Equal([Safe], proc.Requests.Where(r => r.Argv.Contains("fetch")).Select(r => r.WorkingDirectory));
    }

    [Fact]
    public async Task Tidy_all_apply_keeps_work_found_nowhere_else_and_warns_when_lsof_cannot_answer()
    {
        await BuildEstateAsync();
        _ = await Repos.GitAsync(Risky, "branch", "-f", "done", "main");

        var (code, output, error) = await RunWithAsync(new LsofProc(Repos.Proc, new Ran(Ran.NotFound, string.Empty, string.Empty)), "tidy", "--root", Code, "--all", "--apply");

        Assert.Equal(0, code);
        Assert.StartsWith("Tidy\n", output, StringComparison.Ordinal);
        Assert.Contains("Cannot read process working directories (lsof); removals need --force.", error, StringComparison.Ordinal);
        Assert.Contains("2 clones under", error, StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, await Repos.GitAsync(Risky, "branch", "--list", "wip"));
        Assert.Equal(string.Empty, await Repos.GitAsync(Risky, "branch", "--list", "done"));
        Assert.Matches(@"risky\s+branch-merged\s+done\s+cleaned", output);
    }

    [Fact]
    public async Task Tidy_check_leaves_other_findings_alone_and_tabulates_what_it_did()
    {
        await BuildEstateAsync();
        _ = await Repos.GitAsync(Safe, "branch", "done");

        var (_, skipped, _) = await RunWithAsync(NothingBusy(), "tidy", "--root", Code, "--repo", Safe, "--check", "temp-stale", "--apply");
        var kept = await Repos.GitAsync(Safe, "branch", "--list", "done");
        var (code, dry, _) = await RunWithAsync(NothingBusy(), "tidy", "--root", Code, "--repo", "safe");
        var (_, applied, _) = await RunWithAsync(NothingBusy(), "tidy", "--root", Code, "--repo", "safe", "--apply");

        Assert.Contains("✓ Nothing to tidy.", skipped, StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, kept);
        Assert.Equal(0, code);
        Assert.Contains("clone  check          subject      outcome  detail\n", dry, StringComparison.Ordinal);
        Assert.Contains("safe   branch-merged  done     would-clean  no commits outside a remote · git branch -D done\n", dry, StringComparison.Ordinal);
        Assert.EndsWith("\n1 would-clean\n", dry, StringComparison.Ordinal);
        Assert.EndsWith("\n1 cleaned\n", applied, StringComparison.Ordinal);
        Assert.Equal(string.Empty, await Repos.GitAsync(Safe, "branch", "--list", "done"));
    }

    [Fact]
    public async Task Tidy_shortens_a_worktree_inside_its_clone_and_says_refusals_are_deliberate()
    {
        await BuildEstateAsync();
        var tree = await TempWorktreeAsync();
        await File.WriteAllTextAsync(Path.Join(tree, "notes.txt"), "unsaved", Token);
        _clock.Advance(TimeSpan.FromDays(60));
        File.SetLastWriteTimeUtc(tree, _clock.GetUtcNow().UtcDateTime.AddDays(-30));

        var (_, output, _) = await RunWithAsync(NothingBusy(), "tidy", "--root", Code, "--repo", "safe", "--check", "temp-stale-worktree");

        // Windows has no lsof, so the unreadable snapshot refuses before git status is asked.
        var reason = OperatingSystem.IsWindows() ? @"cannot tell whether a process is working in it \(lsof unavailable\)" : "uncommitted changes";
        Assert.Matches($@"safe\s+temp-stale-worktree\s+temp/wt\s+refused\s+{reason}", output);
        Assert.EndsWith("\nRefusals are deliberate — see `estate risk`, or pass --force.\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tidy_repo_takes_paths_and_names_in_order_once_each_and_a_worktree_path_means_its_clone()
    {
        var alpha = await CloneAsync("alpha");
        var beta = await CloneAsync("team/beta");
        var worktree = Path.Join(alpha, ".worktrees", "feature");
        _ = await Repos.GitAsync(alpha, "worktree", "add", "-q", "-b", "feature", worktree);
        var byPath = NothingBusy();
        var byName = NothingBusy();

        var (pathCode, _, pathError) = await RunWithAsync(byPath, "tidy", "--root", Code, "--repo", worktree, "--repo", alpha);
        var (nameCode, _, _) = await RunWithAsync(byName, "tidy", "--root", Code, "--repo", "TEAM/BETA", "--repo", "alpha", "--repo", "beta/");

        Assert.Equal((0, 0), (pathCode, nameCode));
        Assert.DoesNotContain("clones under", pathError, StringComparison.Ordinal);
        Assert.Equal([alpha], Inspected(byPath, alpha, beta));
        Assert.Equal([beta, alpha], Inspected(byName, alpha, beta));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Tidy_repo_resolves_a_link_before_its_parent_segment()
    {
        var alpha = await CloneAsync("alpha");
        var beta = await CloneAsync("beta");
        var inner = Directory.CreateDirectory(Path.Join(beta, "inner")).FullName;
        _ = Directory.CreateSymbolicLink(Path.Join(alpha, "link"), inner);
        var proc = NothingBusy();

        var (code, _, _) = await RunWithAsync(proc, "tidy", "--root", Code, "--repo", Path.Join(alpha, "link", ".."));

        Assert.Equal(0, code);
        Assert.Equal([beta], Inspected(proc, alpha, beta));
    }

    [Theory]
    [InlineData("beta", "is ambiguous: beta, team/beta")]
    [InlineData("gamma", "matches no clone")]
    public async Task Tidy_repo_refuses_a_name_matching_several_clones_or_none(string name, string problem)
    {
        _ = await CloneAsync("beta");
        _ = await CloneAsync("team/beta");

        var (code, output, error) = await RunWithAsync(NothingBusy(), "tidy", "--root", Code, "--repo", name);

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, output);
        Assert.Contains($"--repo {name} {problem} under {Code}. Pass a path, or --root.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tidy_repo_takes_a_checkout_with_a_separate_git_dir_but_not_a_worktree_of_one()
    {
        var split = Path.Join(Code, "split");
        _ = Directory.CreateDirectory(split);
        _ = await Repos.GitAsync(split, "init", "-q", "-b", "main", "--separate-git-dir", Path.Join(_root.Path, "elsewhere.git"));
        await Repos.CommitAsync(split, "first");
        var worktree = Path.Join(Code, "wt");
        _ = await Repos.GitAsync(split, "worktree", "add", "-q", "-b", "wt", worktree);
        var proc = NothingBusy();

        var (code, _, _) = await RunWithAsync(proc, "tidy", "--root", Code, "--repo", split);
        var (refused, _, error) = await RunWithAsync(NothingBusy(), "tidy", "--root", Code, "--repo", worktree);

        Assert.Equal(0, code);
        Assert.Equal([split], Inspected(proc, split));
        Assert.Equal(2, refused);
        Assert.Contains($"--repo {worktree}: cannot find the main working tree of this checkout.", error, StringComparison.Ordinal);
    }
}

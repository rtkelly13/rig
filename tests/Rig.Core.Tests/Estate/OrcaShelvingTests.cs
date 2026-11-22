using System.Text.Json.Nodes;
using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Orca;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.Estate;

public sealed class OrcaShelvingTests : IDisposable
{
    private static readonly string[] CreatePr = ["gh", "pr", "create"];
    private readonly ShelveFixture _fx = new();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow.AddDays(60);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _fx.Dispose();

    [Theory]
    [InlineData(15, 15, true)]
    [InlineData(14, 15, false)]
    [InlineData(15, 14, false)]
    [InlineData(1, 30, false)]
    [InlineData(30, 1, false)]
    [InlineData(-1, 30, false)]
    public void Newest_signal_must_be_strictly_older_than_fourteen_days(int files, int sidebar, bool stale) =>
        Assert.Equal(stale, OrcaShelving.IsStale(_now, _now.AddDays(-files), _now.AddDays(-sidebar).ToUnixTimeMilliseconds(), 14));

    [Fact]
    public void Unknown_signals_and_invalid_policy_are_not_cleanup_authority()
    {
        Assert.False(OrcaShelving.IsStale(_now, null, 1, 14));
        Assert.False(OrcaShelving.IsStale(_now, _now.AddDays(-30), null, 14));
        Assert.False(OrcaShelving.IsStale(_now, _now.AddDays(-30), double.NaN, 14));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OrcaShelvingPolicy(0).Validate());
    }

    private async Task<(string Path, FakeOrca Orca, FakeProc Proc, OrcaShelving Service)> SetupAsync(bool openPr = false, bool failPr = false, bool editDuringPr = false)
    {
        await _fx.InitAsync();
        var path = await _fx.AddWorktreeAsync("stale");
        await ShelveFixture.MakeDirtyAsync(path);
        var orca = new FakeOrca();
        orca.Repos.Add(FakeOrca.Project("r1", _fx.Clone, "clone"));
        var id = orca.AddWorktree(path, "stale");
        orca.Worktrees[0]["lastActivityAt"] = _now.AddDays(-30).ToUnixTimeMilliseconds();
        orca.Activity.Add(new JsonObject { ["worktreeId"] = id, ["agents"] = new JsonArray() });
        var head = await _fx.Git.OutAsync(path, "rev-parse", "HEAD");
        var pr = openPr ? new JsonArray(new JsonObject
        {
            ["number"] = 7,
            ["state"] = "OPEN",
            ["headRefOid"] = head,
            ["isCrossRepository"] = false,
            ["url"] = "https://github.com/test/clone/pull/7",
            ["updatedAt"] = _now.AddDays(-30).ToString("O"),
        }).ToJsonString() : "[]";
        var proc = new FakeProc
        {
            Fallback = Repos.Proc,
            BeforeRun = request =>
            {
                if (request.Argv.Take(3).SequenceEqual(CreatePr))
                {
                    Assert.True(Directory.Exists(path));
                    Assert.Contains("refs/heads/shelved/", _fx.Git.Run(_fx.Clone, ["ls-remote", "origin", "refs/heads/shelved/*"]).Out, StringComparison.Ordinal);
                    if (editDuringPr)
                    {
                        File.WriteAllText(System.IO.Path.Join(path, "new.txt"), "edited during PR creation");
                        File.SetLastWriteTimeUtc(System.IO.Path.Join(path, "new.txt"), _now.UtcDateTime);
                    }
                }
            },
        };
        proc.Answer(new Ran(0, "https://github.com/test/clone.git", ""), "git", "remote", "get-url", "origin");
        proc.Answer(new Ran(0, pr, ""), "gh", "pr", "list");
        proc.Answer(new Ran(0, "[]", ""), "gh", "label", "list");
        proc.Answer(new Ran(0, "", ""), "gh", "label", "create");
        proc.Answer(new Ran(0, "", ""), "gh", "pr", "edit");
        proc.Answer(new Ran(failPr ? 1 : 0, "https://github.com/test/clone/pull/8\n", failPr ? "permission denied" : ""), "gh", "pr", "create");
        proc.Answer(new Ran(0, "n/unrelated\n", ""), "lsof");
        return (path, orca, proc, new OrcaShelving(new Git(proc, _fx.Root), proc, new ManualClock(_now), System.IO.Path.Join(_fx.Root, "ledger.jsonl")));
    }

    [Fact]
    public async Task Dirty_branch_without_PR_is_verified_then_gets_a_TEMP_draft_before_disk_and_sidebar_cleanup()
    {
        var (path, orca, proc, service) = await SetupAsync();
        var journal = new List<OrcaShelvingRow>();
        var result = await service.RunAsync(orca.Runtime(), _fx.Clone, new(), true, journal.Add, token: Token);
        Assert.Equal("shelved", Assert.Single(result).Outcome);
        Assert.False(Directory.Exists(path));
        Assert.Single(orca.Removed);
        var created = Assert.Single(proc.Requests, r => r.Argv.Take(3).SequenceEqual(CreatePr));
        Assert.Contains("--draft", created.Argv);
        Assert.Contains("TEMP: Shelved stale", created.Argv);
        Assert.Contains("shelved", created.Argv);
        Assert.Contains(journal, row => row.Outcome == "shelf-verified" && row.Sha.Length == 40);
        Assert.Contains(journal, row => row.Outcome == "removing" && row.PrUrl != null && row.PrUrl.AbsoluteUri.EndsWith("/8", StringComparison.Ordinal));
        Assert.Single(await _fx.Store.ListAsync(_fx.Clone));
        Assert.True(File.Exists(System.IO.Path.Join(_fx.Root, "ledger.jsonl")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PR_failure_or_activity_during_PR_creation_preserves_worktree_and_remote_shelf(bool failPr, bool changed)
    {
        var (path, orca, _, service) = await SetupAsync(failPr: failPr, editDuringPr: changed);
        var rows = await service.RunAsync(orca.Runtime(), _fx.Clone, new(), true, _ => { }, token: Token);
        Assert.Equal("failed", Assert.Single(rows).Outcome);
        Assert.True(Directory.Exists(path));
        Assert.Empty(orca.Removed);
        Assert.Single(await _fx.Store.ListAsync(_fx.Clone));
        Assert.Equal(40, rows[0].Sha.Length);
    }

    [Fact]
    public async Task Existing_open_PR_is_labelled_stale_and_shelved_without_creating_another_PR()
    {
        var (_, orca, proc, service) = await SetupAsync(openPr: true);
        var rows = await service.RunAsync(orca.Runtime(), _fx.Clone, new(), true, _ => { }, token: Token);
        Assert.Equal("shelved", Assert.Single(rows).Outcome);
        Assert.Contains(proc.Requests, r => r.Argv.Contains("stale,shelved"));
        Assert.DoesNotContain(proc.Requests, r => r.Argv.Take(3).SequenceEqual(CreatePr));
    }

    [Fact]
    public async Task Preview_reports_branch_without_PR_but_never_pushes_labels_creates_or_removes()
    {
        var (path, orca, proc, service) = await SetupAsync();
        var rows = await service.RunAsync(orca.Runtime(), _fx.Clone, new(), false, _ => { }, token: Token);
        Assert.Equal("would-shelve", Assert.Single(rows).Outcome);
        Assert.Equal("stale", rows[0].Branch);
        Assert.True(Directory.Exists(path));
        Assert.Empty(await _fx.Store.ListAsync(_fx.Clone));
        Assert.Empty(orca.Removed);
        Assert.DoesNotContain(proc.Requests, r => r.Argv[0] == "gh" && !r.Argv.Contains("list"));
    }

    [Fact]
    public async Task Recent_uncommitted_file_or_pinned_workspace_stays_without_a_GitHub_write()
    {
        var (path, orca, proc, service) = await SetupAsync();
        File.SetLastWriteTimeUtc(System.IO.Path.Join(path, "new.txt"), _now.UtcDateTime);
        var rows = await service.RunAsync(orca.Runtime(), _fx.Clone, new(), true, _ => { }, token: Token);
        Assert.Equal("kept", Assert.Single(rows).Outcome);
        orca.Worktrees[0]["isPinned"] = true;
        rows = await service.RunAsync(orca.Runtime(), _fx.Clone, new(), true, _ => { }, token: Token);
        Assert.Equal("kept", Assert.Single(rows).Outcome);
        Assert.Empty(orca.Removed);
        Assert.DoesNotContain(proc.Requests, r => r.Argv[0] == "gh");
    }
    [Fact]
    public async Task Ignored_non_regenerable_files_and_unknown_process_liveness_are_decisions_not_uploads()
    {
        var (path, orca, proc, service) = await SetupAsync();
        await File.WriteAllTextAsync(System.IO.Path.Join(path, ".env"), "local-only", Token);
        var rows = await service.RunAsync(orca.Runtime(), _fx.Clone, new(), true, _ => { }, token: Token);
        Assert.Equal("needs-decision", Assert.Single(rows).Outcome);
        Assert.Contains("ignored", rows[0].Reason, StringComparison.Ordinal);
        File.Delete(System.IO.Path.Join(path, ".env"));
        proc.Answer(new Ran(127, "", "no lsof"), "lsof");
        rows = await service.RunAsync(orca.Runtime(), _fx.Clone, new(), true, _ => { }, token: Token);
        Assert.Equal("needs-decision", Assert.Single(rows).Outcome);
        Assert.Contains("process", rows[0].Reason, StringComparison.Ordinal);
        Assert.True(Directory.Exists(path));
        Assert.Empty(await _fx.Store.ListAsync(_fx.Clone));
        Assert.DoesNotContain(proc.Requests, r => r.Argv[0] == "gh" && !r.Argv.Contains("list"));
    }

    [Fact]
    public async Task Failed_push_never_creates_PR_or_removes_work_and_busy_agents_are_kept()
    {
        var (path, orca, proc, service) = await SetupAsync();
        proc.Answer(new Ran(1, "", "remote unavailable"), "git", "-c", "core.longpaths=true", "push");
        var rows = await service.RunAsync(orca.Runtime(), _fx.Clone, new(), true, _ => { }, token: Token);
        Assert.Equal("failed", Assert.Single(rows).Outcome);
        Assert.True(Directory.Exists(path));
        Assert.Empty(orca.Removed);
        Assert.DoesNotContain(proc.Requests, r => r.Argv.Take(3).SequenceEqual(CreatePr));
        orca.Activity[0]["agents"] = new JsonArray(new JsonObject { ["state"] = "waiting" });
        rows = await service.RunAsync(orca.Runtime(), _fx.Clone, new(), true, _ => { }, token: Token);
        Assert.Equal("kept", Assert.Single(rows).Outcome);
    }

}

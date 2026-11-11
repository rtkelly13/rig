using System.Text.Json.Nodes;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.GitHub;

/// <summary>
/// <see cref="RepoResolver"/> and <see cref="RepoFolderCheck"/> over a scripted GitHub and a temp cache file.
/// Ported from the deleted Python resolver's tests, <c>tests/test_workspace_repo_resolver.py</c>.
/// </summary>
public sealed class RepoResolverTests : IDisposable
{
    private static readonly RepoSlug Rig = new("rtkelly13", "rig");
    private static readonly RepoSlug Old = new("rtkelly13", "shared-utilities");
    private static readonly RepoSlug Gone = new("rtkelly13", "gone");

    private readonly TempDir _home = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 11, 9, 30, 15, 250, TimeSpan.Zero));
    private readonly List<ApiRequest> _calls = [];
    private Ran _answer = new(0, "{}", string.Empty);

    public void Dispose() => _home.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string CacheFile => RepoResolver.DefaultCacheFile(_home.Path);

    private RepoResolver Resolver() => new(new Scripted(this), CacheFile, _clock);

    private static string Answer(params (int Index, string Name)[] found) =>
        new JsonObject
        {
            ["data"] = new JsonObject(found.Select(f => KeyValuePair.Create<string, JsonNode?>(
                $"r{f.Index}",
                new JsonObject { ["name"] = f.Name, ["nameWithOwner"] = $"rtkelly13/{f.Name}", ["isArchived"] = false, ["url"] = $"https://github.com/rtkelly13/{f.Name}" }))),
        }.ToJsonString();

    [Fact]
    public async Task One_batched_query_resolves_every_slug_and_a_renamed_one_keeps_the_queried_name()
    {
        _answer = new(0, Answer((0, "rig"), (1, "rig")), string.Empty);

        var resolved = await Resolver().ResolveAsync([Rig, Old], cancellationToken: Token);

        var call = Assert.Single(_calls);
        Assert.Equal("graphql", call.Route);
        var variables = JsonNode.Parse(call.Body!)!["variables"]!;
        Assert.Equal("shared-utilities", (string?)variables["n1"]);
        Assert.False(resolved[Rig]!.IsRenamed);
        Assert.True(resolved[Old]!.IsRenamed);
        Assert.Equal("shared-utilities", resolved[Old]!.QueriedName);
        Assert.Equal(new Uri("https://github.com/rtkelly13/rig"), resolved[Old]!.Url);
    }

    [Fact]
    public async Task A_repository_github_cannot_find_is_null_and_the_rest_of_a_failed_answer_still_counts()
    {
        _answer = new(1, Answer((0, "rig")), "Could not resolve to a Repository");

        var resolved = await Resolver().ResolveAsync([Rig, Gone], cancellationToken: Token);

        Assert.Equal("rig", resolved[Rig]!.CanonicalName);
        Assert.Null(resolved[Gone]);
        Assert.Contains("rtkelly13/rig", await File.ReadAllTextAsync(CacheFile, Token), StringComparison.Ordinal);
        Assert.DoesNotContain("rtkelly13/gone", await File.ReadAllTextAsync(CacheFile, Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fresh_cache_answers_without_a_call_and_a_stale_one_is_asked_again()
    {
        _answer = new(0, Answer((0, "rig")), string.Empty);
        _ = await Resolver().ResolveAsync([Rig], cancellationToken: Token);

        _clock.Advance(TimeSpan.FromHours(23));
        var cached = await Resolver().ResolveAsync([Rig], cancellationToken: Token);
        Assert.Single(_calls);
        Assert.Equal("rig", cached[Rig]!.CanonicalName);

        _clock.Advance(TimeSpan.FromHours(2));
        _ = await Resolver().ResolveAsync([Rig], cancellationToken: Token);
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public async Task Force_refresh_ignores_the_cache()
    {
        _answer = new(0, Answer((0, "rig")), string.Empty);
        _ = await Resolver().ResolveAsync([Rig], cancellationToken: Token);

        _ = await Resolver().ResolveAsync([Rig], forceRefresh: true, cancellationToken: Token);

        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public async Task Nothing_resolved_writes_no_cache_and_an_unreadable_cache_is_ignored()
    {
        await File.WriteAllTextAsync(CacheFile, "{not json", Token);
        _answer = new(1, string.Empty, "gh: not logged in");

        var resolved = await Resolver().ResolveAsync([Rig], cancellationToken: Token);

        Assert.Null(resolved[Rig]);
        Assert.Equal("{not json", await File.ReadAllTextAsync(CacheFile, Token));
    }

    [Fact]
    public async Task The_cache_keeps_the_python_resolvers_format()
    {
        _answer = new(0, Answer((0, "rig")), string.Empty);

        _ = await Resolver().ResolveAsync([Old], cancellationToken: Token);

        var entry = JsonNode.Parse(await File.ReadAllTextAsync(CacheFile, Token))!["repos"]!["rtkelly13/shared-utilities"]!;
        Assert.Equal("rig", (string?)entry["canonical_name"]);
        Assert.Equal("shared-utilities", (string?)entry["queried_name"]);
        Assert.Equal("rtkelly13/rig", (string?)entry["name_with_owner"]);
        // datetime.fromisoformat reads this spelling back.
        Assert.Equal("2026-11-11T09:30:15.250000+00:00", (string?)entry["fetched_at"]);
    }

    [Fact]
    public async Task A_cache_the_python_resolver_wrote_is_read()
    {
        await File.WriteAllTextAsync(CacheFile, """
            {"repos": {"rtkelly13/rig": {"owner": "rtkelly13", "canonical_name": "rig", "name_with_owner": "rtkelly13/rig",
              "queried_name": "rig", "fetched_at": "2026-11-11T08:00:00.123456+00:00", "is_archived": true, "url": ""}}}
            """, Token);

        var resolved = await Resolver().ResolveAsync([Rig], cancellationToken: Token);

        Assert.Empty(_calls);
        Assert.True(resolved[Rig]!.IsArchived);
        Assert.Null(resolved[Rig]!.Url);
    }

    [Theory]
    [InlineData("/code/rig", "rig", "rig", RepoFolderStatus.Ok)]
    [InlineData("/code/RIG", "Rig", "rig", RepoFolderStatus.Ok)]
    [InlineData("/code/shared-utilities", "shared-utilities", "rig", RepoFolderStatus.FolderRenamed)]
    [InlineData("/code/tripletail", "rig", "rig", RepoFolderStatus.FolderMismatch)]
    [InlineData("/code/rig", "shared-utilities", "rig", RepoFolderStatus.RemoteOutdated)]
    public void Folder_and_remote_compare_with_the_canonical_name(string path, string remote, string canonical, RepoFolderStatus status)
    {
        var repo = new CanonicalRepo("rtkelly13", canonical, $"rtkelly13/{canonical}", remote);

        Assert.Equal(status, RepoFolderCheck.Classify(path, new RepoSlug("rtkelly13", remote), repo).Status);
    }

    [Fact]
    public void A_repository_github_does_not_know_is_unresolved()
    {
        var check = RepoFolderCheck.Classify("/code/gone", Gone, null);

        Assert.Equal(RepoFolderStatus.Unresolved, check.Status);
        Assert.Equal("Could not resolve repository 'rtkelly13/gone' on GitHub", check.Detail);
    }

    [Fact]
    public async Task A_worktree_is_checked_as_its_main_checkout()
    {
        var main = Path.Join(_home.Path, "rig");
        var proc = new FakeProc()
            .Answer(new(0, Path.Join(main, ".git") + "\n", string.Empty), "git", "rev-parse", "--git-common-dir")
            .Answer(new(0, "git@github.com:rtkelly13/rig.git\n", string.Empty), "git", "config", "--get", "remote.origin.url");
        _answer = new(0, Answer((0, "rig")), string.Empty);

        var check = await RepoFolderCheck.CheckAsync(new Git(proc, "/nowhere"), Resolver(), Path.Join(main, "temp", "wt"), Token);

        Assert.Equal(RepoFolderStatus.Ok, check.Status);
        Assert.Equal("rig", check.FolderName);
        Assert.Equal(main, proc.Requests[1].WorkingDirectory);
    }

    [Theory]
    [InlineData("", "No remote origin URL configured or not a git repository")]
    [InlineData("git@gitlab.com:o/r.git", "Non-GitHub remote origin (git@gitlab.com:o/r.git)")]
    public async Task A_clone_without_a_github_origin_is_local_only(string origin, string detail)
    {
        var proc = new FakeProc()
            .Answer(new(0, ".git\n", string.Empty), "git", "rev-parse")
            .Answer(new(origin.Length == 0 ? 1 : 0, origin, string.Empty), "git", "config");

        var check = await RepoFolderCheck.CheckAsync(new Git(proc, "/nowhere"), Resolver(), _home.Path, Token);

        Assert.Equal(RepoFolderStatus.LocalOnly, check.Status);
        Assert.Equal(detail, check.Detail);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Not_a_repository_is_local_only_without_asking_for_a_remote()
    {
        var proc = new FakeProc().Answer(new(128, string.Empty, "fatal: not a git repository"), "git");

        var check = await RepoFolderCheck.CheckAsync(new Git(proc, "/nowhere"), Resolver(), _home.Path, Token);

        Assert.Equal(RepoFolderStatus.LocalOnly, check.Status);
        Assert.Single(proc.Requests);
    }

    [Fact]
    public async Task The_estate_scan_checks_each_github_clone_in_one_query_ordered_by_folder()
    {
        var proc = new FakeProc();
        string Clone(string parent, string name, string? origin)
        {
            var folder = Path.Join(_home.Path, parent, name);
            _ = Directory.CreateDirectory(Path.Join(folder, ".git"));
            _ = proc
                .AnswerIn(folder, new(0, ".git\n", string.Empty), "git", "rev-parse")
                .AnswerIn(folder, new(origin is null ? 1 : 0, origin ?? string.Empty, string.Empty), "git", "config");
            return folder;
        }

        _ = Clone("personal", "shared-utilities", "git@github.com:rtkelly13/shared-utilities.git");
        _ = Clone("personal", "rig", "https://github.com/rtkelly13/rig");
        _ = Clone("personal", "alpha", "git@gitlab.com:o/alpha.git");
        _ = Clone("personal", "local", null);
        _ = Directory.CreateDirectory(Path.Join(_home.Path, "personal", "notes"));
        await File.WriteAllTextAsync(Path.Join(_home.Path, "personal", "README.md"), string.Empty, Token);
        _ = Clone("labs", "Beta", "git@github.com:rtkelly-labs/Beta.git");
        _answer = new(0, Answer((0, "Beta"), (1, "rig"), (2, "rig")), string.Empty);
        string[] parents = [Path.Join(_home.Path, "personal"), Path.Join(_home.Path, "labs"), Path.Join(_home.Path, "missing")];

        var checks = await RepoFolderCheck.EstateAsync(new Git(proc, "/nowhere"), Resolver(), parents, cancellationToken: Token);

        Assert.Equal(["Beta", "rig", "shared-utilities"], checks.Select(c => c.FolderName));
        Assert.Equal([RepoFolderStatus.Ok, RepoFolderStatus.Ok, RepoFolderStatus.FolderRenamed], checks.Select(c => c.Status));
        Assert.Single(_calls);
        Assert.True(checks[2].IsIssue);
        Assert.False(checks[2].IsMatch);
        Assert.False(checks[2].RemoteMatchesCanonical);
    }

    [Theory]
    [InlineData("rig", "rig", true, true)]
    [InlineData("tripletail", "rig", false, true)]
    [InlineData("rig", "shared-utilities", true, false)]
    public void Folder_and_remote_matches_are_reported_separately(string folder, string remote, bool isMatch, bool remoteMatches)
    {
        var check = RepoFolderCheck.Classify($"/code/{folder}", new RepoSlug("rtkelly13", remote), new CanonicalRepo("rtkelly13", "rig", "rtkelly13/rig", remote));

        Assert.Equal(isMatch, check.IsMatch);
        Assert.Equal(remoteMatches, check.RemoteMatchesCanonical);
    }

    [Fact]
    public void Local_only_matches_and_unresolved_does_not()
    {
        var local = new RepoFolderCheck("/code/x", null, null, RepoFolderStatus.LocalOnly, "local");
        var gone = RepoFolderCheck.Classify("/code/gone", Gone, null);

        Assert.True(local.IsMatch && local.RemoteMatchesCanonical && !local.IsIssue);
        Assert.False(gone.IsMatch || gone.RemoteMatchesCanonical);
        Assert.True(gone.IsIssue);
    }

    private sealed class Scripted(RepoResolverTests test) : IGitHubApi
    {
        public Task<Ran> CallAsync(ApiRequest request, CancellationToken cancellationToken = default)
        {
            test._calls.Add(request);
            return Task.FromResult(test._answer);
        }
    }
}

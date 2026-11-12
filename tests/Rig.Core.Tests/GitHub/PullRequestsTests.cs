using System.Text.Json.Nodes;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.GitHub;

/// <summary>The shared PR resolver: refs, chain building (pure) and resolution through a fake GraphQL API. Ported from <c>tests/test_github_prs.py</c>.</summary>
public sealed class PullRequestsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // -- refs --------------------------------------------------------------------

    [Theory]
    [InlineData("acme-corp/data-platform#744", "acme-corp/data-platform#744")]
    [InlineData("  Acme-Corp/Data-Platform#7 ", "acme-corp/data-platform#7")]
    [InlineData("owner/repo.js#1", "owner/repo.js#1")]
    [InlineData("owner/repo#0", null)]
    [InlineData("owner/repo", null)]
    [InlineData("owner/repo#1 } evil", null)]
    [InlineData("owner\"/repo#1", null)]
    public void Parses_only_owner_repo_number(string text, string? expected) =>
        Assert.Equal(expected, PrRef.TryParse(text)?.ToString());

    [Fact]
    public void A_ref_round_trips_and_is_lowercased()
    {
        var pr = new PrRef("o", "r", 5);

        Assert.Equal("o/r#5", pr.ToString());
        Assert.Equal("o/r", pr.Slug);
        Assert.Equal(pr, PrRef.TryParse(pr.ToString()));
        Assert.Equal(pr, PrRef.Parse(pr.ToString()));
        Assert.Equal(new PrRef("acme-corp", "data-platform", 1), new PrRef("Acme-Corp", "Data-Platform", 1));
    }

    [Fact]
    public void Parse_throws_for_a_non_ref()
    {
        var error = Assert.Throws<FormatException>(() => PrRef.Parse("owner/repo"));
        Assert.Equal("not an owner/repo#n reference: 'owner/repo'", error.Message);
    }

    [Fact]
    public void Refs_sort_by_owner_repo_then_number()
    {
        PrRef[] refs = [new("b", "a", 1), new("a", "b", 2), new("a", "b", 10), new("a", "a", 9)];

        Assert.Equal(["a/a#9", "a/b#2", "a/b#10", "b/a#1"], refs.Order().Select(pr => pr.ToString()));
        Assert.True(refs[1] < refs[2]);
        Assert.True(refs[1] <= refs[1]);
        Assert.True(refs[0] > refs[3]);
        Assert.True(refs[0] >= refs[0]);
        Assert.True(null < refs[0]);
        Assert.False(refs[0] < null);
        Assert.True((PrRef?)null <= null);
        Assert.Equal(1, refs[0].CompareTo(null));
    }

    // -- stack chains ------------------------------------------------------------

    [Fact]
    public void A_stack_chain_is_bottom_first() =>
        Assert.Equal([new Chain(ChainKind.Stack, "main", [1, 2, 3])], PrChains.Stacks([Pr(3, "c", "b"), Pr(1, "a"), Pr(2, "b", "a")]));

    [Fact]
    public void A_branching_stack_is_walked_breadth_first()
    {
        // 778 is the root; 780, 781 and 783 all sit on it, and 790 sits on 781.
        OpenPr[] prs = [Pr(778, "root"), Pr(790, "b2", "b"), Pr(783, "c", "root"), Pr(781, "b", "root"), Pr(780, "a", "root")];

        Assert.Equal([new Chain(ChainKind.Stack, "main", [778, 780, 781, 783, 790])], PrChains.Stacks(prs));
    }

    [Fact]
    public void Unrelated_prs_make_no_chain() => Assert.Empty(PrChains.Stacks([Pr(1, "a"), Pr(2, "b")]));

    [Fact]
    public void A_merged_middle_member_splits_the_stack() => Assert.Empty(PrChains.Stacks([Pr(1, "a"), Pr(3, "c", "b")]));

    [Fact]
    public void A_fork_head_is_never_a_base() =>
        Assert.Empty(PrChains.Stacks([Pr(1, "feature") with { CrossRepo = true }, Pr(2, "x", "feature")]));

    [Fact]
    public void A_self_based_pr_is_a_root() => Assert.Empty(PrChains.Stacks([Pr(1, "a", "a")]));

    [Fact]
    public void A_shared_head_is_never_a_base() => Assert.Empty(PrChains.Stacks([Pr(1, "dup"), Pr(2, "dup"), Pr(3, "x", "dup")]));

    // -- queue chains ------------------------------------------------------------

    [Fact]
    public void The_native_queue_goes_by_position() =>
        Assert.Equal(
            [new Chain(ChainKind.Queue, "main", [4, 5])],
            PrChains.Queues([Pr(5, "e") with { QueuePosition = 2 }, Pr(4, "d") with { QueuePosition = 1 }, Pr(6, "f")]));

    [Fact]
    public void The_label_queue_is_the_holder_first_then_by_label_time()
    {
        OpenPr[] prs =
        [
            Labelled(Pr(10, "a"), ("automerge", "2026-09-01T10:00:00Z")),
            Labelled(Pr(11, "b"), ("automerge", "2026-09-01T09:00:00Z")),
            Labelled(Pr(12, "c"), ("automerge", null), ("locked-merging", "x")),
        ];

        Assert.Equal([new Chain(ChainKind.Queue, "main", [12, 11, 10])], PrChains.Queues(prs));
    }

    [Fact]
    public void Label_time_falls_back_to_created_at()
    {
        OpenPr[] prs =
        [
            Labelled(Pr(20, "a") with { CreatedAt = "2026-09-03T00:00:00Z" }, ("automerge", null)),
            Labelled(Pr(21, "b") with { CreatedAt = "2026-09-02T00:00:00Z" }, ("automerge", null)),
        ];

        Assert.Equal([new Chain(ChainKind.Queue, "main", [21, 20])], PrChains.Queues(prs));
    }

    [Fact]
    public void Native_queue_members_are_not_label_queued()
    {
        OpenPr[] prs =
        [
            Labelled(Pr(1, "a") with { QueuePosition = 1 }, ("automerge", null)),
            Labelled(Pr(2, "b"), ("automerge", null)),
            Labelled(Pr(3, "c"), ("automerge", null)),
        ];

        Assert.Equal([new Chain(ChainKind.Queue, "main", [2, 3])], PrChains.Queues(prs));
    }

    [Fact]
    public void Queues_are_per_base() =>
        Assert.Empty(PrChains.Queues([Pr(1, "a") with { QueuePosition = 1 }, Pr(2, "b", "release") with { QueuePosition = 1 }]));

    [Fact]
    public void Chains_are_stacks_then_queues()
    {
        OpenPr[] prs = [Pr(1, "a"), Pr(2, "b", "a"), Pr(3, "c") with { QueuePosition = 1 }, Pr(4, "d") with { QueuePosition = 2 }];

        Assert.Equal([new Chain(ChainKind.Stack, "main", [1, 2]), new Chain(ChainKind.Queue, "main", [3, 4])], PrChains.Build(prs));
    }

    [Fact]
    public void Chain_equality_compares_members_in_order()
    {
        var chain = new Chain(ChainKind.Stack, "main", [1, 2]);

        Assert.NotEqual(chain, new Chain(ChainKind.Stack, "main", [2, 1]));
        Assert.False(chain.Equals(None<Chain>()));
        Assert.Equal(chain.GetHashCode(), new Chain(ChainKind.Stack, "main", [1, 2]).GetHashCode());
    }

    // -- GraphQL shape -----------------------------------------------------------

    [Fact]
    public void The_query_passes_names_and_cursor_as_variables_and_aliases_lookups()
    {
        var request = PrResolver.BuildQuery("o", "r", [3, 5], openList: true, after: "Y3Vy");
        var body = JsonNode.Parse(request.Body!)!;
        var query = body["query"]!.GetValue<string>();

        Assert.Equal(("POST", "graphql"), (request.Method, request.Route));
        Assert.Contains("repository(owner: $owner, name: $name)", query, StringComparison.Ordinal);
        Assert.Contains("pullRequests(states: OPEN, first: 100, after: $after)", query, StringComparison.Ordinal);
        Assert.Contains("pr3: pullRequest(number: 3)", query, StringComparison.Ordinal);
        Assert.Contains("pr5: pullRequest(number: 5)", query, StringComparison.Ordinal);
        Assert.Equal("""{"owner":"o","name":"r","after":"Y3Vy"}""", body["variables"]!.ToJsonString());

        var lookupsOnly = JsonNode.Parse(PrResolver.BuildQuery("o", "r", [3], openList: false, after: "Y3Vy").Body!)!;
        Assert.DoesNotContain("open:", lookupsOnly["query"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("$after", lookupsOnly["query"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("""{"owner":"o","name":"r"}""", lookupsOnly["variables"]!.ToJsonString());
    }

    [Fact]
    public void Details_read_size_review_and_checks()
    {
        var node = JsonNode.Parse("""
            {"additions": 700, "deletions": 150, "changedFiles": 12, "isDraft": false,
             "reviewDecision": "CHANGES_REQUESTED", "viewerDidAuthor": true,
             "reviewRequests": {"nodes": [null, {"requestedReviewer": {}}, {"requestedReviewer": {"isViewer": true}}]},
             "commits": {"nodes": [{"commit": {"statusCheckRollup": {"state": "FAILURE"}}}]}}
            """);

        var details = PrResolver.ParseDetails(node);

        Assert.Equal(new PrDetails(700, 150, 12, false, "CHANGES_REQUESTED", "FAILURE", true, true), details);
        Assert.Equal((850, true), (details.ChangedLines, details.ChecksFailing));
        Assert.False(new PrDetails(Checks: "SUCCESS").ChecksFailing);
        Assert.True(new PrDetails(Checks: "ERROR").ChecksFailing);
    }

    [Theory]
    [InlineData("""{"reviewDecision": null, "commits": {"nodes": [{"commit": {}}]}}""")]
    [InlineData("""{"commits": {"nodes": [null, {"commit": {"statusCheckRollup": {"state": "FAILURE"}}}]}}""")]
    [InlineData("""{"additions": "many"}""")]
    [InlineData("""{"isDraft": "yes"}""")]
    [InlineData("""{"reviewRequests": {"nodes": null}}""")]
    [InlineData("""{"reviewRequests": {"nodes": ["x"]}}""")]
    [InlineData("""{"reviewRequests": {"nodes": [{"requestedReviewer": {"isViewer": null}}]}}""")]
    [InlineData("""{"reviewDecision": 3}""")]
    [InlineData("""{"commits": 3}""")]
    [InlineData("""{"reviewRequests": {}, "commits": {}}""")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Misshapen_or_missing_details_read_as_the_defaults(string json) =>
        Assert.Equal(new PrDetails(), PrResolver.ParseDetails(JsonNode.Parse(json)));

    [Fact]
    public void Details_do_not_affect_state_equality()
    {
        Assert.Equal(new PrState(PrStatus.Open), new PrState(PrStatus.Open, Details: new PrDetails(Draft: true)));
        Assert.Equal(new PrState(PrStatus.Open).GetHashCode(), new PrState(PrStatus.Open, Details: new PrDetails(Draft: true)).GetHashCode());
        Assert.NotEqual(new PrState(PrStatus.Unknown, "a"), new PrState(PrStatus.Unknown, "b"));
        Assert.False(new PrState(PrStatus.Open).Equals(None<PrState>()));
    }

    [Fact]
    public void An_open_pr_takes_its_latest_label_event()
    {
        var node = JsonNode.Parse("""
            {"number": 9, "headRefName": "h", "baseRefName": "main", "createdAt": "2026-01-01T00:00:00Z",
             "isCrossRepository": false, "mergeQueueEntry": {"position": 3},
             "labels": {"nodes": [{"name": "AutoMerge"}, null]},
             "timelineItems": {"nodes": [
               {"createdAt": "2026-02-02T00:00:00Z", "label": {"name": "automerge"}},
               {"createdAt": "2026-03-03T00:00:00Z", "label": {"name": "automerge"}},
               {"createdAt": "2026-01-05T00:00:00Z", "label": {"name": "automerge"}},
               {}, null]}}
            """);

        var pr = PrResolver.ParseOpenPr(node);

        Assert.Equal(["automerge"], pr.Labels);
        Assert.Equal(new Dictionary<string, string> { ["automerge"] = "2026-03-03T00:00:00Z" }, pr.LabeledAt);
        Assert.Equal(3, pr.QueuePosition);
        Assert.Equal((9, "h", "main", "2026-01-01T00:00:00Z", false), (pr.Number, pr.Head, pr.Base, pr.CreatedAt, pr.CrossRepo));
    }

    [Fact]
    public void A_bare_open_pr_takes_the_defaults()
    {
        var pr = PrResolver.ParseOpenPr(JsonNode.Parse("""{"number": 4}"""));

        Assert.Equal((4, "", "", "", false, (int?)null), (pr.Number, pr.Head, pr.Base, pr.CreatedAt, pr.CrossRepo, pr.QueuePosition));
        Assert.Empty(pr.Labels);
        Assert.Empty(pr.LabeledAt);
    }

    [Theory]
    [InlineData("""{"headRefName": "no-number"}""")]
    [InlineData("""{"number": 1.5}""")]
    [InlineData("""{"number": 1, "headRefName": null}""")]
    [InlineData("""{"number": 1, "isCrossRepository": null}""")]
    [InlineData("""{"number": 1, "labels": {"nodes": [{"name": null}]}}""")]
    [InlineData("""{"number": 1, "labels": []}""")]
    [InlineData("""{"number": 1, "mergeQueueEntry": {"position": "first"}}""")]
    [InlineData("\"pr\"")]
    public void A_misshapen_open_pr_throws(string json) =>
        Assert.Throws<FormatException>(() => PrResolver.ParseOpenPr(JsonNode.Parse(json)));

    // -- resolution --------------------------------------------------------------

    [Fact]
    public async Task Resolves_states_and_only_the_chains_that_hold_a_requested_pr()
    {
        var fake = Fake(("o/r", new FakePrRepo(
            States(1, "OPEN", 2, "OPEN", 3, "MERGED", 4, "CLOSED"),
            [Node(1, "a"), Node(2, "b", "a"), Node(7, "x"), Node(8, "y", "x")])));

        var resolution = await PrResolver.ResolveAsync(fake, Refs("o/r#1", "o/r#2", "o/r#3", "o/r#4"), cancellationToken: Token);

        Assert.Equal(
            new Dictionary<PrRef, PrState>
            {
                [new("o", "r", 1)] = new(PrStatus.Open),
                [new("o", "r", 2)] = new(PrStatus.Open),
                [new("o", "r", 3)] = new(PrStatus.Merged),
                [new("o", "r", 4)] = new(PrStatus.Closed),
            },
            resolution.States);

        // The 7 <- 8 stack has no requested member, so it is not returned.
        var chain = Assert.Single(resolution.Chains);
        Assert.Equal((ChainKind.Stack, "o/r", "main"), (chain.Kind, chain.Slug, chain.Base));
        Assert.Equal([new PrRef("o", "r", 1), new PrRef("o", "r", 2)], chain.Members);
        Assert.Empty(resolution.Errors);
    }

    [Fact]
    public async Task A_missing_pr_is_unknown_but_the_rest_resolve()
    {
        var fake = Fake(("o/r", new FakePrRepo(States(1, "OPEN"))));

        var states = await PrResolver.ResolveStatesAsync(fake, Refs("o/r#1", "o/r#99999"), Token);

        Assert.Equal(new PrState(PrStatus.Open), states[new PrRef("o", "r", 1)]);
        Assert.Equal(
            new PrState(PrStatus.Unknown, "Could not resolve to a PullRequest with the number of 99999."),
            states[new PrRef("o", "r", 99999)]);
    }

    [Fact]
    public async Task A_whole_repo_failure_is_unknown_with_no_chains()
    {
        var fake = Fake(("o/r", new FakePrRepo(States(1, "OPEN"))), ("o/gone", new FakePrRepo(Fail: "Could not resolve to a Repository")));

        var resolution = await PrResolver.ResolveAsync(fake, Refs("o/r#1", "o/gone#2"), cancellationToken: Token);

        Assert.Equal(new PrState(PrStatus.Open), resolution.States[new PrRef("o", "r", 1)]);
        Assert.Equal(PrStatus.Unknown, resolution.States[new PrRef("o", "gone", 2)].Status);
        Assert.Equal(["o/gone: Could not resolve to a Repository"], resolution.Errors);
        Assert.Equal(new Dictionary<string, string> { ["o/gone"] = "Could not resolve to a Repository" }, resolution.FailedRepos);
    }

    [Fact]
    public async Task An_unreadable_open_list_fails_the_repo_instead_of_building_partial_chains()
    {
        var fake = Fake(("o/r", new FakePrRepo(States(1, "OPEN"), [Node(1, "a"), JsonNode.Parse("""{"headRefName": "no-number"}""")])));

        var resolution = await PrResolver.ResolveAsync(fake, Refs("o/r#1"), cancellationToken: Token);

        Assert.Empty(resolution.Chains);
        Assert.Equal(["o/r: unreadable open-PR list"], resolution.Errors);
        // The lookup that rode on the failed page still counts.
        Assert.Equal(new PrState(PrStatus.Open), resolution.States[new PrRef("o", "r", 1)]);
    }

    [Fact]
    public async Task Gh_missing_is_unknown_everywhere()
    {
        var missing = new FakeGraphQlPrs(new Dictionary<string, FakePrRepo>()) { Missing = true };

        var resolution = await PrResolver.ResolveAsync(missing, Refs("o/r#1"), cancellationToken: Token);

        Assert.Equal(new Dictionary<PrRef, PrState> { [new("o", "r", 1)] = new(PrStatus.Unknown, "gh is not on PATH") }, resolution.States);
        Assert.Equal(["o/r: gh is not on PATH"], resolution.Errors);
        Assert.Empty(missing.Queries);
    }

    [Theory]
    [InlineData(124, "", "gh timed out after 30s", "gh timed out after 30s")]
    [InlineData(1, "not json", "", "gh exited 1")]
    [InlineData(1, """{"data": {"repository": []}}""", "", "gh exited 1")]
    [InlineData(1, """{"data": null, "errors": [{"type": "FORBIDDEN"}, {"message": "rate limited"}, {}]}""", "", "FORBIDDEN; rate limited; error")]
    [InlineData(1, """{"data": {"repository": null}, "errors": [{"path": null}]}""", "bad", "bad")]
    [InlineData(1, """{"errors": [{"path": [true]}]}""", "worse", "worse")]
    [InlineData(1, """{"data": {"repository": {"open": {"pageInfo": null}}}}""", "", "not found")]
    public async Task A_failed_query_is_unknown_with_the_best_reason(int code, string stdout, string stderr, string reason)
    {
        var github = new Answering(new Ran(code, stdout, stderr));

        var states = await PrResolver.ResolveStatesAsync(github, Refs("o/r#1"), Token);

        Assert.Equal(new PrState(PrStatus.Unknown, reason), states[new PrRef("o", "r", 1)]);
    }

    [Theory]
    [InlineData("""{"state": "DRAFT"}""", PrStatus.Unknown, "unexpected state 'DRAFT'")]
    [InlineData("""{"state": null}""", PrStatus.Unknown, "unexpected state ''")]
    [InlineData("""{"state": "OPEN", "merged": true}""", PrStatus.Merged, "")]
    [InlineData("""{"state": "MERGED", "merged": false}""", PrStatus.Merged, "")]
    [InlineData("""{"state": 5}""", PrStatus.Unknown, "not found")]
    [InlineData("""[]""", PrStatus.Unknown, "not found")]
    public async Task A_lookup_is_read_by_its_merged_flag_then_its_state(string lookup, PrStatus status, string error)
    {
        var reply = $$$"""{"data": {"repository": {"open": null, "pr1": {{{lookup}}}}}, "errors": [{"path": ["repository", 3], "message": "odd"}]}""";
        var github = new Answering(new Ran(0, reply, string.Empty));

        var resolution = await PrResolver.ResolveAsync(github, Refs("o/r#1"), cancellationToken: Token);

        Assert.Equal(new PrState(status, error), resolution.States[new PrRef("o", "r", 1)]);
        Assert.Empty(resolution.Errors);
    }

    [Fact]
    public async Task The_open_list_is_paged_before_chains_are_built()
    {
        // The stack's two members land on different pages.
        JsonNode?[] nodes = [.. Enumerable.Range(10, 5).Select(n => Node(n, $"h{n}")), Node(2, "b", "a"), Node(1, "a"), null];
        var fake = Fake(("o/r", new FakePrRepo(States(1, "OPEN", 2, "OPEN"), nodes, PageSize: 3)));

        var resolution = await PrResolver.ResolveAsync(fake, Refs("o/r#2"), cancellationToken: Token);

        Assert.Equal(3, fake.Queries.Count);
        Assert.Equal([new PrRef("o", "r", 1), new PrRef("o", "r", 2)], Assert.Single(resolution.Chains).Members);
        // Lookups ride on the first page only.
        Assert.Contains("pr2:", fake.Queries[0], StringComparison.Ordinal);
        Assert.All(fake.Queries.Skip(1), query => Assert.DoesNotContain("pr2:", query, StringComparison.Ordinal));
        Assert.Equal(["", "3", "6"], fake.Variables.Select(variables => variables["after"]?.GetValue<string>() ?? string.Empty));
    }

    [Fact]
    public async Task A_repo_with_too_many_open_prs_gives_up_without_chains()
    {
        JsonNode?[] nodes = [.. Enumerable.Range(1, PrResolver.MaxOpenPages + 1).Select(n => Node(n, $"h{n}"))];
        var fake = Fake(("o/r", new FakePrRepo(States(1, "OPEN"), nodes, PageSize: 1)));

        var resolution = await PrResolver.ResolveAsync(fake, Refs("o/r#1"), cancellationToken: Token);

        Assert.Equal(["o/r: more than 1000 open PRs"], resolution.Errors);
        Assert.Equal(new PrState(PrStatus.Open), resolution.States[new PrRef("o", "r", 1)]);
        Assert.Equal(PrResolver.MaxOpenPages, fake.Queries.Count);
    }

    [Fact]
    public async Task Lookups_are_chunked()
    {
        var fake = Fake(("o/r", new FakePrRepo(Enumerable.Range(1, 5).ToDictionary(n => n, _ => "MERGED"))));

        var resolution = await PrResolver.ResolveAsync(fake, Refs("o/r#1", "o/r#2", "o/r#3", "o/r#4", "o/r#5"), lookupsPerQuery: 2, cancellationToken: Token);

        Assert.Equal(5, resolution.States.Count);
        Assert.All(resolution.States.Values, state => Assert.Equal(PrStatus.Merged, state.Status));
        Assert.Equal(3, fake.Queries.Count);
        Assert.Contains("open:", fake.Queries[0], StringComparison.Ordinal);
        Assert.All(fake.Queries.Skip(1), query => Assert.DoesNotContain("open:", query, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repos_are_queried_separately()
    {
        var fake = Fake(("o/a", new FakePrRepo(States(1, "OPEN"))), ("o/b", new FakePrRepo(States(1, "MERGED"))));

        var states = await PrResolver.ResolveStatesAsync(fake, Refs("o/a#1", "o/b#1"), Token);

        Assert.Equal(
            new Dictionary<PrRef, PrState> { [new("o", "a", 1)] = new(PrStatus.Open), [new("o", "b", 1)] = new(PrStatus.Merged) },
            states);
        Assert.Equal(2, fake.Queries.Count);
    }

    [Fact]
    public async Task No_refs_runs_nothing()
    {
        var fake = Fake();

        var resolution = await PrResolver.ResolveAsync(fake, [], cancellationToken: Token);

        Assert.Empty(resolution.States);
        Assert.Empty(resolution.Chains);
        Assert.Empty(fake.Queries);
        Assert.Empty(Resolution.Empty.States);
    }

    [Fact]
    public async Task A_repo_with_no_wanted_prs_still_reads_its_chains()
    {
        var fake = Fake(("o/r", new FakePrRepo(OpenNodes: [Node(1, "a"), Node(2, "b", "a")])));

        var repo = await PrResolver.ResolveRepoAsync(fake, "o", "r", [], 100, Token);

        Assert.Empty(repo.States);
        Assert.Equal([new Chain(ChainKind.Stack, "main", [1, 2])], repo.Chains);
        Assert.Equal(string.Empty, repo.Error);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PrResolver.ResolveRepoAsync(fake, "o", "r", [1], 0, Token));
    }

    [Fact]
    public void A_gh_exception_carries_its_message_and_cause()
    {
        var cause = new InvalidOperationException("inner");

        Assert.NotNull(new GhException().Message);
        Assert.Equal("m", new GhException("m").Message);
        Assert.Same(cause, new GhException("m", cause).InnerException);
    }

    private static T? None<T>()
        where T : class => null;

    private static OpenPr Pr(int number, string head, string @base = "main") => new(number, head, @base);

    private static OpenPr Labelled(OpenPr pr, params (string Label, string? At)[] labels) =>
        pr with
        {
            Labels = labels.Select(label => label.Label).ToHashSet(StringComparer.Ordinal),
            LabeledAt = labels.Where(label => label.At is not null).ToDictionary(label => label.Label, label => label.At!, StringComparer.Ordinal),
        };

    private static JsonObject Node(int number, string head, string @base = "main") =>
        new JsonObject { ["number"] = number, ["headRefName"] = head, ["baseRefName"] = @base, ["createdAt"] = "2026-01-01T00:00:00Z" };

    private static Dictionary<int, string> States(params object[] pairs) =>
        Enumerable.Range(0, pairs.Length / 2).ToDictionary(i => (int)pairs[2 * i], i => (string)pairs[(2 * i) + 1]);

    private static List<PrRef> Refs(params string[] texts) => [.. texts.Select(PrRef.Parse)];

    private static FakeGraphQlPrs Fake(params (string Slug, FakePrRepo Repo)[] repos) =>
        new(repos.ToDictionary(repo => repo.Slug, repo => repo.Repo, StringComparer.Ordinal));

    private sealed class Answering(Ran answer) : IGitHubApi
    {
        public Task<Ran> CallAsync(ApiRequest request, CancellationToken cancellationToken = default) => Task.FromResult(answer);
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Rig.Core.Orca;
using Rig.Core.Processes;

namespace Rig.Specs.StepDefinitions;

/// <summary>
/// The base-ref scenarios' world: one or more <c>base_ref</c> reads, in memory with scripted git output or on disk
/// against real git. Both executors feed the production <see cref="OrcaBaseRef"/> and <see cref="OrcaDoctor"/>.
/// </summary>
public sealed class OrcaBaseRefWorld : IDisposable
{
    private readonly SpecScratch _scratch = new();
    private readonly List<(string Name, string Path, string BaseRef)> _repos = [];
    private RecordingProc? _scripted;
    private CountingProc? _counting;

    /// <summary>What the doctor found for the repos read.</summary>
    public IReadOnlyList<OrcaFinding> Findings { get; private set; } = [];

    /// <summary>The repos read, by Orca display name.</summary>
    public IReadOnlyList<string> RepoNames => [.. _repos.Select(repo => repo.Name)];

    /// <summary>How many git calls the read made.</summary>
    public int Calls => _counting?.Count ?? _scripted?.Count ?? 0;

    /// <summary>The git command lines the read made, for a failed call-count assertion.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Requests => _counting?.Requests ?? _scripted?.Requests ?? [];

    /// <summary>How long the read took.</summary>
    public TimeSpan Duration { get; private set; }

    /// <summary>Builds one repo in memory or on disk, arranged to match <paramref name="state"/>.</summary>
    public async Task BuildAsync(BaseRefState state, bool real)
    {
        _repos.Clear();
        Findings = [];
        _counting = null;
        _scripted = null;
        if (!real)
        {
            _scripted = new RecordingProc(request => Scripted(state, request));
            _repos.Add(("rig", "/specs/rig", "origin/main"));
            return;
        }

        var repo = await SpecBaseRefRepo.CreateAsync(_scratch, new SystemProc(), "rig");
        _counting = new CountingProc(new SystemProc());
        switch (state)
        {
            case BaseRefState.Level:
                break;
            case BaseRefState.Behind3:
                await repo.PushOriginAsync(3);
                await repo.FetchAsync("origin");
                break;
            case BaseRefState.Ahead2:
                await repo.CommitLocalAsync(2);
                break;
            case BaseRefState.OtherUpstream:
                await repo.SetUpstreamAsync("upstream");
                await repo.PushOriginAsync(2);
                await repo.FetchAsync("origin");
                break;
            case BaseRefState.MissingBranch:
                await repo.RenameMainAsync();
                break;
            case BaseRefState.MissingRemote:
                await repo.DeleteOriginMainAsync();
                break;
            default:
                throw new InvalidOperationException(
                    $"OrcaBaseRefWorld has no on-disk arrangement for {state}. Add one beside the other BaseRefState cases.");
        }

        _repos.Add(("rig", repo.Local, "origin/main"));
    }

    /// <summary>Builds <paramref name="count"/> real repos that are all level with origin/main.</summary>
    public async Task BuildManyAsync(int count)
    {
        _repos.Clear();
        Findings = [];
        _counting = new CountingProc(new SystemProc());
        _scripted = null;
        for (var index = 0; index < count; index++)
        {
            var repo = await SpecBaseRefRepo.CreateAsync(_scratch, new SystemProc(), $"rig-{index}");
            _repos.Add(($"rig-{index}", repo.Local, "origin/main"));
        }
    }

    /// <summary>Reads every repo, then asks the doctor to render its findings.</summary>
    public async Task ReadAsync(int maxConcurrency = 1)
    {
        IProc proc = _counting is { } counting ? counting : _scripted ?? throw new InvalidOperationException("no repos were built");
        var started = Stopwatch.GetTimestamp();
        var states = await OrcaBaseRef.InspectAsync(
            new Git(proc, _scratch.Path),
            _repos,
            TestContext.Current.CancellationToken,
            maxConcurrency);
        Duration = Stopwatch.GetElapsedTime(started);

        var reposNode = new JsonObject();
        foreach (var repo in _repos)
        {
            reposNode[repo.Name] = new JsonObject { ["base_ref"] = repo.BaseRef };
        }

        var policy = OrcaPolicy.Parse(new JsonObject { ["repos"] = reposNode }.ToJsonString());
        var registered = _repos.Select(repo => new OrcaRepo(repo.Path, repo.Name, WorktreeBaseRef: repo.BaseRef)).ToList();
        Findings = OrcaDoctor.Diagnose(
            policy,
            new OrcaSnapshot(null, registered, "/specs/profile"),
            new OrcaPaths("/specs/estate", "/specs/code", "/specs/home"),
            [],
            DateTimeOffset.UnixEpoch,
            states);
    }

    /// <summary>The <c>local base</c> finding for <paramref name="repo"/>, or null when there is nothing to compare.</summary>
    public OrcaFinding? BaseFinding(string repo = "rig") =>
        Findings.FirstOrDefault(finding => finding.Subject == $"repo {repo}" && finding.Check == "local base");

    /// <summary>Every finding, on one line, for a failed assertion.</summary>
    public string DescribeFindings() => Findings.Count == 0
        ? "(no findings)"
        : string.Join("; ", Findings.Select(finding => $"{finding.Subject}/{finding.Check}={finding.Status}: {finding.Detail}"));

    /// <inheritdoc />
    public void Dispose() => _scratch.Dispose();

    private static Ran Scripted(BaseRefState state, ProcRequest request)
    {
        if (request.Argv.Contains("for-each-ref"))
        {
            return new Ran(0, state switch
            {
                BaseRefState.Level => "main\torigin/main\t\n",
                BaseRefState.Behind3 => "main\torigin/main\t[behind 3]\n",
                BaseRefState.Ahead2 => "main\torigin/main\t[ahead 2]\n",
                BaseRefState.OtherUpstream => "main\tupstream/main\t\n",
                BaseRefState.MissingBranch => "trunk\torigin/main\t\n",
                BaseRefState.MissingRemote => "main\torigin/main\t[gone]\n",
                _ => throw new InvalidOperationException(
                    $"OrcaBaseRefWorld has no scripted for-each-ref output for {state}. Add one beside the other BaseRefState cases."),
            }, string.Empty);
        }

        if (request.Argv.Contains("rev-list"))
        {
            return state switch
            {
                BaseRefState.OtherUpstream => new Ran(0, "0\t2\n", string.Empty),
                BaseRefState.MissingRemote => new Ran(1, string.Empty, "no such ref"),
                _ => throw new InvalidOperationException(
                    $"The read made a rev-list fallback for {state}, which for-each-ref should have answered. "
                    + "This is a waterfall regression: the state's upstream must match the policy ref and carry its counts."),
            };
        }

        return new Ran(Ran.NotFound, string.Empty, $"unexpected git call: {string.Join(' ', request.Argv)}");
    }

    private sealed class RecordingProc(Func<ProcRequest, Ran> answer) : IProc
    {
        private readonly ConcurrentQueue<IReadOnlyList<string>> _requests = new();
        private int _calls;

        public int Count => Volatile.Read(ref _calls);

        public IReadOnlyList<IReadOnlyList<string>> Requests => [.. _requests];

        public Ran Run(ProcRequest request)
        {
            _ = Interlocked.Increment(ref _calls);
            _requests.Enqueue(request.Argv);
            return answer(request);
        }

        public Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default) => Task.FromResult(Run(request));
    }

    private sealed class CountingProc(IProc inner) : IProc
    {
        private readonly ConcurrentQueue<IReadOnlyList<string>> _requests = new();
        private int _calls;

        public int Count => Volatile.Read(ref _calls);

        public IReadOnlyList<IReadOnlyList<string>> Requests => [.. _requests];

        public Ran Run(ProcRequest request)
        {
            _ = Interlocked.Increment(ref _calls);
            _requests.Enqueue(request.Argv);
            return inner.Run(request);
        }

        public async Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            _requests.Enqueue(request.Argv);
            return await inner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

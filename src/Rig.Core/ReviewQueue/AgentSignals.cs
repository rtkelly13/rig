using Rig.Core.Estate;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Workspace;

namespace Rig.Core.ReviewQueue;

/// <summary>A repository slug, lowercased, and a branch in it: how an agent's verdict is found.</summary>
/// <param name="Repo">The slug, such as <c>owner/name</c>, lowercased.</param>
/// <param name="Branch">The branch the worktree has checked out.</param>
public sealed record AgentKey(string Repo, string Branch);

/// <summary>What the agent working on a PR last said about it, from <c>estate wrapup</c>'s state files.</summary>
/// <param name="Verdict"><c>done</c>, <c>pending</c>, or <c>asked</c> while the ask is unanswered.</param>
/// <param name="Detail">The pending reason, or the facts line rig sent with an unanswered ask.</param>
/// <param name="At">When the verdict was given, or the ask sent, UTC ISO 8601.</param>
/// <param name="Head">The commit the worktree was at then; the inbox compares it with the PR's head.</param>
/// <param name="Worktree">The worktree folder's name, to find the session.</param>
public sealed record AgentSignal(string Verdict, string Detail, string At, string Head, string Worktree);

/// <summary>A managed repository and the checkout its worktrees are listed from.</summary>
/// <param name="Repo">Its slug, lowercased.</param>
/// <param name="Root">Its local checkout; absent when the clone is not on this machine.</param>
public sealed record AgentRoot(string Repo, string Root);

/// <summary>
/// Reads the wrap-up verdicts <c>estate wrapup</c> records per worktree, keyed by repository and branch.
/// </summary>
public sealed class AgentSignals : IDisposable
{
    /// <summary>Seconds a scan is reused.</summary>
    public const double TtlSeconds = 30;

    private readonly Func<IReadOnlyList<AgentRoot>> _roots;
    private readonly Func<string, Task<IReadOnlyList<Worktree>>> _worktrees;
    private readonly Func<string, WrapupState?> _read;
    private readonly Func<double> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<AgentKey, AgentSignal> _cache = new Dictionary<AgentKey, AgentSignal>();
    private double _at = double.NegativeInfinity;

    /// <summary>Builds a scanner over its seams.</summary>
    /// <param name="roots">The managed repositories to scan.</param>
    /// <param name="worktrees">Lists a checkout's worktrees.</param>
    /// <param name="read">A worktree's wrap-up state, or null when it has none.</param>
    /// <param name="clock">The seconds clock the cache is measured against.</param>
    public AgentSignals(
        Func<IReadOnlyList<AgentRoot>> roots,
        Func<string, Task<IReadOnlyList<Worktree>>> worktrees,
        Func<string, WrapupState?> read,
        Func<double> clock)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(worktrees);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        _roots = roots;
        _worktrees = worktrees;
        _read = read;
        _clock = clock;
    }

    /// <summary>The real scanner for the estate a registry describes.</summary>
    /// <param name="registryFile">The managed-projects registry.</param>
    /// <param name="estateRoot">The personal estate folder, <c>&lt;code root&gt;/personal</c>.</param>
    /// <param name="git">Lists the worktrees, and names the running checkout's origin.</param>
    /// <param name="cancellationToken">Cancels the origin lookup.</param>
    /// <remarks>
    /// Asynchronous because naming the running checkout is a git call: blocking a startup thread on it holds the
    /// host's build for the process's 30-second git timeout, and starves the thread pool doing so.
    /// </remarks>
    public static async Task<AgentSignals> ForEstateAsync(
        string registryFile,
        string estateRoot,
        Git git,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registryFile);
        ArgumentNullException.ThrowIfNull(estateRoot);
        ArgumentNullException.ThrowIfNull(git);
        var scopes = EstateScopes.Load(Path.Join(Path.GetDirectoryName(registryFile), EstateScopes.FileName));
        var currentCheckout = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(registryFile)));
        string? currentSlug = null;
        if (currentCheckout is not null && Directory.Exists(currentCheckout))
        {
            try
            {
                var origin = (await git
                    .RunAsync(currentCheckout, ["config", "--get", "remote.origin.url"], cancellationToken: cancellationToken)
                    .ConfigureAwait(false)).OutOrEmpty;
                if (origin.Length > 0)
                {
                    currentSlug = (RepoSlug.FromRemote(origin) ?? RepoSlug.FromPath(origin))?.Key;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
            {
                // Fall back to canonical paths if git origin cannot be resolved
            }
        }

        return new AgentSignals(
            () => EstateRoots(registryFile, estateRoot, scopes, currentCheckout, currentSlug),
            async root => await Worktree.ListAsync(git, root).ConfigureAwait(false),
            worktree => new FileWrapupStore(git).Read(worktree),
            () => Environment.TickCount64 / 1000.0);
    }

    /// <summary>Where each enabled managed repository is cloned, keyed by its lowercased slug.</summary>
    /// <param name="registryFile">The managed-projects registry.</param>
    /// <param name="estateRoot">The personal estate folder.</param>
    /// <param name="scopes">Where repositories an estate org owns are cloned.</param>
    /// <param name="currentCheckout">The running checkout containing the registry, when running outside the estate.</param>
    /// <param name="currentSlug">The lowercased slug of <paramref name="currentCheckout"/>'s origin.</param>
    public static IReadOnlyList<AgentRoot> EstateRoots(
        string registryFile,
        string estateRoot,
        EstateScopes scopes,
        string? currentCheckout = null,
        string? currentSlug = null)
    {
        ArgumentNullException.ThrowIfNull(registryFile);
        ArgumentNullException.ThrowIfNull(estateRoot);
        ArgumentNullException.ThrowIfNull(scopes);
        currentCheckout ??= Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(registryFile)));
        var roots = new List<AgentRoot>();
        foreach (var spec in ManagedProjects.Load(registryFile).Where(spec => spec.Enabled))
        {
            if ((RepoSlug.FromRemote(spec.Repo) ?? RepoSlug.FromPath(spec.Repo)) is not { } slug)
            {
                continue;
            }

            var root = currentSlug is not null && slug.Key.Equals(currentSlug, StringComparison.OrdinalIgnoreCase) && currentCheckout is not null
                ? currentCheckout
                : scopes.ProjectDir(estateRoot, spec.Name, spec.Repo);

            roots.Add(new AgentRoot(slug.Key, root));
        }

        return roots;
    }

    /// <summary>
    /// The newest verdict per repository and branch, cached for <see cref="TtlSeconds"/>. A worktree without a record,
    /// or with one that cannot be read, contributes nothing rather than failing the inbox.
    /// </summary>
    public async Task<IReadOnlyDictionary<AgentKey, AgentSignal>> AllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock();
            if (now - _at < TtlSeconds)
            {
                return _cache;
            }

            var found = new Dictionary<AgentKey, AgentSignal>();
            foreach (var root in _roots().Where(root => Directory.Exists(root.Root)))
            {
                foreach (var worktree in await _worktrees(root.Root).ConfigureAwait(false))
                {
                    if (worktree.Bare || worktree.Branch.Length == 0 || _read(worktree.Path) is not { } state)
                    {
                        continue;
                    }

                    var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(worktree.Path));
                    if (Latest(state, name) is { } signal)
                    {
                        found[new AgentKey(root.Repo, worktree.Branch)] = signal;
                    }
                }
            }

            _cache = found;
            _at = now;
            return found;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>The signal recorded for <paramref name="branch"/>, or null when there is none.</summary>
    public static AgentSignal? ForPr(IReadOnlyDictionary<AgentKey, AgentSignal> found, string repo, string branch)
    {
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(branch);
        return branch.Length == 0 ? null : found.GetValueOrDefault(new AgentKey(repo.ToLowerInvariant(), branch));
    }

    /// <summary>The newest ask or answer in <paramref name="state"/>, across its panes.</summary>
    public static AgentSignal? Latest(WrapupState state, string worktree)
    {
        ArgumentNullException.ThrowIfNull(state);
        var ask = state.Asks.Count == 0 ? null : state.Asks.MaxBy(item => When(item.At));
        var answer = state.Answers.Count == 0 ? null : state.Answers.MaxBy(item => When(item.At));
        if (answer is not null && (ask is null || When(answer.At) >= When(ask.At)))
        {
            return new AgentSignal(answer.Verdict == "done" ? "done" : "pending", answer.Detail, answer.At, answer.Head, worktree);
        }

        return ask is null ? null : new AgentSignal("asked", ask.Facts, ask.At, ask.Head, worktree);
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    // A timestamp that cannot be read sorts before every one that can.
    private static DateTimeOffset When(string text) =>
        WrapupStamps.Parse(text) ?? DateTimeOffset.MinValue;
}

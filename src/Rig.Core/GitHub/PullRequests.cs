using System.Globalization;
using System.Text.RegularExpressions;

namespace Rig.Core.GitHub;

/// <summary>A pull request's <c>owner/repo#n</c>, lowercased so two spellings of one PR are equal and sort together.</summary>
public sealed partial record PrRef : IComparable<PrRef>
{
    /// <summary>Lowercases <paramref name="owner"/> and <paramref name="repo"/>.</summary>
    public PrRef(string owner, string repo, int number)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);
        Owner = owner.ToLowerInvariant();
        Repo = repo.ToLowerInvariant();
        Number = number;
    }

    /// <summary>The owner, lowercase.</summary>
    public string Owner { get; }

    /// <summary>The repository name, lowercase.</summary>
    public string Repo { get; }

    /// <summary>The PR number.</summary>
    public int Number { get; }

    /// <summary><c>owner/repo</c>.</summary>
    public string Slug => $"{Owner}/{Repo}";

    /// <summary>Like <see cref="TryParse"/>, but throws <see cref="FormatException"/> for anything that is not <c>owner/repo#n</c>.</summary>
    public static PrRef Parse(string text) =>
        TryParse(text) ?? throw new FormatException($"not an owner/repo#n reference: {Vault.PyText.Repr(text)}");

    /// <summary><c>owner/repo#n</c>, lowercased; null for anything else, so nothing unvalidated reaches a query.</summary>
    public static PrRef? TryParse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = RefPattern().Match(text.Trim().ToLowerInvariant());
        return match.Success
            ? new PrRef(match.Groups[1].Value, match.Groups[2].Value, int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture))
            : null;
    }

    /// <inheritdoc/>
    public int CompareTo(PrRef? other)
    {
        if (other is null)
        {
            return 1;
        }

        var byOwner = string.CompareOrdinal(Owner, other.Owner);
        if (byOwner != 0)
        {
            return byOwner;
        }

        var byRepo = string.CompareOrdinal(Repo, other.Repo);
        return byRepo != 0 ? byRepo : Number.CompareTo(other.Number);
    }

    /// <summary>Sorts before.</summary>
    public static bool operator <(PrRef? left, PrRef? right) => Compare(left, right) < 0;

    /// <summary>Sorts before or equal.</summary>
    public static bool operator <=(PrRef? left, PrRef? right) => Compare(left, right) <= 0;

    /// <summary>Sorts after.</summary>
    public static bool operator >(PrRef? left, PrRef? right) => Compare(left, right) > 0;

    /// <summary>Sorts after or equal.</summary>
    public static bool operator >=(PrRef? left, PrRef? right) => Compare(left, right) >= 0;

    /// <inheritdoc/>
    public override string ToString() => $"{Slug}#{Number}";

    private static int Compare(PrRef? left, PrRef? right) => left is null ? (right is null ? 0 : -1) : left.CompareTo(right);

    [GeneratedRegex(@"\A([a-z0-9-]+)/([a-z0-9._-]+)#([1-9][0-9]{0,8})\z")]
    private static partial Regex RefPattern();
}

/// <summary>Where a PR stands. <see cref="Unknown"/> is any PR GitHub could not answer for.</summary>
public enum PrStatus
{
    /// <summary>Open.</summary>
    Open,

    /// <summary>Merged.</summary>
    Merged,

    /// <summary>Closed without merging.</summary>
    Closed,

    /// <summary>Not resolved; <see cref="PrState.Error"/> says why.</summary>
    Unknown,
}

/// <summary>What a PR asks of the viewer, from the same lookup that reads its state.</summary>
/// <param name="Additions">Lines added.</param>
/// <param name="Deletions">Lines deleted.</param>
/// <param name="ChangedFiles">Files changed.</param>
/// <param name="Draft">A draft.</param>
/// <param name="ReviewDecision"><c>APPROVED</c>, <c>CHANGES_REQUESTED</c>, <c>REVIEW_REQUIRED</c>, or "" when no review is required.</param>
/// <param name="Checks">The head commit's check rollup (<c>SUCCESS</c>, <c>FAILURE</c>, <c>ERROR</c>, <c>PENDING</c>, <c>EXPECTED</c>) or "".</param>
/// <param name="AuthoredByViewer">The viewer wrote it.</param>
/// <param name="ReviewRequested">The viewer is personally requested; team requests are not attributed to the viewer.</param>
public sealed record PrDetails(
    int Additions = 0,
    int Deletions = 0,
    int ChangedFiles = 0,
    bool Draft = false,
    string ReviewDecision = "",
    string Checks = "",
    bool AuthoredByViewer = false,
    bool ReviewRequested = false)
{
    /// <summary>Additions plus deletions.</summary>
    public int ChangedLines => Additions + Deletions;

    /// <summary>The rollup failed or errored.</summary>
    public bool ChecksFailing => Checks is "FAILURE" or "ERROR";
}

/// <summary>A PR's state. Compares by status and error alone, never by <see cref="Details"/>.</summary>
/// <param name="Status">Where it stands.</param>
/// <param name="Error">Why it is unknown; "" otherwise.</param>
/// <param name="Details">What the lookup said about it; null when the lookup failed.</param>
public sealed record PrState(PrStatus Status, string Error = "", PrDetails? Details = null)
{
    /// <inheritdoc/>
    public bool Equals(PrState? other) => other is not null && Status == other.Status && Error == other.Error;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Status, Error);
}

/// <summary>One open PR as the chain builders need it.</summary>
/// <param name="Number">The PR number.</param>
/// <param name="Head">Its head branch.</param>
/// <param name="Base">Its base branch.</param>
/// <param name="CreatedAt">When it was opened, as GitHub's ISO timestamp.</param>
/// <param name="CrossRepo">Its head is in a fork.</param>
/// <param name="QueuePosition">Its place in the native merge queue, if queued.</param>
public sealed record OpenPr(int Number, string Head, string Base, string CreatedAt = "", bool CrossRepo = false, int? QueuePosition = null)
{
    /// <summary>Its labels, lowercase.</summary>
    public IReadOnlySet<string> Labels { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Label name to when it was last added.</summary>
    public IReadOnlyDictionary<string, string> LabeledAt { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>How a chain orders its members.</summary>
public enum ChainKind
{
    /// <summary>Each PR is based on the one before it.</summary>
    Stack,

    /// <summary>A merge queue: native, or by label.</summary>
    Queue,
}

/// <summary>PRs that merge in order, the next to merge first.</summary>
/// <param name="Kind">Stack or queue.</param>
/// <param name="Base">The base branch at the bottom.</param>
/// <param name="Members">PR numbers, lowest (next to merge) first.</param>
public sealed record Chain(ChainKind Kind, string Base, IReadOnlyList<int> Members)
{
    /// <inheritdoc/>
    public bool Equals(Chain? other) => other is not null && Kind == other.Kind && Base == other.Base && Members.SequenceEqual(other.Members);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, Base, Members.Count);
}

/// <summary>A <see cref="Chain"/> in a named repository.</summary>
/// <param name="Kind">Stack or queue.</param>
/// <param name="Slug">The lowercase <c>owner/repo</c>.</param>
/// <param name="Base">The base branch at the bottom.</param>
/// <param name="Members">The PRs, lowest first.</param>
public sealed record RepoChain(ChainKind Kind, string Slug, string Base, IReadOnlyList<PrRef> Members)
{
    /// <summary><paramref name="chain"/> in <paramref name="owner"/>/<paramref name="repo"/>.</summary>
    public static RepoChain Of(string owner, string repo, Chain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var members = chain.Members.Select(number => new PrRef(owner, repo, number)).ToList();
        return new RepoChain(chain.Kind, $"{owner}/{repo}".ToLowerInvariant(), chain.Base, members);
    }
}

/// <summary>PR states plus the chains that include a requested PR.</summary>
/// <param name="States">Each requested PR's state.</param>
/// <param name="Chains">Every chain with a requested member.</param>
/// <param name="FailedRepos">Repository slug to why it could not be resolved; its chains are unknowable, so none are returned for it.</param>
/// <param name="ElapsedMs">How long resolution took.</param>
public sealed record Resolution(
    IReadOnlyDictionary<PrRef, PrState> States,
    IReadOnlyList<RepoChain> Chains,
    IReadOnlyDictionary<string, string> FailedRepos,
    long ElapsedMs = 0)
{
    /// <summary>Nothing resolved: no states, chains or failures.</summary>
    public static Resolution Empty { get; } = new(new Dictionary<PrRef, PrState>(), [], new Dictionary<string, string>());

    /// <summary>One <c>slug: error</c> line per failed repository, by slug.</summary>
    public IReadOnlyList<string> Errors =>
        [.. FailedRepos.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}: {pair.Value}")];
}

/// <summary>One repository's answer.</summary>
/// <param name="States">PR number to state.</param>
/// <param name="Chains">The repository's chains; empty when it failed.</param>
/// <param name="Error">Why the repository could not be resolved; "" when it was.</param>
public sealed record RepoResolution(IReadOnlyDictionary<int, PrState> States, IReadOnlyList<Chain> Chains, string Error = "");

namespace Rig.Specs.StepDefinitions;

/// <summary>The local-branch shapes the Orca base-ref feature can arrange.</summary>
public enum BaseRefState
{
    /// <summary>Local main and origin/main are the same commit.</summary>
    Level,

    /// <summary>Origin/main has three commits local main does not.</summary>
    Behind3,

    /// <summary>Local main has two commits origin/main does not.</summary>
    Ahead2,

    /// <summary>Local main tracks upstream/main while the policy names origin/main.</summary>
    OtherUpstream,

    /// <summary>The policy branch does not exist locally.</summary>
    MissingBranch,

    /// <summary>Local main exists but origin/main does not.</summary>
    MissingRemote,
}

/// <summary>What the doctor should report for a <see cref="BaseRefState"/>.</summary>
public enum BaseRefStatus
{
    /// <summary>An <c>ok</c> local-base finding.</summary>
    Current,

    /// <summary>A <c>warn</c> local-base finding.</summary>
    Behind,

    /// <summary>An <c>ok</c> local-base finding that names the commits ahead.</summary>
    Ahead,

    /// <summary>No local-base finding: there is nothing to compare.</summary>
    Absent,
}

/// <summary>
/// Translates the feature's words to the enums the world switches on. An unknown word fails with the known words and
/// where to add the new one, rather than a step-binding error or a silent default.
/// </summary>
public static class BaseRefSpecState
{
    private static readonly IReadOnlyDictionary<string, BaseRefState> States = new Dictionary<string, BaseRefState>(StringComparer.Ordinal)
    {
        ["level"] = BaseRefState.Level,
        ["behind 3"] = BaseRefState.Behind3,
        ["ahead 2"] = BaseRefState.Ahead2,
        ["other upstream"] = BaseRefState.OtherUpstream,
        ["missing branch"] = BaseRefState.MissingBranch,
        ["missing remote"] = BaseRefState.MissingRemote,
    };

    private static readonly IReadOnlyDictionary<string, BaseRefStatus> Statuses = new Dictionary<string, BaseRefStatus>(StringComparer.Ordinal)
    {
        ["current"] = BaseRefStatus.Current,
        ["behind"] = BaseRefStatus.Behind,
        ["ahead"] = BaseRefStatus.Ahead,
        ["absent"] = BaseRefStatus.Absent,
    };

    /// <summary>The state words the feature may use, sorted for a stable error message.</summary>
    public static IReadOnlyList<string> KnownStates => [.. States.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The status words the feature may use, sorted for a stable error message.</summary>
    public static IReadOnlyList<string> KnownStatuses => [.. Statuses.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The state <paramref name="state"/> names.</summary>
    /// <exception cref="InvalidOperationException">The feature names a state no scenario arranges.</exception>
    public static BaseRefState ParseState(string state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return States.TryGetValue(state, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"The Orca base-ref feature names a state the specs do not know: \"{state}\". "
                + $"Known states: {string.Join(", ", KnownStates)}. "
                + "Add the state to BaseRefState and to OrcaBaseRefWorld's arrangement and scripted output.");
    }

    /// <summary>The status <paramref name="status"/> names.</summary>
    /// <exception cref="InvalidOperationException">The feature names a status no assertion handles.</exception>
    public static BaseRefStatus ParseStatus(string status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return Statuses.TryGetValue(status, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"The Orca base-ref feature names a status the specs do not assert: \"{status}\". "
                + $"Known statuses: {string.Join(", ", KnownStatuses)}. "
                + "Add the status to BaseRefStatus and to OrcaBaseRefSteps.ThenTheLocalBaseIs.");
    }
}

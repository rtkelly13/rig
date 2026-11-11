using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Governance;

/// <summary>
/// One difference between the declared and the observed state of one resource. <see cref="Kind"/> is <c>apply</c> (a
/// write <c>plan --apply</c> makes), <c>refused</c> (a safety gate stops the write until a person acts) or <c>note</c>
/// (worth knowing, never written and not drift). <see cref="Subject"/> names the thing inside the resource the change
/// is about (an environment, a label); it is empty when the resource is one thing.
/// </summary>
/// <param name="Resource">The resource's name.</param>
/// <param name="Action">What would be done.</param>
/// <param name="Detail">Why, or what exactly.</param>
/// <param name="Kind"><c>apply</c>, <c>refused</c> or <c>note</c>.</param>
/// <param name="Subject">The thing inside the resource, or empty.</param>
public sealed record Change(string Resource, string Action, string Detail, string Kind = Change.Apply, string Subject = "")
{
    /// <summary>A write <c>--apply</c> makes.</summary>
    public const string Apply = "apply";

    /// <summary>A write a safety gate refuses.</summary>
    public const string Refused = "refused";

    /// <summary>Worth knowing, never written.</summary>
    public const string Note = "note";

    /// <summary>Whether this is drift: anything but a note.</summary>
    public bool IsDrift => Kind != Note;

    /// <summary>The change as <c>plan --json</c> prints it.</summary>
    public JsonObject ToJson() => new()
    {
        ["resource"] = Resource,
        ["action"] = Action,
        ["detail"] = Detail,
        ["kind"] = Kind,
        ["subject"] = Subject,
    };
}

/// <summary>A repo to plan: its local checkout (workflows are read from it) and its GitHub <c>owner/name</c>.</summary>
/// <param name="Path">The checkout.</param>
/// <param name="Slug">Its GitHub <c>owner/name</c>.</param>
public sealed record Target(string Path, string Slug);

/// <summary>GitHub could not be read, so no plan can be trusted for this resource.</summary>
public sealed class GitHubReadException : Exception
{
    /// <summary>A failed read with no reason given.</summary>
    public GitHubReadException()
    {
    }

    /// <summary>A failed read for <paramref name="message"/>.</summary>
    public GitHubReadException(string message)
        : base(message)
    {
    }

    /// <summary>A failed read for <paramref name="message"/>, caused by <paramref name="innerException"/>.</summary>
    public GitHubReadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One resource's plan for one repo, with the apply step bound to what was planned.</summary>
/// <param name="Resource">The resource's name.</param>
/// <param name="Changes">What differs.</param>
/// <param name="Apply">Makes the planned writes; returns one line per write that failed.</param>
/// <param name="Error">Why GitHub could not be read, or null.</param>
public sealed record Planned(string Resource, IReadOnlyList<Change> Changes, Func<CancellationToken, Task<IReadOnlyList<string>>> Apply, string? Error = null)
{
    /// <summary>An apply step that writes nothing.</summary>
    public static Task<IReadOnlyList<string>> Nothing(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>A resource with its state types erased, so resources of different kinds share one list.</summary>
public interface IPlanner
{
    /// <summary>The resource's name.</summary>
    string Name { get; }

    /// <summary>Plans the resource for <paramref name="target"/>.</summary>
    Task<Planned> PlanAsync(RepoPolicies policies, IGitHubApi api, Target target, CancellationToken cancellationToken = default);
}

/// <summary>
/// One kind of GitHub state: desired (from <c>config/repo_policies.json</c>, null when the repo declares nothing) →
/// observed (read through <see cref="IGitHubApi"/>) → diff (pure) → apply. A second plan after a successful apply is empty.
/// </summary>
/// <typeparam name="TDesired">What the policy declares.</typeparam>
/// <typeparam name="TObserved">What GitHub holds.</typeparam>
public abstract class Resource<TDesired, TObserved> : IPlanner
    where TDesired : class
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <summary>What the policy declares for <paramref name="target"/>, or null when it declares nothing.</summary>
    public abstract TDesired? Desired(RepoPolicies policies, Target target);

    /// <summary>What GitHub holds; throws <see cref="GitHubReadException"/> when it cannot be read.</summary>
    public abstract Task<TObserved> ReadAsync(IGitHubApi api, Target target, CancellationToken cancellationToken);

    /// <summary>The changes that bring <paramref name="observed"/> to <paramref name="desired"/>; no I/O.</summary>
    public abstract IReadOnlyList<Change> Diff(TDesired desired, TObserved observed);

    /// <summary>Makes the <c>apply</c> changes; returns one line per write that failed.</summary>
    public abstract Task<IReadOnlyList<string>> ApplyAsync(IGitHubApi api, Target target, TDesired desired, IReadOnlyList<Change> changes, CancellationToken cancellationToken);

    /// <inheritdoc />
    public async Task<Planned> PlanAsync(RepoPolicies policies, IGitHubApi api, Target target, CancellationToken cancellationToken = default)
    {
        if (Desired(policies, target) is not { } desired)
        {
            return new(Name, [], Planned.Nothing);
        }

        TObserved observed;
        try
        {
            observed = await ReadAsync(api, target, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubReadException e)
        {
            return new(Name, [], Planned.Nothing, e.Message);
        }

        var changes = Diff(desired, observed);
        return new(
            Name,
            changes,
            token => changes.Any(c => c.Kind == Change.Apply) ? ApplyAsync(api, target, desired, changes, token) : Planned.Nothing(token));
    }
}

/// <summary>Everything the plan found for one repo. This, not the rendered table, is what callers branch on.</summary>
/// <param name="Repo">The repo's <c>owner/name</c>, or its folder when it has none.</param>
/// <param name="Changes">Every change, resource by resource.</param>
/// <param name="Errors">Every read or write that failed.</param>
public sealed record RepoPlan(string Repo, IReadOnlyList<Change> Changes, IReadOnlyList<string> Errors)
{
    /// <summary>Whether anything but a note differs.</summary>
    public bool Drifted => Changes.Any(change => change.IsDrift);

    /// <summary>The plan as <c>plan --json</c> prints it.</summary>
    public JsonObject ToJson() => new()
    {
        ["repo"] = Repo,
        ["changes"] = new JsonArray([.. Changes.Select(JsonNode (c) => c.ToJson())]),
        ["errors"] = new JsonArray([.. Errors.Select(e => (JsonNode?)e)]),
    };
}

/// <summary>Reads and writes through the GitHub seam, failing the way every resource reports it.</summary>
public static class GitHubCalls
{
    /// <summary>GETs <paramref name="route"/> as JSON; throws <see cref="GitHubReadException"/> when the call or the parse fails.</summary>
    public static async Task<JsonNode> ReadJsonAsync(IGitHubApi api, string route, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);
        var ran = await api.CallAsync(ApiRequest.Get(route), cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            throw new GitHubReadException($"GET {route}: {Reason(ran)}");
        }

        return Parse(ran.Out) ?? throw new GitHubReadException($"GET {route}: unreadable response");
    }

    /// <summary>One write through the seam.</summary>
    public static Task<Ran> SendAsync(IGitHubApi api, string method, string route, string? body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);
        return api.CallAsync(new ApiRequest(method, route, body), cancellationToken);
    }

    /// <summary>Whether a failed call was GitHub's 404, which for some routes means "not configured".</summary>
    public static bool IsNotFound(Ran ran)
    {
        ArgumentNullException.ThrowIfNull(ran);
        return ran.Err.Contains("HTTP 404", StringComparison.Ordinal) || ran.Err.Contains("Not Found", StringComparison.Ordinal);
    }

    /// <summary>A failed call's error text, trimmed, or <c>exit N</c> when it printed none.</summary>
    public static string Reason(Ran ran)
    {
        ArgumentNullException.ThrowIfNull(ran);
        return ran.Err.Trim() is { Length: > 0 } err ? err : $"exit {ran.Code}";
    }

    /// <summary><paramref name="text"/> parsed, or null when it is not JSON.</summary>
    public static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The string at <paramref name="key"/>, or null when it is missing or not a string.</summary>
    public static string? Str(JsonNode? owner, string key) =>
        owner is JsonObject map && map[key] is { } value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>The boolean at <paramref name="key"/>, or null when it is missing or not a boolean.</summary>
    public static bool? Bool(JsonNode? owner, string key) =>
        owner is JsonObject map && map[key] is { } value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : null;

    /// <summary>The integer at <paramref name="key"/>, or null when it is missing or not an integer.</summary>
    public static long? Number(JsonNode? owner, string key) =>
        owner is JsonObject map && map[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out long number) ? number : null;
}

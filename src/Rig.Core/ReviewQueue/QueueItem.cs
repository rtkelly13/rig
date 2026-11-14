using System.Globalization;
using System.Text.Json.Serialization;

namespace Rig.Core.ReviewQueue;

/// <summary>The fields that determine an item's position in the local triage queue.</summary>
/// <param name="Repo">Repository slug.</param>
/// <param name="Number">PR or issue number.</param>
/// <param name="Kind"><c>pr</c> or <c>issue</c>.</param>
/// <param name="Title">The observed title.</param>
/// <param name="State">GitHub's state.</param>
/// <param name="Created">Creation timestamp, retained verbatim for the projection.</param>
/// <param name="Updated">Last observed update timestamp.</param>
/// <param name="Head">PR head SHA, empty for issues.</param>
/// <param name="Priority">P0 through P3; 4 means unlabelled.</param>
/// <param name="Readiness">Clean, behind, blocked, or draft/unknown (0 through 3).</param>
/// <param name="Lines">Changed lines.</param>
/// <param name="InScope">Whether the repository is still managed.</param>
/// <param name="Body">Description, absent in old snapshots.</param>
/// <param name="Facts">GitHub metadata; absence means not fetched, never success.</param>
public sealed record QueueItem(
    string Repo, int Number, string Kind, string Title, string State, string Created, string Updated,
    string Head = "", int Priority = 4, int Readiness = 3, int Lines = 0, bool InScope = true, string? Body = null, QueueFacts? Facts = null)
{
    /// <summary>The stable local key.</summary>
    [JsonIgnore]
    public string Reference => $"{Repo}#{Number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The canonical GitHub URL.</summary>
    [JsonIgnore]
    public Uri Url => new($"https://github.com/{Repo}/{(Kind == "pr" ? "pull" : "issues")}/{Number.ToString(CultureInfo.InvariantCulture)}");
}

/// <summary>The existing, clock-independent priority/readiness/size/age order.</summary>
public static class QueueOrder
{
    /// <summary>Sorts one kind of item; callers place the PR queue before the issue queue.</summary>
    public static IOrderedEnumerable<QueueItem> Sort(IEnumerable<QueueItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.OrderBy(item => item.Priority)
            .ThenBy(item => item.Readiness)
            .ThenBy(item => item.Lines)
            .ThenBy(item => DateTimeOffset.Parse(item.Created, CultureInfo.InvariantCulture))
            .ThenBy(item => item.Repo, StringComparer.Ordinal)
            .ThenBy(item => item.Number);
    }
}

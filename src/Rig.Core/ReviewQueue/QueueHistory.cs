using System.Globalization;
using System.Text.Json.Serialization;

namespace Rig.Core.ReviewQueue;

/// <summary>An append-only local or imported event.</summary>
/// <param name="Id">Database ordering key.</param>
/// <param name="At">Timestamp.</param>
/// <param name="Action">Local action or github-prefixed observation.</param>
/// <param name="Head">Head SHA at the time.</param>
/// <param name="Note">Recorded text.</param>
/// <param name="Actor">Local user, rig, or GitHub login.</param>
public sealed record QueueEvent(long Id, string At, string Action, string Head, string Note, string Actor);

/// <summary>A remote discussion event, deduplicated by its source key.</summary>
/// <param name="Key">Source identity including edit version.</param>
/// <param name="At">Timestamp.</param>
/// <param name="Actor">GitHub login.</param>
/// <param name="Action">GitHub event kind.</param>
/// <param name="Head">Related head SHA.</param>
/// <param name="Note">Discussion body.</param>
public sealed record QueueActivity(string Key, string At, string Actor, string Action, string Head, string Note);

/// <summary>A local decision against a particular snapshot.</summary>
/// <param name="Repo">Repository slug.</param>
/// <param name="Number">Item number.</param>
/// <param name="Action">One of the supported local actions.</param>
/// <param name="Head">The head the user saw.</param>
/// <param name="Note">Optional note, at most 10,000 Unicode code points.</param>
public sealed record QueueInteraction(string Repo, int Number, string Action, string Head, string Note = "")
{
    /// <summary>Validate the same public input contract as the Python interaction decoder.</summary>
    public void Validate()
    {
        if (Number <= 0 || Note is null || Vault.UnicodeText.Length(Note) > 10000
            || Action is not ("opened" or "unread" or "archived" or "unarchived" or "review_requested" or "reviewed"
                or "changes_requested" or "deferred" or "resumed" or "note"))
        {
            throw new ArgumentException("Invalid triage interaction");
        }
    }
}

/// <summary>An observed item and all its history, in insertion order.</summary>
/// <param name="Item">Latest snapshot.</param>
/// <param name="Events">Append-only history.</param>
public sealed record QueueEntry(QueueItem Item, IReadOnlyList<QueueEvent> Events)
{
    /// <summary>The last defer/resume decision is defer; refreshing never clears it.</summary>
    [JsonIgnore]
    public bool Deferred => Events.LastOrDefault(e => e.Action is "deferred" or "resumed")?.Action == "deferred";

    /// <summary>Archive holds only for the same head, until a later human GitHub event or explicit unarchive.</summary>
    [JsonIgnore]
    public bool Archived
    {
        get
        {
            var decision = Events.LastOrDefault(e => e.Action is "archived" or "unarchived");
            return decision is { Action: "archived" } && decision.Head == Item.Head
                && !Events.Any(e => e.Action.StartsWith("github:", StringComparison.Ordinal)
                    && !e.Actor.Contains("[bot]", StringComparison.Ordinal)
                    && DateTimeOffset.Parse(e.At, CultureInfo.InvariantCulture) > DateTimeOffset.Parse(decision.At, CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Whether it appears in the active queue; history remains even when it does not.</summary>
    [JsonIgnore]
    public bool Active => Item.InScope && Item.State == "OPEN" && !Deferred && !Archived;
}

using System.Text.Json.Serialization;

namespace Rig.Core.ReviewQueue;

/// <summary>One observed check job.</summary>
/// <param name="Name">Check name.</param>
/// <param name="State">GitHub state.</param>
/// <param name="Link">Link as stored in existing snapshots.</param>
public sealed record QueueCheck(string Name, string State, [property: JsonPropertyName("url")] string Link);

/// <summary>A linked PR or issue.</summary>
/// <param name="Repo">Repository slug.</param>
/// <param name="Number">Item number.</param>
/// <param name="Title">Title.</param>
/// <param name="State">GitHub state.</param>
/// <param name="Kind">PR or issue.</param>
public sealed record RelatedItem(string Repo, int Number, string Title, string State, string Kind);

/// <summary>One member of a native GitHub stack.</summary>
/// <param name="Number">PR number.</param>
/// <param name="Title">PR title.</param>
/// <param name="State">GitHub state.</param>
/// <param name="Position">Position in the stack.</param>
public sealed record QueueStackMember(int Number, string Title, string State, int Position);

/// <summary>Native GitHub stack membership, when observed.</summary>
/// <param name="Number">Stack number.</param>
/// <param name="Size">Reported total size.</param>
/// <param name="Position">This PR's position.</param>
/// <param name="Base">Base branch.</param>
public sealed record StackInfo(int Number, int Size, int Position, string Base)
{
    /// <summary>Observed members.</summary>
    public IReadOnlyList<QueueStackMember> Members { get; init; } = [];
}

/// <summary>Backward-compatible GitHub facts; omitted collections default to empty, unknown states stay unknown.</summary>
public sealed record QueueFacts
{
    /// <summary>Author login.</summary>
    public string Author { get; init; } = "";
    /// <summary>The viewer authored it.</summary>
    public bool ViewerAuthored { get; init; }
    /// <summary>Assignee logins.</summary>
    public IReadOnlyList<string> Assignees { get; init; } = [];
    /// <summary>Label names.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];
    /// <summary>Milestone title.</summary>
    public string Milestone { get; init; } = "";
    /// <summary>Reported comment count.</summary>
    public int Comments { get; init; }
    /// <summary>GitHub's review decision.</summary>
    public string ReviewDecision { get; init; } = "";
    /// <summary>The viewer is requested to review.</summary>
    public bool ReviewRequested { get; init; }
    /// <summary>Requested reviewer logins or teams.</summary>
    public IReadOnlyList<string> Reviewers { get; init; } = [];
    /// <summary>A draft PR.</summary>
    public bool Draft { get; init; }
    /// <summary>GitHub mergeability, unknown until observed.</summary>
    public string Mergeable { get; init; } = "UNKNOWN";
    /// <summary>Base branch.</summary>
    public string Base { get; init; } = "";
    /// <summary>Head branch.</summary>
    public string Head { get; init; } = "";
    /// <summary>The PR head is in a fork.</summary>
    public bool CrossRepository { get; init; }
    /// <summary>Check rollup, unknown until observed.</summary>
    public string ChecksState { get; init; } = "UNKNOWN";
    /// <summary>Observed check jobs.</summary>
    public IReadOnlyList<QueueCheck> Checks { get; init; } = [];
    /// <summary>Total checks, null when not fetched.</summary>
    public int? ChecksTotal { get; init; }
    /// <summary>Unresolved threads.</summary>
    public int UnresolvedThreads { get; init; }
    /// <summary>Total threads.</summary>
    public int ThreadsTotal { get; init; }
    /// <summary>Issue closure reason.</summary>
    public string StateReason { get; init; } = "";
    /// <summary>Linked items.</summary>
    public IReadOnlyList<RelatedItem> Related { get; init; } = [];
    /// <summary>Stack membership, null when not fetched.</summary>
    public StackInfo? Stack { get; init; }
    /// <summary>Fields whose nested connection was truncated.</summary>
    public IReadOnlyList<string> Partial { get; init; } = [];
}

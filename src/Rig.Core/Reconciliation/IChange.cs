namespace Rig.Core.Reconciliation;

/// <summary>What happened to one subject during reconciliation.</summary>
public enum ChangeKind
{
    /// <summary>Observed state already matches desired state; nothing to do.</summary>
    Aligned,

    /// <summary>Observed state differs and the plan would change it; nothing has been touched.</summary>
    WouldChange,

    /// <summary>Observed state differed and the change was applied.</summary>
    Changed,

    /// <summary>The plan deliberately left the subject alone, because changing it was not provably safe.</summary>
    Refused,

    /// <summary>The change was attempted and failed, or state could not be read.</summary>
    Error,
}

/// <summary>
/// One typed row in a reconciliation plan: what the reconciler looked at, what it decided, and why.
/// </summary>
/// <remarks>
/// Rows are data, not rendered text. Callers branch on <see cref="Kind"/> and <see cref="Subject"/>; reports render
/// them without parsing them back.
/// </remarks>
public interface IChange
{
    /// <summary>The resource, path, branch or other subject the change is about.</summary>
    string Subject { get; }

    /// <summary>What reconciliation decided for <see cref="Subject"/>.</summary>
    ChangeKind Kind { get; }

    /// <summary>The command, reason or failure that explains the kind.</summary>
    string Detail { get; }
}

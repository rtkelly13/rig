namespace Rig.Core.Reconciliation;

/// <summary>
/// The kind of atomic change planned during reconciliation.
/// </summary>
public enum ChangeKind
{
    /// <summary>Resource is already in desired state; no mutation needed.</summary>
    NoOp = 0,

    /// <summary>Resource does not exist and must be created.</summary>
    Create = 1,

    /// <summary>Resource exists but has drifted from desired state and must be updated.</summary>
    Update = 2,

    /// <summary>Resource exists unexpectedly and must be removed.</summary>
    Delete = 3,

    /// <summary>The plan would change the resource, but a guard refused it; shown for a human, never applied.</summary>
    Refusal = 4,
}

/// <summary>
/// A planned atomic mutation or check against an external state resource.
/// </summary>
public interface IChange
{
    /// <summary>Target resource identifier, name, or path.</summary>
    string Target { get; }

    /// <summary>Human-readable explanation of what will change and why.</summary>
    string Description { get; }

    /// <summary>The mutation kind (NoOp, Create, Update, Delete).</summary>
    ChangeKind Kind { get; }
}

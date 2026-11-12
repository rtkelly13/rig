namespace Rig.Core.Chrome.MacOS;

/// <summary>
/// One decoded accessibility element of Chrome's browser UI. <see cref="Identity"/> is the element's CFHash: metadata
/// for telling elements apart, never a handle to act on. Two nodes are equal when every field and every child is, as
/// Python's frozen dataclass compared them, so the same element decoded twice compares equal.
/// </summary>
/// <param name="Identity">The element's CFHash; 0 for a node read without one.</param>
/// <param name="Role">AXRole.</param>
/// <param name="Subrole">AXSubrole.</param>
/// <param name="Title">AXTitle.</param>
/// <param name="Description">AXDescription.</param>
/// <param name="Identifier">AXIdentifier.</param>
/// <param name="Help">AXHelp.</param>
/// <param name="Selected">AXValue as a flag; null when it is not one.</param>
/// <param name="Expanded">AXExpanded; null when absent.</param>
/// <param name="Children">AXChildren, in order; none below a skipped role.</param>
public sealed record AxNode(
    long Identity = 0,
    string Role = "",
    string Subrole = "",
    string Title = "",
    string Description = "",
    string Identifier = "",
    string Help = "",
    bool? Selected = null,
    bool? Expanded = null,
    IReadOnlyList<AxNode>? Children = null)
{
    /// <summary>AXChildren, in order.</summary>
    public IReadOnlyList<AxNode> Children { get; init; } = Children ?? [];

    /// <summary>The name a person reads: the title, else the description.</summary>
    public string Name => Title.Length > 0 ? Title : Description;

    /// <inheritdoc/>
    public bool Equals(AxNode? other) =>
        other is not null
        && Identity == other.Identity
        && string.Equals(Role, other.Role, StringComparison.Ordinal)
        && string.Equals(Subrole, other.Subrole, StringComparison.Ordinal)
        && string.Equals(Title, other.Title, StringComparison.Ordinal)
        && string.Equals(Description, other.Description, StringComparison.Ordinal)
        && string.Equals(Identifier, other.Identifier, StringComparison.Ordinal)
        && string.Equals(Help, other.Help, StringComparison.Ordinal)
        && Selected == other.Selected
        && Expanded == other.Expanded
        && Children.SequenceEqual(other.Children);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Identity, Role, Title, Description, Children.Count);
}

/// <summary>Accessibility could not be read: no permission, a stale element, or an incomplete tree.</summary>
public class AccessibilityUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AccessibilityUnavailableException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public AccessibilityUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the failure behind it.</summary>
    public AccessibilityUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

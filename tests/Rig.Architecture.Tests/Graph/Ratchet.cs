namespace Rig.Architecture.Tests.Graph;

/// <summary>
/// Compares the violations that occur with the exceptions listed for them. <c>Added</c> is a violation nobody listed;
/// <c>Stale</c> is a listed exception that no longer occurs, which must be deleted so the list only shrinks.
/// </summary>
internal static class Ratchet
{
    public static (IReadOnlyList<string> Added, IReadOnlyList<string> Stale) Compare(
        IReadOnlyCollection<string> occurring,
        IReadOnlyCollection<string> listed) =>
        (occurring.Except(listed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
         listed.Except(occurring, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
}

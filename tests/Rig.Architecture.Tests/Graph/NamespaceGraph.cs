using ArchUnitNET.Domain;

namespace Rig.Architecture.Tests.Graph;

/// <summary>
/// The namespace graph of one root, read from compiled IL by ArchUnitNET, so any type reference counts, not only a
/// <c>using</c>. Edges are keyed by namespace: compiler-generated, nested and coverage types fold into the namespace
/// that declares them, and a source generator's <c>*.GeneratedCode</c> namespace into its parent, which keeps the
/// graph identical between plain and coverage runs.
/// </summary>
internal static class NamespaceGraph
{
    private const string Generated = ".GeneratedCode";

    public static IReadOnlyList<string> Namespaces(ArchUnitNET.Domain.Architecture architecture, string root) =>
        architecture.Types
            .Select(NamespaceOf)
            .Where(ns => Within(ns, root))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<NamespaceEdge> Edges(ArchUnitNET.Domain.Architecture architecture, string root) =>
        architecture.Types
            .Where(type => Within(NamespaceOf(type), root))
            .SelectMany(type => type.Dependencies.Select(dependency => (From: type, To: dependency.Target)))
            .Select(pair => (pair.From, pair.To, FromNs: NamespaceOf(pair.From), ToNs: NamespaceOf(pair.To)))
            .Where(pair => Within(pair.ToNs, root) && pair.FromNs != pair.ToNs)
            .GroupBy(pair => (pair.FromNs, pair.ToNs))
            .Select(group => new NamespaceEdge(
                group.Key.FromNs,
                group.Key.ToNs,
                group.Select(pair => $"{Outer(pair.From)} → {Outer(pair.To)}").Distinct().Order(StringComparer.Ordinal).ToList()))
            .OrderBy(edge => edge.Key, StringComparer.Ordinal)
            .ToList();

    // Generic parameters and some constructed types carry no namespace; they are never a rig namespace.
    private static string NamespaceOf(IType type)
    {
        var ns = type.Namespace?.FullName ?? "";
        return ns.EndsWith(Generated, StringComparison.Ordinal) ? ns[..^Generated.Length] : ns;
    }

    private static bool Within(string ns, string root) =>
        ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal);

    // The outermost declaring type, without generic arity: Shelving+<>c__DisplayClass`1 reads as Shelving.
    private static string Outer(IType type) => type.Name.Split('+')[0].Split('`')[0];
}

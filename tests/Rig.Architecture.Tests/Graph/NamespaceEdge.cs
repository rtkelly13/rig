namespace Rig.Architecture.Tests.Graph;

/// <summary>
/// Every reference from types in <paramref name="From"/> to types in <paramref name="To"/>, with the distinct type
/// pairs behind it (<c>Shelving → OrcaProject</c>) so a failure names what to move.
/// </summary>
internal sealed record NamespaceEdge(string From, string To, IReadOnlyList<string> Pairs)
{
    public string Key => $"{From} -> {To}";
}

namespace Rig.Architecture.Tests.Graph;

/// <summary>
/// Where each namespace under <paramref name="Root"/> sits. Keys are relative to the root (<c>""</c> is the root
/// itself, <c>"Desktop.Windows"</c> a child) and a namespace takes the role of its longest listed prefix, so a child
/// inherits its area's role unless it is listed too. An area is the first segment below the root.
/// </summary>
internal sealed record RoleMap(string Root, IReadOnlyDictionary<string, Role> Roles)
{
    public string Relative(string ns) =>
        ns == Root ? "" : ns.StartsWith(Root + ".", StringComparison.Ordinal) ? ns[(Root.Length + 1)..] : ns;

    public Role RoleOf(string ns)
    {
        var relative = Relative(ns);
        return Prefixes(relative).Select(prefix => Roles.GetValueOrDefault(prefix)).FirstOrDefault(role => role != Role.None);
    }

    public string AreaOf(string ns) => Relative(ns).Split('.')[0];

    // "A.B.C", "A.B", "A"; the root ("") has a role of its own and is not a prefix of its areas.
    private static IEnumerable<string> Prefixes(string relative) =>
        relative.Length == 0
            ? [""]
            : Enumerable.Range(1, relative.Count(c => c == '.') + 1)
                .Select(depth => string.Join('.', relative.Split('.').Take(depth)))
                .Reverse();
}

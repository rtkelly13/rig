using ArchUnitNET.Loader;
using Rig.Architecture.Tests.Graph;
using Rig.Core;

namespace Rig.Architecture.Tests;

/// <summary>
/// The target architecture's layering beyond cycles: every namespace has a role, features are independent, and
/// references never point up a role. Today's violations are the explicit <see cref="Exceptions"/>, the first of the
/// three steps a new gate lands in. A move that removes one deletes its line, and a line that no longer occurs fails
/// until it is deleted, so the list only shrinks. Edges are keyed by namespace; the failure lists the type pairs
/// behind each one, which is what to move.
/// </summary>
public sealed class StructureTests
{
    private static readonly ArchUnitNET.Domain.Architecture Compiled =
        new ArchLoader().LoadAssemblies(typeof(ExitCode).Assembly, System.Reflection.Assembly.Load("rig")).Build();

    // The type pairs behind each line are in the failure message when it is removed.
    private static readonly string[] Exceptions =
    [
        "Rig.Cli.Estate -> Rig.Cli.Workspace",
        "Rig.Cli.Forwarding -> Rig.Cli.Agent",
        "Rig.Cli.Governance -> Rig.Cli.Gh",
        "Rig.Cli.Governance -> Rig.Cli.Gh.Threads",
        "Rig.Cli.Governance -> Rig.Cli.Workspace",
        "Rig.Core.Chrome -> Rig.Core.Runtime",
        "Rig.Core.Chrome -> Rig.Core.Workspace",
        "Rig.Core.Chrome.Windows -> Rig.Core.Desktop",
        "Rig.Core.Chrome.Windows -> Rig.Core.Desktop.Windows",
        "Rig.Core.Connectivity -> Rig.Core.Workspace",
        "Rig.Core.Otel -> Rig.Core.Vault",
        "Rig.Core.ReviewQueue -> Rig.Core.Workspace",
        "Rig.Core.Tools -> Rig.Core.Jobs",
        "Rig.Core.Web -> Rig.Core.Workspace",
    ];

    public static TheoryData<string> Projects => [Roles.Core.Root, Roles.Cli.Root];

    private static RoleMap MapFor(string root) => root == Roles.Core.Root ? Roles.Core : Roles.Cli;

    [Theory]
    [MemberData(nameof(Projects))]
    public void Every_namespace_has_a_role(string root)
    {
        var roles = MapFor(root);
        var unplaced = NamespaceGraph.Namespaces(Compiled, root).Where(ns => roles.RoleOf(ns) == Role.None).ToList();

        Assert.True(unplaced.Count == 0, $"Place these in Graph/Roles.cs: {string.Join(", ", unplaced)}");
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void References_follow_the_layering_apart_from_the_listed_exceptions(string root)
    {
        var roles = MapFor(root);
        var violations = NamespaceGraph.Edges(Compiled, root)
            .Select(edge => (Edge: edge, Kind: Layering.Classify(roles, edge.From, edge.To)))
            .Where(classified => Layering.IsViolation(classified.Kind))
            .ToList();
        var (added, stale) = Ratchet.Compare(
            violations.Select(classified => classified.Edge.Key).ToList(),
            Exceptions.Where(key => key.StartsWith(root + ".", StringComparison.Ordinal)).ToList());
        var described = violations
            .Where(classified => added.Contains(classified.Edge.Key))
            .Select(classified => $"{classified.Edge.Key} ({classified.Kind}): {string.Join(", ", classified.Edge.Pairs)}");

        Assert.True(
            added.Count == 0 && stale.Count == 0,
            $"""
            New references against the layering; move the shared type down a layer:
            {string.Join('\n', described)}
            Exceptions that no longer occur; delete them:
            {string.Join('\n', stale)}
            """);
    }
}

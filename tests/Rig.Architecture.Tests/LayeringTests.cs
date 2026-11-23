using Rig.Architecture.Tests.Graph;

namespace Rig.Architecture.Tests;

/// <summary>The classifier and ratchet behind <see cref="StructureTests"/>, on a small made-up map.</summary>
public sealed class LayeringTests
{
    private static readonly RoleMap Map = new("App", new Dictionary<string, Role>
    {
        [""] = Role.Leaf,
        ["Text"] = Role.Leaf,
        ["Plans"] = Role.Domain,
        ["Git"] = Role.Adapter,
        ["Estate"] = Role.Feature,
        ["Desktop"] = Role.Feature,
        ["Desktop.Windows"] = Role.Adapter,
        ["Host"] = Role.CompositionRoot,
    });

    [Theory]
    [InlineData("App.Estate", "App.Git", EdgeKind.Allowed)]
    [InlineData("App.Git", "App.Plans", EdgeKind.Allowed)]
    [InlineData("App.Plans", "App", EdgeKind.Allowed)]
    [InlineData("App.Host", "App.Estate", EdgeKind.Allowed)]
    [InlineData("App.Desktop.Windows", "App.Desktop", EdgeKind.WithinArea)]
    [InlineData("App.Estate", "App.Desktop", EdgeKind.CrossesFeature)]
    [InlineData("App.Estate", "App.Desktop.Windows", EdgeKind.CrossesFeature)]
    [InlineData("App.Git", "App.Estate", EdgeKind.PointsUp)]
    [InlineData("App.Git", "App.Desktop.Windows", EdgeKind.PointsUp)]
    [InlineData("App.Text", "App.Plans", EdgeKind.PointsUp)]
    [InlineData("App.Estate", "App.Host", EdgeKind.PointsUp)]
    public void An_edge_is_classified_by_the_roles_at_each_end(string from, string to, EdgeKind expected)
    {
        Assert.Equal(expected, Layering.Classify(Map, from, to));
    }

    [Theory]
    [InlineData("App", Role.Leaf)]
    [InlineData("App.Estate.Shelving", Role.Feature)]
    [InlineData("App.Desktop.Windows.Uia", Role.Adapter)]
    [InlineData("App.Mail", Role.None)]
    public void A_namespace_takes_the_role_of_its_longest_listed_prefix(string ns, Role expected)
    {
        Assert.Equal(expected, Map.RoleOf(ns));
    }

    [Fact]
    public void A_violation_nobody_listed_is_added_and_a_listed_one_that_is_gone_is_stale()
    {
        var (added, stale) = Ratchet.Compare(["A -> B", "C -> D"], ["C -> D", "E -> F"]);

        Assert.Equal(["A -> B"], added);
        Assert.Equal(["E -> F"], stale);
    }
}

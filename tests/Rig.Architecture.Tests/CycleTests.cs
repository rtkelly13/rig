using ArchUnitNET.Fluent.Slices;
using ArchUnitNET.Loader;
using Rig.Architecture.Tests.Fixtures.CycleParent;
using Rig.Core;

namespace Rig.Architecture.Tests;

/// <summary>
/// The target architecture's rule that the graph is acyclic at every depth, namespaces included. <c>(**)</c> makes
/// every full namespace its own slice, the root <c>Rig.Core</c> too, so <c>Desktop</c> and
/// <c>Desktop.Windows</c> may not reference each other both ways. ArchUnitNET reads the compiled IL, so any type
/// reference counts, not only a <c>using</c>. A cycle almost always means a shared concept lives inside one feature;
/// the fix is to move it down a layer.
/// </summary>
public sealed class CycleTests
{
    private static readonly ArchUnitNET.Domain.Architecture Compiled =
        new ArchLoader().LoadAssemblies(typeof(ExitCode).Assembly, System.Reflection.Assembly.Load("rig")).Build();

    private static readonly ArchUnitNET.Domain.Architecture Fixture =
        new ArchLoader().LoadAssemblies(typeof(Parent).Assembly).Build();

    private static List<string> Cycles(ArchUnitNET.Domain.Architecture architecture, string root) =>
        SliceRuleDefinition.Slices().Matching($"{root}(**)").Should().BeFreeOfCycles()
            .Evaluate(architecture)
            .Where(result => !result.Passed)
            .Select(result => result.Description)
            .ToList();

    // Rig.Cli's own namespace is left out until the command base classes move out of it; the commands below it
    // inherit them, so it is in a cycle with every command group today.
    [Theory]
    [InlineData("Rig.Core")]
    [InlineData("Rig.Cli.")]
    public void Namespaces_are_free_of_cycles(string root)
    {
        var cycles = Cycles(Compiled, root);

        Assert.True(cycles.Count == 0, string.Join('\n', cycles));
    }

    [Fact]
    public void A_cycle_between_a_namespace_and_its_child_is_caught()
    {
        var cycles = Cycles(Fixture, "Rig.Architecture.Tests.Fixtures.");

        Assert.Single(cycles);
    }
}

using ArchUnitNET.Fluent.Slices;
using ArchUnitNET.Loader;
using Rig.Core;

namespace Rig.Architecture.Tests;

/// <summary>
/// The target architecture's rule that the graph is acyclic at every depth, namespaces included. Each namespace is a
/// slice, so <c>Desktop</c> and <c>Desktop.Windows</c> may not reference each other both ways. ArchUnitNET reads the
/// compiled IL, so any type reference counts, not only a <c>using</c>. A cycle almost always means a shared concept
/// lives inside one feature; the fix is to move it down a layer.
/// </summary>
public sealed class CycleTests
{
    private static readonly ArchUnitNET.Domain.Architecture Compiled =
        new ArchLoader().LoadAssemblies(typeof(ExitCode).Assembly, System.Reflection.Assembly.Load("rig")).Build();

    [Theory]
    [InlineData("Rig.Core.(*)..")]
    [InlineData("Rig.Cli.(*)..")]
    public void Namespaces_are_free_of_cycles(string slices)
    {
        var cycles = SliceRuleDefinition.Slices().Matching(slices).Should().BeFreeOfCycles()
            .Evaluate(Compiled)
            .Where(result => !result.Passed)
            .Select(result => result.Description)
            .ToList();

        Assert.True(cycles.Count == 0, string.Join('\n', cycles));
    }
}

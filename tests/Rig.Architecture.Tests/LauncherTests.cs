using NetArchTest.Rules;

namespace Rig.Architecture.Tests;

/// <summary>
/// <c>rigw</c> stays a hand-off: it lives in its own namespace and reaches nothing but rig's entry point, so whatever
/// a scheduled task runs is the same code <c>rig</c> runs.
/// </summary>
public sealed class LauncherTests
{
    private static readonly System.Reflection.Assembly Launcher = System.Reflection.Assembly.Load("rigw");

    [Theory]
    [InlineData("Rig.Core")]
    [InlineData("Rig.Web")]
    [InlineData("System.CommandLine")]
    public void The_launcher_reaches_nothing_but_rigs_entry_point(string library)
    {
        var result = Types.InAssembly(Launcher).ShouldNot().HaveDependencyOn(library).GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void The_launcher_is_one_type_under_its_namespace()
    {
        var types = Types.InAssembly(Launcher).That().ResideInNamespaceStartingWith("Rig").GetTypes().Select(type => type.FullName);
        var handsOff = Types.InAssembly(Launcher).That().ResideInNamespaceStartingWith("Rig").Should().HaveDependencyOn("Rig.Cli").GetResult();

        Assert.Equal(["Rig.Launcher.Launcher"], types);
        Assert.True(handsOff.IsSuccessful);
    }
}

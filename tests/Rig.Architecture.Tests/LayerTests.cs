using NetArchTest.Rules;
using Rig.Core;

namespace Rig.Architecture.Tests;

/// <summary>
/// rig's layer rules, which replace import-linter's contracts (ADR 0022). Project references stop a lower
/// layer referencing a higher one at all; these catch what references cannot, such as a core type reaching
/// for a console or command-line library it can see transitively.
/// </summary>
public sealed class LayerTests
{
    private static readonly System.Reflection.Assembly Core = typeof(ExitCode).Assembly;

    [Theory]
    [InlineData("Spectre.Console")]
    [InlineData("System.CommandLine")]
    [InlineData("Rig.Cli")]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Rig.Web")]
    public void Core_does_not_depend_on_presentation(string presentation)
    {
        var result = Types.InAssembly(Core).ShouldNot().HaveDependencyOn(presentation).GetResult();

        Assert.True(result.IsSuccessful, Offenders(result));
    }

    [Fact]
    public void Core_types_live_under_the_core_namespace()
    {
        // Coverage runs add coverlet's tracker types, and the compiler adds types such as <>z__ReadOnlyArray
        // for collection expressions and <PrivateImplementationDetails> structs (unmarked) for static arrays; none is rig's.
        // Types nested in those, such as <>z__ReadOnlySingleElementList`1/Enumerator, are the compiler's too.
        // CsWin32 generates its interop into Windows.Win32, internal, and offers no way to move it; types nested
        // in it have no namespace of their own.
        var result = Types.InAssembly(Core)
            .That().DoNotResideInNamespaceStartingWith("Coverlet")
            .And().MeetCustomRule(new NotGeneratedInterop())
            .And().MeetCustomRule(new NotCompilerOwned())
            .And().DoNotHaveNameStartingWith("__StaticArrayInitTypeSize")
            .Should().ResideInNamespaceStartingWith("Rig.Core")
            .GetResult();

        Assert.True(result.IsSuccessful, Offenders(result));
    }

    // Neither the type nor any type it is nested in is marked compiler-generated or carries a name only the compiler can give.
    private sealed class NotCompilerOwned : ICustomRule
    {
        public bool MeetsRule(Mono.Cecil.TypeDefinition type)
        {
            for (var current = type; current is not null; current = current.DeclaringType)
            {
                if (current.Name.StartsWith('<')
                    || current.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute"))
                {
                    return false;
                }
            }

            return true;
        }
    }

    // The outermost type around this one is not in CsWin32's Windows.Win32 namespace.
    private sealed class NotGeneratedInterop : ICustomRule
    {
        public bool MeetsRule(Mono.Cecil.TypeDefinition type)
        {
            var outer = type;
            while (outer.DeclaringType is { } declaring)
            {
                outer = declaring;
            }

            return !outer.Namespace.StartsWith("Windows.Win32", StringComparison.Ordinal);
        }
    }

    private static string Offenders(NetArchTest.Rules.TestResult result) =>
        string.Join(", ", result.FailingTypeNames ?? []);
}

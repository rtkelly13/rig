using DotMake.CommandLine;
using NetArchTest.Rules;
using Rig.Cli;
using Rig.Core;
using Rig.Core.Reconciliation;

namespace Rig.Architecture.Tests;

/// <summary>
/// Architectural quality gates and shape analysis enforcing ADR 0023, Spectre.Console presentation,
/// and reconciliation contracts across Rig.Cli and Rig.Core.
/// </summary>
public sealed class CliArchitectureTests
{
    private static readonly System.Reflection.Assembly CliAssembly = System.Reflection.Assembly.Load("rig");
    private static readonly System.Reflection.Assembly CoreAssembly = typeof(ExitCode).Assembly;

    [Theory]
    [InlineData("Spectre.Console")]
    [InlineData("System.CommandLine")]
    [InlineData("Rig.Cli")]
    public void Core_has_zero_dependencies_on_presentation_layer(string presentation)
    {
        var result = Types.InAssembly(CoreAssembly)
            .ShouldNot()
            .HaveDependencyOn(presentation)
            .GetResult();

        Assert.True(result.IsSuccessful, Offenders(result));
    }

    [Fact]
    public void Options_classes_must_carry_CliCommand_attribute()
    {
        var result = Types.InAssembly(CliAssembly)
            .That().ArePublic()
            .And().AreClasses()
            .And().HaveNameEndingWith("Options", StringComparison.Ordinal)
            .Should().MeetCustomRule(new HasCliCommandAttributeRule())
            .GetResult();

        Assert.True(result.IsSuccessful, Offenders(result));
    }

    [Fact]
    public void CliCommand_classes_must_implement_RunAsync_handler_contract()
    {
        var result = Types.InAssembly(CliAssembly)
            .That().MeetCustomRule(new HasCliCommandAttributeRule())
            .Should().MeetCustomRule(new ImplementsHandlerMethodRule())
            .GetResult();

        Assert.True(result.IsSuccessful, Offenders(result));
    }

    [Fact]
    public void Reconciliation_plans_must_implement_IPlan_contract()
    {
        var result = Types.InAssembly(CoreAssembly)
            .That().ResideInNamespaceStartingWith("Rig.Core.Reconciliation")
            .And().HaveNameEndingWith("Plan", StringComparison.Ordinal)
            .And().AreClasses()
            .Should().ImplementInterface(typeof(IPlan))
            .GetResult();

        Assert.True(result.IsSuccessful, Offenders(result));
    }

    [Fact]
    public void Reconciliation_changes_must_implement_IChange_contract()
    {
        var result = Types.InAssembly(CoreAssembly)
            .That().ResideInNamespaceStartingWith("Rig.Core.Reconciliation")
            .And().HaveNameEndingWith("Change", StringComparison.Ordinal)
            .And().AreClasses()
            .Should().ImplementInterface(typeof(IChange))
            .GetResult();

        Assert.True(result.IsSuccessful, Offenders(result));
    }

    [Fact]
    public void Neither_Core_nor_Cli_calls_raw_Console_Write_or_WriteLine()
    {
        var cliResult = Types.InAssembly(CliAssembly)
            .Should().MeetCustomRule(new NoRawConsoleWritesRule())
            .GetResult();

        var coreResult = Types.InAssembly(CoreAssembly)
            .Should().MeetCustomRule(new NoRawConsoleWritesRule())
            .GetResult();

        Assert.True(cliResult.IsSuccessful, $"Rig.Cli offenders: {Offenders(cliResult)}");
        Assert.True(coreResult.IsSuccessful, $"Rig.Core offenders: {Offenders(coreResult)}");
    }

    private sealed class HasCliCommandAttributeRule : ICustomRule
    {
        public bool MeetsRule(Mono.Cecil.TypeDefinition type)
        {
            return type.CustomAttributes.Any(a =>
                a.AttributeType.FullName == typeof(CliCommandAttribute).FullName);
        }
    }

    private sealed class ImplementsHandlerMethodRule : ICustomRule
    {
        public bool MeetsRule(Mono.Cecil.TypeDefinition type)
        {
            // A group only names its subcommands, so it needs no handler of its own.
            return IsGroup(type) || HasHandler(type);
        }

        private static bool IsGroup(Mono.Cecil.TypeDefinition type)
        {
            return type.NestedTypes.Any(nested => nested.CustomAttributes.Any(a =>
                a.AttributeType.FullName == typeof(CliCommandAttribute).FullName));
        }

        private static bool HasHandler(Mono.Cecil.TypeDefinition type)
        {
            // Must have a public RunAsync or Run method returning Task<int> or int
            return type.Methods.Any(m =>
                m.IsPublic &&
                m.Name is "RunAsync" or "Run" &&
                (m.ReturnType.FullName == "System.Threading.Tasks.Task`1<System.Int32>" ||
                 m.ReturnType.FullName == "System.Int32"));
        }
    }

    private sealed class NoRawConsoleWritesRule : ICustomRule
    {
        public bool MeetsRule(Mono.Cecil.TypeDefinition type)
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.Operand is Mono.Cecil.MethodReference mr &&
                        mr.DeclaringType.FullName == "System.Console" &&
                        mr.Name is "WriteLine" or "Write")
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    private static string Offenders(NetArchTest.Rules.TestResult result) =>
        string.Join(", ", result.FailingTypeNames ?? []);
}

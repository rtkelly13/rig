using Rig.Core.Estate;
using Rig.Core.Workspace;

namespace Rig.Core.Tests.Estate;

/// <summary><see cref="OwnerScopes"/> loaded from a tracked scopes file and a machine config, over temp files.</summary>
public sealed class OwnerScopesTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private OwnerScopes Load(string? machine)
    {
        var scopes = _dir.File("estate-scopes.json");
        File.WriteAllText(scopes, """{"personal": {"owners": ["rtkelly13"], "orgs": ["Resultful"]}}""");
        var config = _dir.File("config.json");
        if (machine is not null)
        {
            File.WriteAllText(config, machine);
        }

        return OwnerScopes.Load(scopes, config);
    }

    [Fact]
    public void With_no_role_set_the_machine_is_personal_and_writes_to_the_personal_owners()
    {
        var scopes = Load(machine: null);

        Assert.Equal(MachineRole.Personal, scopes.Role);
        Assert.False(scopes.RoleSet);
        Assert.Equal(["rtkelly13", "Resultful"], scopes.Writable);
    }

    [Fact]
    public void A_work_machine_writes_to_its_work_orgs_only()
    {
        var scopes = Load("""{"tools": {"machine": {"role": "work"}, "estate": {"work_orgs": ["acme"]}}}""");

        Assert.Equal(MachineRole.Work, scopes.Role);
        Assert.True(scopes.RoleSet);
        Assert.Equal(["acme"], scopes.Writable);
    }

    [Theory]
    [InlineData("RTKELLY13", OwnerClass.Personal)]
    [InlineData("resultful", OwnerClass.Personal)]
    [InlineData("ACME", OwnerClass.Work)]
    [InlineData("someone", OwnerClass.Other)]
    public void Owners_are_classified_without_case(string owner, OwnerClass expected) =>
        Assert.Equal(expected, Load("""{"tools": {"estate": {"work_orgs": ["acme", "rtkelly13"]}}}""").Classify(owner));
}

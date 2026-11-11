using Rig.Core.Desktop;
using Rig.Core.Workspace;

namespace Rig.Core.Tests.Desktop;

/// <summary><c>{name}</c> placeholders: where their values come from, how they resolve and what is left unresolved.</summary>
public sealed class PlaceholderTests
{
    private static readonly OrderedDictionary<string, string> None = new(StringComparer.Ordinal);

    [Fact]
    public void The_workspaces_are_placeholders_of_their_own() =>
        Assert.Equal(
            [new("workspace", "C:\\code"), new("personal-workspace", "C:\\personal")],
            new WorkspacePlan { Workspace = "C:\\code", PersonalWorkspace = "C:\\personal" }.Substitutions(None, None).ToList());

    [Fact]
    public void Aliases_resolve_placeholders_naming_other_ones()
    {
        var plan = new WorkspacePlan { Workspace = "C:\\code", Aliases = Map(("vscode_ws", "{workspace}\\project.code-workspace")) };

        Assert.Equal("C:\\code\\project.code-workspace", plan.Substitutions(None, None)["vscode_ws"]);
    }

    [Fact]
    public void A_plan_alias_beats_the_environment_and_the_machine_beats_both()
    {
        var plan = new WorkspacePlan { Aliases = Map(("appdata", "plan"), ("tools", "plan")) };

        var substitutions = plan.Substitutions(Map(("appdata", "env"), ("localappdata", "env")), Map(("tools", "machine")));

        Assert.Equal([new("appdata", "plan"), new("localappdata", "env"), new("tools", "machine")], substitutions.ToList());
    }

    [Fact]
    public void An_empty_plan_has_no_placeholders() => Assert.Empty(new WorkspacePlan().Substitutions(None, None));

    [Fact]
    public void A_cycle_or_self_reference_is_left_as_written()
    {
        var resolved = Placeholders.ResolveMap(Map(("a", "{b}"), ("b", "{a}"), ("self", "x{self}"), ("unknown", "{nope}")));

        Assert.Equal([new("b", "{b}"), new("a", "{b}"), new("self", "x{self}"), new("unknown", "{nope}")], resolved.ToList());
    }

    [Fact]
    public void Substitute_replaces_in_order_and_names_whatever_is_left()
    {
        Assert.Equal("C:\\code\\x", Placeholders.Substitute("{workspace}\\x", Map(("workspace", "C:\\code"))));
        Assert.Null(Placeholders.Substitute(null, None));
        Assert.Equal(
            "Unresolved placeholders: {a}, {b}",
            Assert.Throws<InvalidPlanException>(() => Placeholders.Substitute("{b}{a}{b}", None)).Message);
    }

    [Fact]
    public void Environment_aliases_skip_unset_and_empty_variables()
    {
        var variables = Map(("LOCALAPPDATA", "C:\\local"), ("APPDATA", string.Empty));

        Assert.Equal([new("localappdata", "C:\\local")], Placeholders.EnvironmentAliases(variables.GetValueOrDefault).ToList());
    }

    [Fact]
    public void Machine_desktop_aliases_are_trimmed_and_unreadable_ones_ignored()
    {
        using var temp = new TempDir();
        var path = temp.File("config.json");

        File.WriteAllText(path, """{"tools": {"desktop": {"aliases": {" tools ": "D:\\tools", "  ": "blank"}}}}""");
        Assert.Equal([new("tools", "D:\\tools")], MachineConfig.DesktopAliases(path).ToList());

        File.WriteAllText(path, """{"tools": {"desktop": {"aliases": {"tools": "D:\\tools", "n": 1}}}}""");
        Assert.Empty(MachineConfig.DesktopAliases(path));

        File.WriteAllText(path, "not json");
        Assert.Empty(MachineConfig.DesktopAliases(path));
        Assert.Empty(MachineConfig.DesktopAliases(Path.Join(temp.Path, "missing.json")));
    }

    private static OrderedDictionary<string, string> Map(params (string Name, string Value)[] values)
    {
        var map = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            map[name] = value;
        }

        return map;
    }
}

using System.Text.Json.Nodes;
using Rig.Core.Desktop;

namespace Rig.Core.Tests.Desktop;

/// <summary>How a bundle plan's refs override the desktop files they point at, and how a plan is saved as one.</summary>
public sealed class PlanBundleTests : IDisposable
{
    private readonly TempDir _temp = new();

    [Fact]
    public void A_ref_overrides_the_files_name_feature_and_disabled()
    {
        var text = Bundle(
            """{"bundle": {"desktops": [{"path": "desktops/a.json", "name": 7, "feature": 3, "disabled": "no"}]}}""",
            ("a.json", """{"name": "File", "feature": "file-feature", "disabled": false}"""));

        var desktop = PlanFiles.Parse(text, baseDirectory: _temp.Path).Desktops[0];

        Assert.Equal(("7", "3", true), (desktop.Name, desktop.Feature, desktop.Disabled));
    }

    [Fact]
    public void Without_overrides_the_file_keeps_its_own_values()
    {
        var text = Bundle("""{"bundle": {"desktops": [{"path": "desktops/a.json"}]}}""", ("a.json", """{"name": "File", "feature": "f", "disabled": true}"""));

        var desktop = PlanFiles.Parse(text, baseDirectory: _temp.Path).Desktops[0];

        Assert.Equal(("File", "f", true), (desktop.Name, desktop.Feature, desktop.Disabled));
    }

    [Fact]
    public void A_ref_name_stands_in_for_a_file_without_one()
    {
        var text = Bundle("""{"bundle": {"desktops": [{"path": "desktops/a.json", "name": "From Ref"}]}}""", ("a.json", """{"launch": []}"""));

        Assert.Equal("From Ref", PlanFiles.Parse(text, baseDirectory: _temp.Path).Desktops[0].Name);
    }

    [Fact]
    public void A_desktop_named_nowhere_is_an_error()
    {
        var text = Bundle("""{"bundle": {"desktops": [{"path": "desktops/a.json"}]}}""", ("a.json", "{}"));

        Assert.Contains("must define a name", Assert.Throws<InvalidPlanException>(() => PlanFiles.Parse(text, baseDirectory: _temp.Path)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundle_aliases_win_over_root_aliases_and_every_value_reads_as_text()
    {
        var text = Bundle(
            """{"aliases": {"a": "root", "b": null}, "bundle": {"aliases": {"a": "bundle", "c": 3, "d": true}, "desktops": [{"path": "desktops/a.json"}]}}""",
            ("a.json", """{"name": "A"}"""));

        Assert.Equal([new("a", "bundle"), new("b", "None"), new("c", "3"), new("d", "True")], PlanFiles.Parse(text, baseDirectory: _temp.Path).Aliases.ToList());
    }

    [Fact]
    public void Root_settings_are_kept()
    {
        var text = Bundle(
            """{"version": 1, "workspace": "C:/ws", "personal-workspace": "C:/personal", "switch_delay_ms": 10, "notes": ["n"], "features": {"lab": "off"}, "bundle": {"desktops": [{"path": "desktops/a.json"}]}}""",
            ("a.json", """{"name": "A"}"""));

        var plan = PlanFiles.Parse(text, baseDirectory: _temp.Path);

        Assert.Equal(("C:/ws", "C:/personal", 10), (plan.Workspace, plan.PersonalWorkspace, plan.SwitchDelayMs));
        Assert.Equal(["n"], plan.Notes);
        Assert.Equal([new("lab", false)], plan.Features.ToList());
    }

    [Fact]
    public void Root_desktops_are_used_when_the_bundle_lists_none() =>
        Assert.Equal(["Inline"], PlanFiles.Parse("""{"desktops": [{"name": "Inline"}], "bundle": {"desktops": []}}""").Desktops.Select(desktop => desktop.Name));

    [Fact]
    public void Root_desktops_are_ignored_when_the_bundle_lists_some()
    {
        var text = Bundle("""{"desktops": "not a list", "bundle": {"desktops": [{"path": "desktops/a.json"}]}}""", ("a.json", """{"name": "A"}"""));

        Assert.Equal(["A"], PlanFiles.Parse(text, baseDirectory: _temp.Path).Desktops.Select(desktop => desktop.Name));
    }

    [Theory]
    [InlineData("""{"bundle": "nope"}""", "JSON objects")]
    [InlineData("""{"bundle": {"aliases": []}}""", "JSON objects")]
    [InlineData("""{"bundle": {"desktops": "nope"}}""", "must be a list")]
    [InlineData("""{"bundle": {"desktops": ["nope"]}}""", "JSON objects")]
    [InlineData("""{"bundle": {"desktops": [{}]}}""", "relative path")]
    [InlineData("""{"bundle": {"desktops": [{"path": "  "}]}}""", "relative path")]
    [InlineData("""{"bundle": {"desktops": [{"path": 5}]}}""", "relative path")]
    public void Malformed_bundles_say_what_is_wrong(string root, string message) =>
        Assert.Contains(message, Assert.Throws<InvalidPlanException>(() => PlanFiles.Parse(root, baseDirectory: _temp.Path)).Message, StringComparison.Ordinal);

    [Fact]
    public void A_bundle_read_from_memory_has_nowhere_to_find_its_desktops() =>
        Assert.Contains("base directory", Assert.Throws<InvalidPlanException>(() => PlanFiles.Parse("""{"bundle": {"desktops": [{"path": "a.json"}]}}""")).Message, StringComparison.Ordinal);

    [Fact]
    public void A_desktop_file_that_is_not_an_object_is_an_error()
    {
        var text = Bundle("""{"bundle": {"desktops": [{"path": "desktops/a.json"}]}}""", ("a.json", "[1]"));

        Assert.Contains("JSON objects", Assert.Throws<InvalidPlanException>(() => PlanFiles.Parse(text, baseDirectory: _temp.Path)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_saved_bundle_names_each_desktop_file_after_its_desktop_and_keeps_names_apart()
    {
        var plan = new WorkspacePlan
        {
            Desktops = [new DesktopPlan("Work & Play"), new DesktopPlan("work-play"), new DesktopPlan("!!!") { Feature = "lab", Disabled = true }],
        };
        var root = Path.Join(_temp.Path, "plan.json");

        PlanFiles.SaveBundle(plan, root);

        var refs = JsonNode.Parse(File.ReadAllText(root))!["bundle"]!["desktops"]!.AsArray().Select(item => (string)item!["path"]!).ToList();
        Assert.Equal(["desktops/work-play.json", "desktops/work-play-2.json", "desktops/desktop-3.json"], refs);
        var loaded = PlanFiles.Load(root);
        Assert.Equal(["Work & Play", "work-play", "!!!"], loaded.Desktops.Select(desktop => desktop.Name));
        Assert.Equal(("lab", true), (loaded.Desktops[2].Feature, loaded.Desktops[2].Disabled));
    }

    [Fact]
    public void A_saved_plan_is_written_without_a_byte_order_mark_and_ends_with_a_newline()
    {
        var path = Path.Join(_temp.Path, "nested", "plan.json");

        PlanFiles.Save(new WorkspacePlan { Workspace = "C:\\code", Notes = ["caf\u00e9"] }, path);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal((byte)'{', bytes[0]);
        Assert.EndsWith("\n", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("caf\\u00e9", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal("C:\\code", PlanFiles.Load(path).Workspace);
    }

    [Theory]
    [InlineData("Work & Play", "work-play")]
    [InlineData("  --Lab 2--  ", "lab-2")]
    [InlineData("!!!", "")]
    public void Slugify_keeps_letters_and_digits_joined_by_single_hyphens(string name, string expected) =>
        Assert.Equal(expected, PlanFiles.Slugify(name));

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    private string Bundle(string root, params (string Name, string Content)[] desktops)
    {
        foreach (var (name, content) in desktops)
        {
            File.WriteAllText(_temp.File(Path.Join("desktops", name)), content);
        }

        return root;
    }
}

using Rig.Core.Desktop;

namespace Rig.Core.Tests.Desktop;

/// <summary>Reading a plan the way the desktop commands do: paths, flags, columns and verification.</summary>
public sealed class PlanLoadingTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private static Dictionary<string, string?> Environment(params (string Name, string? Value)[] values) =>
        values.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);

    [Fact]
    public void The_default_plan_is_under_appdata_on_windows_and_the_config_folder_elsewhere()
    {
        var windows = Environment(("APPDATA", "/appdata"));
        var none = Environment();

        Assert.Equal(
            Path.Join("/appdata", "workspace-manager", "windows-workspace.json"),
            PlanLoading.DefaultPath(name => windows.GetValueOrDefault(name), "/home", windows: true));
        Assert.Equal(
            Path.Join("/home", ".config", "workspace-manager", "windows-workspace.json"),
            PlanLoading.DefaultPath(name => none.GetValueOrDefault(name), "/home", windows: true));
        Assert.Equal(
            Path.Join("/home", ".config", "workspace-manager", "windows-workspace.json"),
            PlanLoading.DefaultPath(name => windows.GetValueOrDefault(name), "/home", windows: false));
    }

    [Fact]
    public void Logs_sit_beside_the_plan_unless_the_override_says_otherwise()
    {
        Assert.Equal(Path.Join("/plans", "logs"), PlanLoading.LogsDirectory("/plans/workspace.json", _ => null));
        Assert.Equal("/elsewhere", PlanLoading.LogsDirectory("/plans/workspace.json", name => name == PlanLoading.LogDirectoryVariable ? "/elsewhere" : null));
    }

    [Fact]
    public void A_plan_loads_with_its_flags_and_feature_overrides()
    {
        var path = Path.Join(_temp.Path, "plan.json");
        PlanFiles.Save(
            new WorkspacePlan
            {
                Desktops = [new("Work") { Launch = [new("Notes") { Path = "/notes", Feature = "notes" }] }],
                Features = new OrderedDictionary<string, bool>(StringComparer.Ordinal) { ["notes"] = true },
            },
            path);

        var plan = PlanLoading.Load(
            path,
            () => [new PlanFlag("notes", true), new PlanFlag("other", false)],
            enable: [],
            disable: ["notes"],
            screenWidths: new Dictionary<int, int>(),
            paths: null,
            verify: false);

        Assert.False(plan.Features["notes"]);
        Assert.False(plan.Features["other"]);
        Assert.True(plan.Desktops[0].Launch[0].Disabled);
    }

    [Fact]
    public void A_launch_entry_can_be_read_from_a_standalone_file()
    {
        var entry = PlanFiles.ParseLaunchEntry("""{"name": "Notes", "type": "terminal", "cwd": "/code", "command": "opencode"}""");

        Assert.Equal("Notes", entry.Name);
        Assert.Equal(LaunchEntry.Terminal, entry.Type);
        Assert.Equal(LaunchEntry.DefaultTerminalShell, entry.Shell);
    }

    [Fact]
    public void A_machine_plan_loads_from_the_repository_template()
    {
        var path = Path.Join(_temp.Path, "plan.bundle.json");
        PlanFiles.SaveBundle(
            new WorkspacePlan { Desktops = [new("Work") { Launch = [new("Notes") { Path = "/notes", Placement = new(1, new("full")) }] }] },
            path);

        var plan = PlanLoading.BuildMachinePlan(path, "/code/workspace", "/code/personal");

        Assert.Equal("/code/workspace", plan.Workspace);
        Assert.Equal("/code/personal", plan.PersonalWorkspace);
        Assert.Equal("Work", Assert.Single(plan.Desktops).Name);
    }

    [Fact]
    public void A_plan_with_no_desktops_is_refused()
    {
        var error = Assert.Throws<InvalidPlanException>(() =>
            PlanLoading.Verify(new WorkspacePlan(), "plan.json", new Dictionary<string, string>()));

        Assert.Equal("Invalid workspace plan plan.json: Workspace plan must define at least one desktop", error.Message);
    }

    [Fact]
    public void A_plan_with_a_duplicate_entry_is_refused()
    {
        var plan = new WorkspacePlan
        {
            Desktops =
            [
                new("Work") { Launch = [new("Notes") { Path = "/notes" }, new("Notes") { Path = "/notes" }] },
            ],
        };

        var error = Assert.Throws<InvalidPlanException>(() => PlanLoading.Verify(plan, "plan.json", new Dictionary<string, string>()));

        Assert.Contains("Work: duplicate launch entry `Notes`", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_valid_plan_passes_through_verification()
    {
        var plan = new WorkspacePlan
        {
            Desktops = [new("Work") { Launch = [new("Notes") { Path = "/notes", Placement = new(1, new("full")) }] }],
        };

        Assert.Same(plan, PlanLoading.Verify(plan, "plan.json", new Dictionary<string, string>()));
    }
}

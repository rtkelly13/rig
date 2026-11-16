using System.CommandLine;
using Rig.Cli.Desktop;
using Rig.Core.Desktop;
using Rig.Core.Tests;

namespace Rig.Cli.Tests.Desktop;

/// <summary><c>rig desktop workspace</c>: the command tree and the commands that run without a Windows desktop.</summary>
public sealed class DesktopCommandTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly StringWriter _out = new() { NewLine = "\n" };
    private readonly StringWriter _error = new() { NewLine = "\n" };

    public void Dispose()
    {
        _temp.Dispose();
        _out.Dispose();
        _error.Dispose();
    }

    private DesktopHost Host(string? home = null) => new(
        _ => null,
        home ?? _temp.Path,
        _temp.Path,
        Path.Join(_temp.Path, "appdata"),
        TimeProvider.System,
        _out,
        _error,
        () => [],
        () => null);

    private Task<int> Run(params string[] args) =>
        new DesktopWorkspaceCommand(Host()).Build().Parse(args)
            .InvokeAsync(new InvocationConfiguration { Output = _out, Error = _error }, TestContext.Current.CancellationToken);

    private static Command Sub(Command command, string name) =>
        command.Subcommands.Single(sub => sub.Name == name);

    [Fact]
    public void The_desktop_group_carries_the_workspace_group()
    {
        var desktop = new DesktopCommand(Host()).Build();

        Assert.Equal("desktop", desktop.Name);
        var workspace = Sub(desktop, "workspace");
        Assert.Equal(
            [
                "restore",
                "audit",
                "status",
                "validate-plan",
                "features",
                "export-plan",
                "wire-machine",
                "align",
                "name-desktops",
                "guard",
                "detect-app",
                "launch-file",
                "test-item",
                "restart-app",
                "update-orca",
            ],
            workspace.Subcommands.Select(sub => sub.Name));
    }

    [Fact]
    public async Task A_native_command_says_it_needs_windows()
    {
        Assert.Equal(1, await Run("restore"));
        Assert.Equal(1, await Run("status"));
        Assert.Equal(1, await Run("audit"));
        Assert.Equal(1, await Run("align"));
        Assert.Equal(1, await Run("detect-app"));
        Assert.Equal(1, await Run("name-desktops"));
        Assert.Equal(1, await Run("guard"));
        Assert.Equal(1, await Run("wire-machine"));
        Assert.Equal(1, await Run("export-plan"));
        Assert.Equal(1, await Run("test-item", "--name", "x"));
        Assert.Equal(1, await Run("restart-app", "--name", "x"));
        Assert.Equal(1, await Run("update-orca"));
        Assert.All(
            _out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => Assert.Contains("requires Windows.", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_valid_plan_passes_validation()
    {
        var path = Path.Join(_temp.Path, "plan.json");
        var notes = Path.Join(_temp.Path, "notes.exe");
        await File.WriteAllTextAsync(notes, "fixture", TestContext.Current.CancellationToken);
        PlanFiles.Save(
            new WorkspacePlan
            {
                Desktops = [new("Work") { Launch = [new("Notes") { Path = notes, Placement = new(1, new("full")) }] }],
            },
            path);

        var exit = await Run("validate-plan", "--config", path);

        Assert.Equal(0, exit);
        Assert.Equal("Plan is valid.\n", _out.ToString());
    }

    [Fact]
    public async Task An_invalid_plan_is_reported_and_fails()
    {
        var path = Path.Join(_temp.Path, "plan.json");
        PlanFiles.Save(new WorkspacePlan { Desktops = [new("Work")] }, path);

        var exit = await Run("validate-plan", "--config", path);

        Assert.Equal(1, exit);
        Assert.StartsWith("Structure: Work: virtual desktop", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_config_is_reported()
    {
        Assert.Equal(1, await Run("validate-plan", "--config", Path.Join(_temp.Path, "missing.json")));
        Assert.Equal($"Config not found: {Path.Join(_temp.Path, "missing.json")}\n", _out.ToString());
    }

    [Fact]
    public async Task Features_lists_what_each_toggle_owns()
    {
        var path = Path.Join(_temp.Path, "plan.json");
        PlanFiles.Save(
            new WorkspacePlan
            {
                Desktops = [new("Work") { Launch = [new("Notes") { Path = "/notes", Feature = "notes" }] }],
                Features = new OrderedDictionary<string, bool>(StringComparer.Ordinal) { ["notes"] = true },
            },
            path);

        var exit = await Run("features", "--config", path);

        Assert.Equal(0, exit);
        Assert.Contains("Workspace Features", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("notes: on (plan)", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("Work: Notes", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Features_surfaces_a_reference_to_an_undeclared_toggle()
    {
        var path = Path.Join(_temp.Path, "plan.json");
        PlanFiles.Save(
            new WorkspacePlan
            {
                Desktops = [new("Work") { Launch = [new("Notes") { Path = "/notes", Feature = "notes" }] }],
            },
            path);

        var exit = await Run("features", "--config", path);

        Assert.Equal(1, exit);
        Assert.Contains("Referencing undeclared features", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("Work/Notes", _out.ToString(), StringComparison.Ordinal);
    }
}

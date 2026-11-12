using Rig.Core.Jobs;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;
using Rig.Core.Workspace;

namespace Rig.Core.Tests.Jobs;

/// <summary>The pieces of <c>Rig.Core.Jobs</c> the oracle does not reach: times, paths, the scheduler and machine config.</summary>
public sealed class JobsTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("2026-11-12", "2026-11-12T00:00:00.0000000")]
    [InlineData("2026-11-12T09", "2026-11-12T09:00:00.0000000")]
    [InlineData("2026-11-12 09:30Z", "2026-11-12T09:30:00.0000000+00:00")]
    [InlineData("2026-11-12T093015+0130", "2026-11-12T09:30:15.0000000+01:30")]
    [InlineData("2026-11-12T09:30:15.1234567-05:00", "2026-11-12T09:30:15.1234560-05:00")]
    [InlineData("2026-11-12T09:30:15,5+00", "2026-11-12T09:30:15.5000000+00:00")]
    public void An_iso_time_reads_as_python_s_fromisoformat_reads_it(string text, string expected)
    {
        var parsed = IsoStamp.Parse(text);

        Assert.NotNull(parsed);
        Assert.Equal(expected, expected.Length > 27 ? parsed.Value.ToString("o") : parsed.Value.DateTime.ToString("o"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a time")]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30T09:00")]
    [InlineData("2026-11-12T25:00")]
    [InlineData("2026-11-12Z")]
    [InlineData("20261112")]
    public void Anything_else_is_not_a_time(string text)
    {
        Assert.Null(IsoStamp.Parse(text));
    }

    [Fact]
    public void An_outcome_built_with_a_bad_start_says_so_when_asked()
    {
        Assert.Throws<InvalidOperationException>(() => new Outcome("yesterday", 1, 0, "tick").StartedAt);
    }

    [Fact]
    public void Boot_time_is_uptime_back_on_windows_and_the_epoch_elsewhere()
    {
        var now = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(now.AddHours(-2), BootTime.At(now, TimeSpan.FromHours(2), windows: true));
        Assert.Equal(DateTimeOffset.UnixEpoch, BootTime.At(now, TimeSpan.FromHours(2), windows: false));
    }

    [Fact]
    public async Task A_task_is_registered_when_schtasks_query_finds_it()
    {
        var proc = new FakeProc()
            .Answer(new Ran(0, string.Empty, string.Empty), "schtasks", "/Query", "/TN", CoordinatorTasks.Logon)
            .Answer(new Ran(1, string.Empty, "ERROR: not found"), "schtasks", "/Query", "/TN", CoordinatorTasks.Tick);
        var tasks = new Schtasks(proc);

        Assert.True(await tasks.ExistsAsync(CoordinatorTasks.Logon, TestContext.Current.CancellationToken));
        Assert.False(await tasks.ExistsAsync(CoordinatorTasks.Tick, TestContext.Current.CancellationToken));
        Assert.Equal(["schtasks", "/Query", "/TN", CoordinatorTasks.Logon], proc.Requests[0].Argv);
    }

    [Fact]
    public void The_plan_lives_under_appdata_on_windows_and_the_config_folder_elsewhere()
    {
        Assert.Equal(Path.Join("/appdata", "workspace-manager", "windows-workspace.json"), JobRegistry.DefaultPlanPath("/appdata", "/home", windows: true));
        Assert.Equal(Path.Join("/home", ".config", "workspace-manager", "windows-workspace.json"), JobRegistry.DefaultPlanPath(string.Empty, "/home", windows: true));
        Assert.Equal(Path.Join("/home", ".config", "workspace-manager", "windows-workspace.json"), JobRegistry.DefaultPlanPath("/appdata", "/home", windows: false));
        Assert.Equal(Path.Join("/home", "AppData", "Local", "shared-utilities", "chrome-tabs", "organise.log"), JobRegistry.ChromeTabsLog(null, "/home"));
    }

    [Fact]
    public void On_windows_scripts_carry_exe_and_estate_roots_compare_without_case()
    {
        var env = new JobsEnvironment("/rig/Scripts", "plan", "/Code/work/workspace", "/code/personal", ["/CODE/work", "/elsewhere/work"], "feed", "orca", "log", Windows: true);

        Assert.Equal(Path.Join("/rig/Scripts", "rig.exe"), env.Script("rig"));
        Assert.Equal([env.CodeRoot, "/elsewhere/work"], env.EstateRoots);
    }

    [Fact]
    public void Off_windows_case_tells_estate_roots_apart()
    {
        var env = new JobsEnvironment("/rig/bin", "plan", "/Code/work/ws", "/code/personal", ["work"], "feed", "orca", "log", Windows: false);

        Assert.Equal([env.CodeRoot, "work", "/Code/work/ws"], env.EstateRoots);
        Assert.Equal(Path.Join("/rig/bin", "rig"), env.Script("rig"));
    }

    [Fact]
    public void The_workspace_paths_need_a_workspace_repo()
    {
        var env = new JobsEnvironment("/rig/bin", "plan", null, "/code/personal", [], "feed", "orca", "log", Windows: false);

        Assert.Throws<InvalidOperationException>(() => env.WorkspacePython);
    }

    [Theory]
    [InlineData("""{"tools": {"estate": {"workspace_repo": "work/workspace"}}}""", "work/workspace")]
    [InlineData("""{"tools": {"estate": {"workspace_repo": "  /abs/ws  "}}}""", "/abs/ws")]
    [InlineData("""{"tools": {"estate": {"workspace_repo": "~/ws"}}}""", "~/ws")]
    [InlineData("""{"tools": {"estate": {"workspace_repo": "   "}}}""", null)]
    [InlineData("""{"tools": {"estate": {"workspace_repo": 3}}}""", null)]
    [InlineData("""{"tools": {"estate": {}}}""", null)]
    [InlineData("""[]""", null)]
    [InlineData("""not json""", null)]
    public void The_workspace_repo_resolves_as_a_work_folder_does(string config, string? expected)
    {
        var path = Path.Join(_temp.Path, "config.json");
        File.WriteAllText(path, config);

        Assert.Equal(
            expected switch
            {
                null => null,
                "~/ws" => Path.Join("/home", "ws"),
                _ => Path.Combine("/code", expected),
            },
            MachineConfig.WorkspaceRepo(path, "/code", "/home"));
    }
}

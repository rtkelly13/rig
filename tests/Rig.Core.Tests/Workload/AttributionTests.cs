using Rig.Core.Tests.Fakes;
using Rig.Core.Workload;

namespace Rig.Core.Tests.Workload;

/// <summary>How a snapshot becomes a sample: ownership by process tree, sessions, large processes and bursts.</summary>
public sealed class AttributionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, 500, TimeSpan.Zero);
    private static readonly DateTime Boot = new(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc);

    private static readonly AppRules Rules = AppRules.Parse("""
        {
          "apps": [
            { "app": "Claude Code", "names": ["claude"], "sessions": true },
            { "app": "Chrome", "names": ["chrome"], "paths": ["/Google Chrome.app/"] },
            { "app": "Terminal", "names": ["WindowsTerminal"] }
          ],
          "burst_ignore": ["chrome", "conhost", "Helper*"]
        }
        """);

    private static WorkloadSample Build(FakeProcessTable table, SampleLimits? limits = null)
    {
        var snapshot = new ProcessSnapshot([.. table.Processes], table.Memory);
        return Attribution.Build(snapshot, Rules, table.Commands, limits ?? SampleLimits.Default, Now, "box", "windows");
    }

    [Fact]
    public void A_process_belongs_to_its_nearest_matching_ancestor()
    {
        var table = new FakeProcessTable()
            .Add(1, 0, "explorer.exe", 100)
            .Add(2, 1, "WindowsTerminal.exe", 50)
            .Add(3, 2, "pwsh.exe", 60)
            .Add(4, 3, "claude.exe", 800)
            .Add(5, 4, "bash.exe", 10)
            .Add(6, 5, "dotnet.exe", 2000)
            .Add(7, 0, "chrome.exe", 300)
            .Add(8, 7, "chrome.exe", 200)
            .Add(9, 0, "helper", 1, path: "/Applications/Google Chrome.app/Contents/helper");

        var sample = Build(table);

        Assert.Equal(1, sample.V);
        Assert.Equal(new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc), sample.Ts);
        Assert.Equal(("box", "windows"), (sample.Host, sample.Os));
        Assert.Equal(new RamUse(65536, 32768), sample.Ram);
        Assert.Equal(new ProcessTotals(9, 3521, 0), sample.Processes);
        Assert.Equal(
            [
                new AppUse("Claude Code", 3, 2810, 0),
                new AppUse("Chrome", 3, 501, 0),
                new AppUse("Terminal", 2, 110, 0),
                new AppUse(Attribution.Other, 1, 100, 0),
            ],
            sample.Apps);
    }

    [Fact]
    public void Apps_past_the_top_join_other()
    {
        var table = new FakeProcessTable()
            .Add(1, 0, "claude", 900)
            .Add(2, 0, "chrome", 500)
            .Add(3, 0, "WindowsTerminal", 100)
            .Add(4, 0, "loose", 50);

        var sample = Build(table, SampleLimits.Default with { TopApps = 1 });

        Assert.Equal([new AppUse("Claude Code", 1, 900, 0), new AppUse(Attribution.Other, 3, 650, 0)], sample.Apps);
    }

    [Fact]
    public void A_parent_that_started_after_its_child_is_a_reused_pid_not_the_parent()
    {
        var table = new FakeProcessTable()
            .Add(10, 0, "claude.exe", 700, started: Boot.AddHours(2))
            .Add(11, 10, "orphan.exe", 40, started: Boot.AddHours(1))
            .Add(12, 12, "self.exe", 1, started: Boot);

        var sample = Build(table);

        Assert.Equal([new AppUse("Claude Code", 1, 700, 0), new AppUse(Attribution.Other, 2, 41, 0)], sample.Apps);
        Assert.Empty(sample.Claude.Children);
    }

    [Fact]
    public void Sessions_are_the_outermost_session_processes_with_their_trees()
    {
        var table = new FakeProcessTable()
            .Add(1, 0, "WindowsTerminal", 50)
            .Add(2, 1, "claude.exe", 600)
            .Add(3, 2, "bash.exe", 10)
            .Add(4, 3, "claude.exe", 300)
            .Add(5, 4, "node.exe", 90)
            .Add(6, 1, "claude.exe", 1000)
            .Add(7, 6, "bash.exe", 5)
            .Add(8, 0, "claude.exe", 800);

        var claude = Build(table).Claude;

        Assert.Equal(3, claude.Sessions);
        Assert.Equal(new Spread(600, 800, 1000), claude.SessionMb);
        Assert.Equal(new Spread(800, 1000, 1005), claude.TreeMb);
        Assert.Equal(new Dictionary<string, int> { ["bash.exe"] = 2, ["claude.exe"] = 1, ["node.exe"] = 1 }, claude.Children);
    }

    [Fact]
    public void No_sessions_leaves_the_spreads_out()
    {
        var claude = Build(new FakeProcessTable().Add(1, 0, "chrome", 10)).Claude;

        Assert.Equal(0, claude.Sessions);
        Assert.Null(claude.SessionMb);
        Assert.Null(claude.TreeMb);
        Assert.Empty(claude.Children);
        Assert.Null(Attribution.SpreadOf([]));
        Assert.Equal(new Spread(1, 2, 4), Attribution.SpreadOf([4, 1, 2, 3]));
    }

    [Fact]
    public void Large_processes_carry_a_redacted_command_and_only_theirs_are_fetched()
    {
        var table = new FakeProcessTable()
            .Add(1, 0, "claude.exe", 700)
            .Add(2, 1, "VBCSCompiler.exe", 3500, 3000, started: Boot.AddTicks(1234567))
            .Add(3, 0, "Memory Compression", 8, 1100)
            .Add(4, 0, "small.exe", 1023, 1000);
        table.Commands[2] = "VBCSCompiler.exe --token=abcdef123 -pipename:x";
        table.Commands[4] = "never asked";

        var snapshot = new ProcessSnapshot([.. table.Processes], table.Memory);
        var pids = Attribution.LargePids(snapshot, SampleLimits.Default);
        var sample = Build(table);

        Assert.Equal([2, 3], pids);
        Assert.Equal(
            [
                new LargeProcess(2, "VBCSCompiler.exe", "Claude Code", 3500, 3000, Boot, "VBCSCompiler.exe --token=*** -pipename:x"),
                new LargeProcess(3, "Memory Compression", Attribution.Other, 8, 1100, null, string.Empty),
            ],
            sample.Large);
    }

    [Fact]
    public void Bursts_are_names_with_many_copies_not_on_the_ignore_list()
    {
        var table = new FakeProcessTable().Add(1, 0, "claude.exe", 700).Add(2, 0, "pytest-host", 1);
        for (var pid = 100; pid < 110; pid++)
        {
            table.Add(pid, 1, "python.exe", 100);
            table.Add(pid + 100, 0, "chrome.exe", 10);
            table.Add(pid + 200, 0, "Helper (Renderer)", 10);
        }

        for (var pid = 400; pid < 408; pid++)
        {
            table.Add(pid, 0, "conhost.exe", 1);
            table.Add(pid + 100, 0, "eight.exe", 1);
        }

        table.Add(600, 2, "PYTHON.EXE", 5);

        var bursts = Build(table).Bursts;

        Assert.Equal([new Burst("python.exe", 11, "Claude Code", 1005)], bursts);
    }

    [Fact]
    public void A_cycle_in_the_parent_links_ends()
    {
        var table = new FakeProcessTable().Add(1, 2, "a", 1).Add(2, 1, "b", 1);

        Assert.Equal([new AppUse(Attribution.Other, 2, 2, 0)], Build(table).Apps);
    }
}

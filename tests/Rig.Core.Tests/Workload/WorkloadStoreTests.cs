using System.Text;
using Rig.Core.Processes;
using Rig.Core.Services;
using Rig.Core.Tests.Fakes;
using Rig.Core.Workload;

namespace Rig.Core.Tests.Workload;

/// <summary>Day files: writing, pruning and reading back; the summary; the sampler end to end; and the LaunchAgent.</summary>
public sealed class WorkloadStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => temp.Dispose();

    [Fact]
    public void Paths_sit_under_the_base_folder()
    {
        var paths = new WorkloadPaths(temp.Path);

        Assert.Equal(Path.Join(temp.Path, "workload"), paths.Folder);
        Assert.Equal(Path.Join(temp.Path, "logs", "workload.log"), paths.Log);
        Assert.Equal(Path.Join(temp.Path, "workload", "2026-11-11.jsonl"), paths.DayFile(new DateTimeOffset(2026, 11, 12, 0, 30, 0, TimeSpan.FromHours(1))));
        Assert.Empty(paths.DayFiles());
        Assert.Empty(paths.Read(Now.AddDays(-1)));
    }

    [Fact]
    public void A_sample_is_one_snake_case_line_and_reads_back()
    {
        var paths = new WorkloadPaths(temp.Path);
        var sample = Sample(Now, freeMb: 20000);

        var (file, bytes) = paths.Append(sample);
        paths.Append(Sample(Now.AddMinutes(15), freeMb: 19000));
        var line = File.ReadAllLines(file)[0];

        Assert.Equal(paths.DayFile(Now), file);
        Assert.Equal(Encoding.UTF8.GetByteCount(line) + 1, bytes);
        Assert.StartsWith("""{"v":1,"ts":"2026-11-11T12:00:00Z","host":"box","os":"windows","ram":{"total_mb":65536,"free_mb":20000}""", line, StringComparison.Ordinal);
        Assert.Contains("\"session_mb\":{\"min\":600,\"median\":800,\"max\":1000}", line, StringComparison.Ordinal);
        Assert.DoesNotContain("tree_mb", line, StringComparison.Ordinal);
        var back = Assert.Single(paths.Read(Now.AddMinutes(1)));
        Assert.Equal(19000, back.Ram.FreeMb);
        Assert.Equal(2, paths.Read(Now.AddDays(-3)).Count);
    }

    [Fact]
    public void Lines_that_are_not_samples_are_skipped()
    {
        Assert.Null(WorkloadPaths.Parse(" "));
        Assert.Null(WorkloadPaths.Parse("{not json"));
        Assert.Null(WorkloadPaths.Parse("{\"v\":1}"));
        Assert.Null(WorkloadPaths.Parse("[]"));
        Assert.NotNull(WorkloadPaths.Parse(WorkloadPaths.Line(Sample(Now, 1))));
    }

    [Fact]
    public void Pruning_takes_old_day_files_but_never_today_or_other_files()
    {
        var paths = new WorkloadPaths(temp.Path);
        var old = Touch(paths, "2026-11-01.jsonl", days: 10);
        Touch(paths, "2026-11-08.jsonl", days: 3);
        var today = Touch(paths, "2026-11-11.jsonl", days: 30);
        Touch(paths, "notes.jsonl", days: 30);

        Assert.Equal([old], paths.Prune(Now, apply: false));
        Assert.True(File.Exists(old));
        Assert.Equal([old], paths.Prune(Now, apply: true));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(today));
        Assert.Equal(2, paths.DayFiles().Count);
    }

    [Fact]
    public async Task The_sampler_asks_only_for_large_command_lines_appends_and_prunes()
    {
        var paths = new WorkloadPaths(temp.Path);
        var old = Touch(paths, "2026-11-01.jsonl", days: 10);
        var table = new FakeProcessTable().Add(1, 0, "claude.exe", 700).Add(2, 1, "dotnet.exe", 2000);
        table.Commands[2] = "dotnet build";

        var result = await new WorkloadSampler(table, AppRules.Bundled, paths, SampleLimits.Default).RunAsync(Now, "box", "windows", Ct);

        Assert.Equal([2], Assert.Single(table.Asked));
        Assert.Equal("dotnet build", Assert.Single(result.Sample.Large).Command);
        Assert.Equal([old], result.Pruned);
        Assert.Equal(paths.DayFile(Now), result.File);
        Assert.True(result.Bytes < 4096);
    }

    [Fact]
    public void The_summary_walks_the_day()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test+1", "test+1");
        var leak = new LargeProcess(5, "QtWebEngineProcess.exe", "Stream Deck", 1000, 900, null, string.Empty);
        var first = Sample(Now, 25000) with { Large = [leak], Bursts = [new Burst("python.exe", 10, "Claude Code", 900)] };
        var low = Sample(Now.AddHours(1), 19700, privateMb: 44300) with { Large = [leak with { PrivateMb = 1200 }] };
        var last = Sample(Now.AddHours(2), 28700) with
        {
            Large = [leak with { PrivateMb = 1800 }, new LargeProcess(6, "VBCSCompiler.exe", "Claude Code", 3500, 3000, null, string.Empty)],
            Bursts = [new Burst("python.exe", 14, "Claude Code", 2000), new Burst("dotnet.exe", 19, "Claude Code", 4000)],
        };

        var text = string.Join("\n", WorkloadSummary.Lines([first, low, last], zone));

        Assert.Equal(
            """
            3 samples from 2026-11-11 13:00 to 2026-11-11 15:00 on box
            Free RAM: 19.2–28.0 GB of 64.0 GB; lowest at 2026-11-11 14:00
            Peak private total: 43.3 GB at 2026-11-11 14:00 (300 processes)
            Apps at the peak (private, working set, processes):
              Claude Code           7.8 GB    0.0 GB    20
              other                 1.0 GB    1.0 GB   280
            Claude Code sessions: 3–3 (most at 2026-11-11 13:00); each 600–1,000 MB, median 800 MB; largest session tree 1.5 GB
            Large processes (first → last private MB, peak, growth):
              VBCSCompiler.exe (6, Claude Code) 3,500 → 3,500, peak 3,500, 2026-11-11 15:00 to 2026-11-11 15:00
              QtWebEngineProcess.exe (5, Stream Deck) 1,000 → 1,800, peak 1,800, 2026-11-11 13:00 to 2026-11-11 15:00, +400 MB/h
            Bursts (most copies, owner, peak private MB, samples seen in):
              dotnet.exe ×19 (Claude Code) 4,000 MB in 1
              python.exe ×14 (Claude Code) 2,000 MB in 2
            """.ReplaceLineEndings("\n"),
            text);
    }

    [Fact]
    public void The_summary_of_nothing_says_so()
    {
        Assert.Equal(["No samples in that window."], WorkloadSummary.Lines([], TimeZoneInfo.Utc));
        var quiet = Sample(Now, 1000) with { Claude = new SessionUse(0, null, null, new Dictionary<string, int>()) };

        var text = string.Join("\n", WorkloadSummary.Lines([quiet], TimeZoneInfo.Utc));

        Assert.Contains("Claude Code sessions: none seen.", text, StringComparison.Ordinal);
        Assert.Contains("Large processes: none.", text, StringComparison.Ordinal);
        Assert.Contains("Bursts: none.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_agent_runs_sample_every_fifteen_minutes_in_the_background()
    {
        var path = WorkloadService.PathFor(["/Users/me/.local/bin/rig", "/usr/local/share/dotnet/dotnet", "/Users/me/.local/bin/uv", "bare"]);
        var text = Encoding.UTF8.GetString(WorkloadService.Render("/Users/me/.local/bin/rig", path, "/logs/workload.log"));

        Assert.Equal("/Users/me/.local/bin:/usr/local/share/dotnet:/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin", path);
        Assert.Equal(
            "<dict>\n\t<key>EnvironmentVariables</key>\n\t<dict>\n\t\t<key>PATH</key>\n\t\t<string>" + path + "</string>\n\t</dict>\n"
            + "\t<key>Label</key>\n\t<string>dev.rtkelly.rig.workload</string>\n"
            + "\t<key>LowPriorityIO</key>\n\t<true/>\n"
            + "\t<key>Nice</key>\n\t<integer>10</integer>\n"
            + "\t<key>ProcessType</key>\n\t<string>Background</string>\n"
            + "\t<key>ProgramArguments</key>\n\t<array>\n\t\t<string>/Users/me/.local/bin/rig</string>\n\t\t<string>workload</string>\n\t\t<string>sample</string>\n\t</array>\n"
            + "\t<key>RunAtLoad</key>\n\t<true/>\n"
            + "\t<key>StandardErrorPath</key>\n\t<string>/logs/workload.log</string>\n"
            + "\t<key>StandardOutPath</key>\n\t<string>/logs/workload.log</string>\n"
            + "\t<key>StartInterval</key>\n\t<integer>900</integer>\n"
            + "</dict>\n</plist>\n",
            text[text.IndexOf("<dict>", StringComparison.Ordinal)..]);
        Assert.DoesNotContain("KeepAlive", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_agent_installs_loads_and_uninstalls_through_launchd()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl");
        var launchd = new Launchd(proc, temp.Folder("agents"), "gui/501");
        var paths = new WorkloadPaths(temp.Folder("base"));

        Assert.False(WorkloadService.Installed(launchd));
        var plist = await WorkloadService.InstallAsync(launchd, paths, "/bin/rig", "/bin", Ct);

        Assert.Equal(Path.Join(temp.Path, "agents", WorkloadService.PlistName), plist);
        Assert.True(WorkloadService.Installed(launchd));
        Assert.True(Directory.Exists(Path.GetDirectoryName(paths.Log)));
        Assert.True(await WorkloadService.LoadedAsync(launchd, Ct));
        Assert.True(await WorkloadService.UninstallAsync(launchd, Ct));
        Assert.False(WorkloadService.Installed(launchd));
        await Assert.ThrowsAsync<ArgumentNullException>(() => WorkloadService.UninstallAsync(null!, Ct));
    }

    private static WorkloadSample Sample(DateTimeOffset at, long freeMb, long privateMb = 30000) =>
        new(
            1,
            at.UtcDateTime,
            "box",
            "windows",
            new RamUse(65536, freeMb),
            new ProcessTotals(300, privateMb, 20000),
            [new AppUse("Claude Code", 20, 8000, 0), new AppUse(Attribution.Other, 280, 1000, 1000)],
            new SessionUse(3, new Spread(600, 800, 1000), at == Now ? null : new Spread(700, 900, 1500), new Dictionary<string, int> { ["bash.exe"] = 3 }),
            [],
            []);

    private static string Touch(WorkloadPaths paths, string name, int days)
    {
        Directory.CreateDirectory(paths.Folder);
        var path = Path.Join(paths.Folder, name);
        File.WriteAllText(path, string.Empty);
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime.AddDays(-days));
        return path;
    }
}

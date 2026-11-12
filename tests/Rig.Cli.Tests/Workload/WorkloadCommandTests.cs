using System.CommandLine;
using System.ComponentModel;
using Rig.Cli.Workload;
using Rig.Core.Processes;
using Rig.Core.Services;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;
using Rig.Core.Workload;

namespace Rig.Cli.Tests.Workload;

/// <summary><c>rig workload</c> through its command line, with a fake process table, a temp data folder and a fake launchctl.</summary>
public sealed class WorkloadCommandTests : IDisposable
{
    private readonly TempDir temp = new();
    private readonly FakeProc proc = new();
    private readonly FakeProcessTable table = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();
    private readonly WorkloadPaths paths;
    private readonly Dictionary<string, string> onPath = new(StringComparer.Ordinal)
    {
        ["rig"] = "/Users/me/.local/bin/rig",
        ["dotnet"] = "/usr/local/share/dotnet/dotnet",
    };

    private bool macOs;

    public WorkloadCommandTests()
    {
        paths = new WorkloadPaths(temp.Folder("base"));
        table.Add(1, 0, "claude.exe", 700).Add(2, 1, "dotnet.exe", 2000);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Plist => Path.Join(temp.Path, "agents", WorkloadService.PlistName);

    private string Output => output.ToString().ReplaceLineEndings("\n");

    private string Error => error.ToString().ReplaceLineEndings("\n");

    public void Dispose()
    {
        output.Dispose();
        error.Dispose();
        temp.Dispose();
    }

    [Fact]
    public async Task Sample_appends_a_line_and_prints_one_summary_line()
    {
        Assert.Equal(0, await Run("sample"));

        var line = Assert.Single(await File.ReadAllLinesAsync(paths.DayFile(clock.Now), Ct));
        Assert.Contains("\"host\":\"box\"", line, StringComparison.Ordinal);
        Assert.StartsWith("2026-11-11T12:00:00Z free 32.0/64.0 GB, private 2.6 GB in 2 processes, 1 Claude sessions, 1 large, 0 bursts -> ", Output, StringComparison.Ordinal);
        Assert.DoesNotContain("pruned", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sample_reports_what_it_pruned()
    {
        Directory.CreateDirectory(paths.Folder);
        var old = Path.Join(paths.Folder, "2026-11-01.jsonl");
        await File.WriteAllTextAsync(old, string.Empty, Ct);
        File.SetLastWriteTimeUtc(old, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, await Run("sample"));

        Assert.Contains("; pruned 1 old file(s)", Output, StringComparison.Ordinal);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public async Task Sample_fails_cleanly_when_the_table_cannot_be_read()
    {
        table.Failure = new Win32Exception(5, "Access is denied");

        Assert.Equal(WorkloadCommand.FailedExit, await Run("sample"));

        Assert.Equal("could not sample: Access is denied\n", Error);
        Assert.False(Directory.Exists(paths.Folder));
    }

    [Fact]
    public async Task Status_and_summary_with_nothing_recorded()
    {
        Assert.Equal(0, await Run("status"));
        Assert.Equal(0, await Run("summary"));

        Assert.Equal($"folder: {paths.Folder}\nno samples yet\nNo samples in that window.\n", Output);
    }

    [Fact]
    public async Task Status_counts_files_and_the_newest_sample()
    {
        Assert.Equal(0, await Run("sample"));
        clock.Advance(TimeSpan.FromMinutes(20));
        output.GetStringBuilder().Clear();

        Assert.Equal(0, await Run("status"));

        Assert.Contains("1 day file(s), 2026-11-11.jsonl to 2026-11-11.jsonl", Output, StringComparison.Ordinal);
        Assert.Contains("1 sample(s) in the last 24 h; newest 2026-11-11 13:00 (20 min ago)", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_says_when_the_last_day_had_no_sample()
    {
        Assert.Equal(0, await Run("sample"));
        clock.Advance(TimeSpan.FromDays(2));
        output.GetStringBuilder().Clear();

        Assert.Equal(0, await Run("status"));

        Assert.Contains("no sample in the last 24 h", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_reads_the_window()
    {
        Assert.Equal(0, await Run("sample"));
        clock.Advance(TimeSpan.FromHours(3));
        output.GetStringBuilder().Clear();

        Assert.Equal(0, await Run("summary", "--hours", "2"));
        Assert.Equal("No samples in that window.\n", Output);
        output.GetStringBuilder().Clear();

        Assert.Equal(0, await Run("summary"));
        Assert.StartsWith("1 samples from 2026-11-11 13:00 to 2026-11-11 13:00 on box\n", Output, StringComparison.Ordinal);
        Assert.Equal(WorkloadCommand.FailedExit, await Run("summary", "--hours", "0"));
        Assert.Equal("--hours must be at least 1\n", Error);
    }

    [Fact]
    public async Task Service_is_macos_only()
    {
        Assert.Equal(WorkloadCommand.FailedExit, await Run("service", "install"));
        Assert.Equal(0, await Run("service", "uninstall"));
        Assert.Equal(0, await Run("service", "status"));

        Assert.Contains("Only on macOS", Error, StringComparison.Ordinal);
        Assert.Equal("Not installed.\nservice: macOS only; on Windows the jobs coordinator runs `workload sample`\n", Output);
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public async Task Service_installs_reports_and_uninstalls_on_macos()
    {
        macOs = true;
        Launchctl(loaded: true);

        Assert.Equal(0, await Run("service", "install"));
        var plist = await File.ReadAllTextAsync(Plist, Ct);
        Assert.Contains("<string>/Users/me/.local/bin:/usr/local/share/dotnet:/usr/local/bin:", plist, StringComparison.Ordinal);
        Assert.Contains(proc.Requests, request => request.Argv.SequenceEqual(["launchctl", "bootstrap", "gui/501", Plist]));
        Assert.Equal(0, await Run("status"));
        Assert.Equal(0, await Run("service", "uninstall"));
        Assert.Equal(0, await Run("service", "status"));

        Assert.Equal(
            $"loaded {Plist}; samples every 15 minutes into {paths.Folder}\n"
            + $"folder: {paths.Folder}\nno samples yet\nservice: loaded ({Plist})\n"
            + "removed\n"
            + $"service: not installed ({Plist})\n",
            Output);
    }

    [Fact]
    public async Task Service_status_tells_installed_from_loaded()
    {
        macOs = true;
        Launchctl(loaded: false);
        Directory.CreateDirectory(Path.GetDirectoryName(Plist)!);
        await File.WriteAllTextAsync(Plist, string.Empty, Ct);

        Assert.Equal(0, await Run("service", "status"));

        Assert.Equal($"service: installed, not loaded ({Plist})\n", Output);
    }

    [Fact]
    public async Task Service_install_needs_rig_on_path()
    {
        macOs = true;
        onPath.Remove("rig");

        Assert.Equal(WorkloadCommand.FailedExit, await Run("service", "install"));

        Assert.Equal("rig is not on PATH; run `rig runtime install` first.\n", Error);
    }

    private void Launchctl(bool loaded)
    {
        _ = proc.Answer(new Ran(0, string.Empty, string.Empty), "launchctl");
        _ = proc.Answer(new Ran(loaded ? 0 : 113, string.Empty, string.Empty), "launchctl", "print");
    }

    private Task<int> Run(params string[] args)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test+1", "test+1");
        var host = new WorkloadHost(paths, macOs, macOs ? "macos" : "windows", "box", clock, zone, name => onPath.GetValueOrDefault(name));
        var parts = new WorkloadParts(table, _ => Task.FromResult(new Launchd(proc, Path.Join(temp.Path, "agents"), "gui/501")));
        return new WorkloadCommand(host, parts, output, error).Build().Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, Ct);
    }
}

using Rig.Core.Desktop;

namespace Rig.Core.Tests.Desktop;

/// <summary>
/// Pinning an entry's window to the pid its app records, so two profiles of one app each claim their own window.
/// Replaces <c>tests/desktop_manager/test_window_pid_file.py</c>.
/// </summary>
public sealed class WindowPidFileTests : IDisposable
{
    private static readonly PlacementSpec Full = new(1, new("full"));

    private readonly TempDir _temp = new();

    private Dictionary<string, string> Subs => new() { ["data"] = _temp.Path };

    public void Dispose() => _temp.Dispose();

    private static LaunchEntry Orca(string name, string pidFile) =>
        new(name) { Path = @"C:\Orca\Orca.exe", Placement = Full, WindowProcessName = "Orca.exe", WindowPidFile = pidFile };

    private static LiveWindow Window(long handle, int pid) =>
        new(handle, "Orca", pid, "Orca.exe", DesktopNumber: 2, ScreenNumber: 1, InferredPlacement: Full);

    private string Write(string relative, string json)
    {
        var path = _temp.File(relative);
        File.WriteAllText(path, json);
        return path;
    }

    private void Runtime(string folder, int pid) => Write($"{folder}/orca-runtime.json", $$"""{"runtimeId": "x", "pid": {{pid}}}""");

    [Fact]
    public void The_recorded_pid_is_read_and_a_missing_or_bad_file_gives_none()
    {
        Runtime("ok", 41);

        Assert.Equal(41, WindowPidFile.RecordedPid(Path.Combine(_temp.Path, "ok", "orca-runtime.json")));
        Assert.Null(WindowPidFile.RecordedPid(Path.Combine(_temp.Path, "missing.json")));
        Assert.Null(WindowPidFile.RecordedPid(Write("bad.json", "{not json")));
        Assert.Null(WindowPidFile.RecordedPid(_temp.Path));
    }

    /// <summary>A pid file's text and the pid Python reads from it.</summary>
    public static TheoryData<string, int?> PidFiles => new()
    {
        { """{"pid": 1.0}""", null },
        { """{"pid": "1"}""", null },
        { """{"pid": true}""", null },
        { """{"pid": 1e3}""", null },
        { """{"pid": null}""", null },
        { "[1]", null },
        { "{}", null },
        { """{"pid": -5}""", -5 },
        { """{"pid": 7, "x": [1]}""", 7 },
        { """{"pid": 1, "pid": 2}""", 2 },
    };

    [Theory]
    [MemberData(nameof(PidFiles))]
    public void Only_a_whole_number_pid_counts(string json, int? pid) => Assert.Equal(pid, WindowPidFile.RecordedPid(Write("pid.json", json)));

    [Fact]
    public void An_entry_without_a_pid_file_allows_any_window()
    {
        var entry = new LaunchEntry("Fork") { WindowProcessName = "Fork.exe" };

        Assert.True(WindowPidFile.WindowOwnerAllowed(entry, 7, Subs));
        Assert.True(WindowPidFile.WindowOwnerAllowed(entry, null, Subs));
    }

    [Fact]
    public void Each_profile_claims_only_its_own_window_whatever_the_order()
    {
        Runtime("orca", 100);
        Runtime("orca-personal", 200);
        var work = Orca("Work Orca", "{data}/orca/orca-runtime.json");
        var personal = Orca("Personal Orca", "{data}/orca-personal/orca-runtime.json");
        var (personalWindow, workWindow) = (Window(2, 200), Window(1, 100));

        var first = WindowReuse.ClaimReusableWindow(work, [personalWindow, workWindow], Subs, 2);
        var second = WindowReuse.ClaimReusableWindow(personal, first.Remaining, Subs, 2);

        Assert.Equal(workWindow, first.Window);
        Assert.Equal(personalWindow, second.Window);
    }

    [Fact]
    public void A_profile_that_is_not_running_claims_nothing_so_it_launches()
    {
        Runtime("orca", 100);
        var personal = Orca("Personal Orca", "{data}/orca-personal/orca-runtime.json");

        Assert.Null(WindowReuse.ClaimReusableWindow(personal, [Window(1, 100)], Subs, 2).Window);
    }

    [Fact]
    public void A_captured_window_for_another_profile_does_not_satisfy_the_entry()
    {
        Runtime("orca-personal", 200);
        var personal = Orca("Personal Orca", Path.Combine(_temp.Path, "orca-personal", "orca-runtime.json"));

        static WindowSnapshot Snapshot(int pid) => new("Orca", pid, "Orca.exe", ScreenNumber: 1, InferredPlacement: Full);

        Assert.Equal(0, WindowReuse.CapturedWindowMatchScore(personal, Snapshot(100), new Dictionary<string, string>()));
        Assert.True(WindowReuse.CapturedWindowMatchScore(personal, Snapshot(200), new Dictionary<string, string>()) > 0);
    }

    [Fact]
    public void A_pid_verified_window_on_another_desktop_is_claimed_to_be_moved()
    {
        Runtime("orca", 100);
        var work = Orca("Work Orca", "{data}/orca/orca-runtime.json");
        var elsewhere = Window(1, 100);

        Assert.Equal(elsewhere, WindowReuse.ClaimReusableWindow(work, [elsewhere], Subs, 1).Window);
        Assert.True(WindowPidFile.PidVerified(work, 100, Subs));
    }

    [Fact]
    public void An_unverified_window_on_another_desktop_is_not_claimed()
    {
        Runtime("orca", 100);
        var work = Orca("Work Orca", "{data}/orca/orca-runtime.json");
        var plain = new LaunchEntry("Fork") { WindowProcessName = "Orca.exe", Placement = Full };

        Assert.Null(WindowReuse.ClaimReusableWindow(work, [Window(1, 999)], Subs, 1).Window);
        Assert.Null(WindowReuse.ClaimReusableWindow(plain, [Window(1, 100)], Subs, 1).Window);
        Assert.False(WindowPidFile.PidVerified(plain, 100, Subs));
    }
}

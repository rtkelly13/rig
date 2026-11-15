using Rig.Core.Desktop;

namespace Rig.Core.Tests.Desktop;

/// <summary>The display guard's state machine over a fake machine: drop-outs, flaps, sleep and restore locks.</summary>
public sealed class DisplayGuardTests
{
    private static readonly Rect LeftHalf = new(0, 0, 1280, 1392);
    private static readonly Rect RightHalf = new(1280, 0, 1280, 1392);
    private static readonly Rect Parked = new(2560, 700, 1280, 1152);

    private static readonly GuardMonitor G32Qc = new("DISPLAY3", new(0, 0, 2560, 1440), Primary: true);
    private static readonly GuardMonitor Benq = new("DISPLAY1", new(2560, -427, 4480, 653), Primary: false);
    private static readonly GuardMonitor Iiyama = new("DISPLAY2", new(2560, 700, 4480, 1900), Primary: false);
    private static readonly MonitorSignature AllThree = new([G32Qc, Benq, Iiyama]);
    private static readonly MonitorSignature Dropped = new([Benq, Iiyama]);

    private static WindowPlacementRecord Record(long hwnd, Rect rect, ShowState state = ShowState.Normal, int pid = 100) =>
        new(hwnd, pid, $"Window {hwnd}", "app.exe", state, rect, rect);

    private static void Run(DisplayGuard guard, FakeMachine machine, double seconds, double step = 1.0)
    {
        for (var elapsed = 0.0; elapsed < seconds; elapsed += step)
        {
            guard.Step();
            machine.Clock += step;
        }
    }

    private static DisplayGuard Settled(FakeMachine machine)
    {
        var guard = new DisplayGuard(machine, new DisplayGuardSettings { RecoveryPasses = 0 });
        Run(guard, machine, 10);
        Assert.Equal(GuardPhase.Watching, guard.Phase);
        Assert.Contains(AllThree, guard.Snapshots.Keys);
        return guard;
    }

    [Fact]
    public void Puts_windows_back_when_the_dropped_monitor_returns()
    {
        var machine = new FakeMachine { Windows = { [1] = Record(1, LeftHalf), [2] = Record(2, RightHalf) } };
        var guard = Settled(machine);

        machine.Signature = Dropped;
        machine.Park(1);
        machine.Park(2);
        Run(guard, machine, 3);
        machine.Signature = AllThree;
        Run(guard, machine, 8);

        Assert.Equal([1, 2], Assert.Single(machine.Applied));
        Assert.Equal(LeftHalf, machine.Windows[1].NormalRect);
        Assert.Equal(RightHalf, machine.Windows[2].NormalRect);
    }

    [Fact]
    public void Only_moves_windows_that_differ_from_the_snapshot()
    {
        var machine = new FakeMachine { Windows = { [1] = Record(1, LeftHalf), [2] = Record(2, RightHalf) } };
        var guard = Settled(machine);

        machine.Signature = Dropped;
        machine.Park(2);
        Run(guard, machine, 3);
        machine.Signature = AllThree;
        Run(guard, machine, 8);

        Assert.Equal([2], Assert.Single(machine.Applied));
    }

    [Fact]
    public void Waits_for_the_topology_to_settle_through_a_flap()
    {
        var machine = new FakeMachine { Windows = { [1] = Record(1, LeftHalf) } };
        var guard = Settled(machine);

        foreach (var signature in new[] { Dropped, AllThree, Dropped })
        {
            machine.Signature = signature;
            machine.Park(1);
            Run(guard, machine, 3);
        }

        Assert.Empty(machine.Applied);
        machine.Signature = AllThree;
        Run(guard, machine, 5);
        Assert.Empty(machine.Applied);
        Run(guard, machine, 3);
        Assert.Equal([1], Assert.Single(machine.Applied));
    }

    [Fact]
    public void A_display_event_without_a_signature_change_still_triggers_a_check()
    {
        var machine = new FakeMachine { Windows = { [1] = Record(1, LeftHalf) } };
        var guard = Settled(machine);

        machine.Events += 2;
        machine.Park(1);
        Run(guard, machine, 8);

        Assert.Equal([1], Assert.Single(machine.Applied));
    }

    [Fact]
    public void A_suspend_gap_triggers_a_check()
    {
        var machine = new FakeMachine { Windows = { [1] = Record(1, LeftHalf) } };
        var guard = Settled(machine);

        machine.Park(1);
        machine.Clock += 3600;
        Run(guard, machine, 8);

        Assert.Equal([1], Assert.Single(machine.Applied));
        Assert.Contains(machine.Messages, message => message.Contains("suspend gap", StringComparison.Ordinal));
    }

    [Fact]
    public void Ignores_windows_that_closed_and_leaves_new_ones_alone()
    {
        var machine = new FakeMachine { Windows = { [1] = Record(1, LeftHalf), [2] = Record(2, RightHalf) } };
        var guard = Settled(machine);

        machine.Signature = Dropped;
        _ = machine.Windows.Remove(2);
        machine.Windows[3] = Record(3, Parked);
        machine.Park(1);
        Run(guard, machine, 3);
        machine.Signature = AllThree;
        Run(guard, machine, 8);

        Assert.Equal([1], Assert.Single(machine.Applied));
        Assert.Equal(Parked, machine.Windows[3].NormalRect);
    }

    [Fact]
    public void A_restore_pauses_the_guard_and_clears_every_snapshot()
    {
        var machine = new FakeMachine { Windows = { [1] = Record(1, LeftHalf) } };
        var guard = Settled(machine);

        machine.Restoring = true;
        Run(guard, machine, 2);

        Assert.Equal(GuardPhase.Paused, guard.Phase);
        Assert.Empty(guard.Snapshots);
        Assert.Contains(machine.Messages, message => message.Contains("pausing", StringComparison.Ordinal));

        machine.Restoring = false;
        Run(guard, machine, 10);
        Assert.Equal(GuardPhase.Watching, guard.Phase);
    }

    [Fact]
    public void A_dry_run_reports_without_moving()
    {
        var machine = new FakeMachine { Windows = { [1] = Record(1, LeftHalf) } };
        var guard = new DisplayGuard(machine, new DisplayGuardSettings { RecoveryPasses = 0, DryRun = true });
        Run(guard, machine, 10);

        machine.Signature = Dropped;
        machine.Park(1);
        Run(guard, machine, 3);
        machine.Signature = AllThree;
        Run(guard, machine, 8);

        Assert.Empty(machine.Applied);
        Assert.Contains(machine.Messages, message => message.Contains("Dry run: would restore 1 window", StringComparison.Ordinal));
    }

    [Fact]
    public void A_window_that_moved_a_little_is_left_alone()
    {
        var machine = new FakeMachine { Windows = { [1] = Record(1, LeftHalf) } };
        _ = Settled(machine);
        machine.Windows[1] = machine.Windows[1] with { WindowRect = new(4, 4, 1280, 1392) };

        Assert.False(DisplayGuard.PlacementDiffers(Record(1, LeftHalf), machine.Windows[1]));
    }

    [Fact]
    public void A_minimized_windows_parking_spot_does_not_count()
    {
        var expected = Record(1, LeftHalf, ShowState.Minimized);
        var actual = expected with { WindowRect = Parked };

        Assert.False(DisplayGuard.PlacementDiffers(expected, actual));
        Assert.True(DisplayGuard.PlacementDiffers(expected, actual with { ShowState = ShowState.Normal }));
    }

    [Fact]
    public void Describes_a_signature_and_a_move()
    {
        Assert.Equal(
            "2 monitor(s): DISPLAY1@2560,-427 1920x1080; DISPLAY2*@0,0 2560x1440",
            new MonitorSignature([new("DISPLAY1", new(2560, -427, 4480, 653), false), new("DISPLAY2", new(0, 0, 2560, 1440), true)]).Describe());
        Assert.Equal("no monitors", MonitorSignature.Empty.Describe());

        var expected = Record(1, LeftHalf) with { ProcessName = "app.exe", Title = new string('t', 70) };
        var actual = expected with { WindowRect = Parked, ShowState = ShowState.Minimized };
        var described = DisplayGuard.DescribeMove(expected, actual);

        Assert.StartsWith("app.exe [1] 'ttt", described, StringComparison.Ordinal);
        Assert.EndsWith("normal 0,0 1280x1392", described, StringComparison.Ordinal);
        Assert.Contains("minimized 2560,700 1280x1152 ->", described, StringComparison.Ordinal);
    }

    private sealed class FakeMachine : IDisplayGuardPorts
    {
        public MonitorSignature Signature { get; set; } = AllThree;

        public Dictionary<long, WindowPlacementRecord> Windows { get; init; } = [];

        public double Clock { get; set; } = 1000.0;

        public int Events { get; set; }

        public bool Restoring { get; set; }

        public List<long[]> Applied { get; } = [];

        public List<string> Messages { get; } = [];

        public double Now => Clock;

        public MonitorSignature MonitorSignature() => Signature;

        public int DisplayEventCount() => Events;

        public bool RestoreInProgress() => Restoring;

        public IReadOnlyList<WindowPlacementRecord> CapturePlacements() => [.. Windows.Values];

        public IReadOnlyList<long> ApplyPlacements(IReadOnlyList<WindowPlacementRecord> records)
        {
            Applied.Add([.. records.Select(record => record.Hwnd)]);
            foreach (var record in records)
            {
                Windows[record.Hwnd] = record;
            }

            return [.. records.Select(record => record.Hwnd)];
        }

        public void Log(string message) => Messages.Add(message);

        public void Park(long hwnd) => Windows[hwnd] = Windows[hwnd] with { NormalRect = Parked, WindowRect = Parked };
    }
}

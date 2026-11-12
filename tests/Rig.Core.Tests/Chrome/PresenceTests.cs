using System.Runtime.Versioning;
using Microsoft.Win32;
using Rig.Core.Chrome;
using Rig.Core.Chrome.Windows;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Chrome;

/// <summary>Waiting for a clear machine, idle and input checks, and live-call detection. Replaces <c>test_chrome_presence</c>.</summary>
public sealed class PresenceTests
{
    private sealed class Clock
    {
        public double Now { get; set; }

        public List<double> Pauses { get; } = [];

        public double Read() => Now;

        public void Sleep(double seconds)
        {
            Pauses.Add(seconds);
            Now += seconds;
        }
    }

    private static Func<Block?> Blocks(Clock clock, double clearAt, bool hint = false) =>
        () => clock.Now >= clearAt ? null : new Block("busy", hint ? clearAt - clock.Now : null);

    private static ChromeWindow Window(params string[] names)
    {
        var tabs = names.Select((name, index) =>
        {
            var parsed = TabNames.Parse(name);
            return new ChromeTab(new RuntimeId([42, 1, index]), 1, index, name, parsed.Title, index == 0, false, parsed.Loaded, parsed.Audible)
            {
                Alerts = parsed.Alerts,
            };
        }).ToList();
        return new ChromeWindow(1, "w", "Profile 4", "Ryan", 1, false, 1, tabs);
    }

    [Fact]
    public void Clear_at_once_does_not_sleep()
    {
        var clock = new Clock();
        var outcome = Presence.WaitUntilClear(Blocks(clock, 0), 60, clock.Sleep, clock.Read);
        Assert.Equal((true, 1, null), (outcome.Ready, outcome.Checks, outcome.LastBlock));
        Assert.Empty(clock.Pauses);
    }

    [Fact]
    public void Pauses_double_up_to_the_cap()
    {
        var clock = new Clock();
        var outcome = Presence.WaitUntilClear(Blocks(clock, 1000), 400, clock.Sleep, clock.Read, firstPause: 5, maxPause: 60);
        Assert.False(outcome.Ready);
        Assert.Equal([5, 10, 20, 40, 60, 60], clock.Pauses.Take(6));
        Assert.Equal(400, clock.Pauses.Sum());
        Assert.Equal("busy", outcome.LastBlock);
        Assert.Equal(400, outcome.WaitedSeconds);
    }

    [Fact]
    public void A_hint_wakes_the_wait_exactly_when_the_block_may_clear()
    {
        var clock = new Clock();
        var outcome = Presence.WaitUntilClear(Blocks(clock, 3, hint: true), 600, clock.Sleep, clock.Read, firstPause: 60);
        Assert.True(outcome.Ready);
        Assert.Equal([3.0], clock.Pauses);
        Assert.Equal("busy", outcome.LastBlock);
    }

    [Fact]
    public void A_hint_never_wakes_sooner_than_half_a_second()
    {
        var clock = new Clock();
        Presence.WaitUntilClear(Blocks(clock, 0.1, hint: true), 600, clock.Sleep, clock.Read);
        Assert.Equal([0.5], clock.Pauses);
    }

    [Fact]
    public void Capped_pauses_catch_an_idle_spell_well_before_a_five_minute_lock()
    {
        // The user stops typing at t=500; grouping wants 60s idle; the machine locks at 300s idle.
        var clock = new Clock();
        const double wentIdle = 500;
        Block? Blocked()
        {
            var idle = clock.Now >= wentIdle ? clock.Now - wentIdle : 0;
            return idle >= 60 ? null : new Block("busy", 60 - idle);
        }

        var outcome = Presence.WaitUntilClear(Blocked, 3600, clock.Sleep, clock.Read, maxPause: 60);
        Assert.True(outcome.Ready);
        Assert.True(clock.Now - wentIdle < 300);
    }

    [Fact]
    public void Idle_time_survives_a_tick_wrap()
    {
        var presence = new FakePresence { Now = 1_000, LastInput = uint.MaxValue - 999 };
        Assert.Equal(2.0, Presence.IdleSeconds(presence));
    }

    [Fact]
    public void Input_watch_ignores_its_own_steps_and_sees_the_users()
    {
        var presence = new FakePresence();
        var watch = new InputWatch(presence);

        presence.LastInput = presence.Now + 200;
        Assert.False(watch.UserMoved());
        presence.LastInput = presence.Now - 5_000;
        Assert.False(watch.UserMoved());
        presence.LastInput = presence.Now + 301;
        Assert.True(watch.UserMoved());

        presence.Now += 1_000;
        watch.Mark();
        Assert.False(watch.UserMoved());
    }

    [Fact]
    public void Input_stop_fires_once_the_user_moves()
    {
        var presence = new FakePresence();
        var stop = new InputStop(new InputWatch(presence));

        Assert.Null(stop.Check());
        Assert.False(stop.Fired);
        presence.LastInput = presence.Now + 1_000;
        Assert.Equal("keyboard or mouse input", stop.Check());
        Assert.True(stop.Fired);
    }

    [Theory]
    [InlineData("Meet – abc-defg-hij - Desktop content shared - Memory usage - 300 MB", "desktop content shared")]
    [InlineData("Meet – abc-defg-hij – Microphone recording", "microphone recording")]
    [InlineData("Quarterly review - Google Slides - Tab content shared", "tab content shared")]
    [InlineData("Meet – abc-defg-hij - Audio playing", "a call is live (Meet")]
    public void A_recording_sharing_or_audible_call_tab_is_live(string name, string says)
    {
        var live = Unattended.LiveCall(new FakePresence(), [Window("Inbox - Gmail", name)]);
        Assert.NotNull(live);
        Assert.Contains(says, live, StringComparison.Ordinal);
    }

    [Fact]
    public void Quiet_tabs_and_idle_devices_are_not_live() =>
        Assert.Null(Unattended.LiveCall(new FakePresence(), [Window("Meet – abc-defg-hij", "Lo-fi beats - YouTube - Audio playing")]));

    [Fact]
    public void Chrome_holding_the_microphone_is_live_without_any_tab_alert() =>
        Assert.Equal(
            "a call is live: Chrome is using the microphone and camera",
            Unattended.LiveCall(new FakePresence { Devices = ["microphone", "camera"] }, []));

    [Fact]
    public void A_device_check_that_fails_is_not_live() =>
        Assert.Null(Unattended.LiveCall(new FakePresence { DeviceError = new IOException("no registry") }, []));

    [Fact]
    public void Block_reports_a_live_call_once_idle()
    {
        var block = Unattended.GroupBlock(new FakePresence().Idle(120), () => [Window("Meet – abc-defg-hij - Camera and microphone recording")], 1);
        Assert.NotNull(block);
        Assert.Contains("camera and microphone recording", block.Reason, StringComparison.Ordinal);
        Assert.Null(block.ClearsIn);
    }

    [Fact]
    public void Block_reports_a_locked_session_first() =>
        Assert.Equal(new Block("the session is locked"), Unattended.GroupBlock(new FakePresence { Locked = true }.Idle(9999), () => [], 1));

    [Fact]
    public void Block_says_how_long_until_idle_is_enough()
    {
        var block = Unattended.GroupBlock(new FakePresence().Idle(20), () => [], 1);
        Assert.Equal(new Block("the machine was used 20s ago (waits for 1 min idle)", 40), block);
        Assert.Equal("the machine was used 20s ago (waits for 0.5 min idle)", Unattended.GroupBlock(new FakePresence().Idle(20.5), () => [], 0.5)!.Reason);
    }

    [Fact]
    public void Idle_unlocked_machine_without_a_call_is_clear() =>
        Assert.Null(Unattended.GroupBlock(new FakePresence().Idle(61), () => [], 1));

    [Fact]
    public void Session_is_not_locked_while_tests_run()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            Assert.Skip("Needs the Windows input desktop.");
            return;
        }

        var presence = new NativePresence();
        Assert.False(presence.SessionLocked());
        Assert.True(Presence.IdleSeconds(presence) >= 0);
        Assert.Subset(new HashSet<string> { "microphone", "camera" }, presence.DevicesInUse().ToHashSet());
    }

    [Fact]
    public void Devices_in_use_reads_the_privacy_settings()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            Assert.Skip("Reads the Windows registry.");
            return;
        }

        ReadsTheRegistry();
    }

    [SupportedOSPlatform("windows6.1")]
    private static void ReadsTheRegistry()
    {
        var root = $@"Software\rig-tests\{Guid.NewGuid():N}";
        using var user = Registry.CurrentUser.CreateSubKey(root);
        try
        {
            const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
            void App(string store, string name, long? start, long? stop)
            {
                using var app = user.CreateSubKey($@"{Store}\{store}\NonPackaged\{name}");
                if (start is { } started)
                {
                    app.SetValue("LastUsedTimeStart", started, RegistryValueKind.QWord);
                }

                if (stop is { } stopped)
                {
                    app.SetValue("LastUsedTimeStop", stopped, RegistryValueKind.QWord);
                }
            }

            App("microphone", @"C:#Program Files#Other#other.exe", 5, 0);
            App("microphone", @"C:#Program Files#Google#Chrome#Application#chrome.exe", 5, 9);
            App("microphone", @"C:#Chrome#CHROME.EXE", 5, null);
            Assert.Empty(NativePresence.DevicesInUse(user, "chrome.exe"));

            App("webcam", @"C:#Program Files#Google#Chrome#Application#chrome.exe", 5, 0);
            App("microphone", @"D:#Chrome#chrome.exe", 5, 0);
            Assert.Equal(["microphone", "camera"], NativePresence.DevicesInUse(user, "Chrome.exe"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }
}

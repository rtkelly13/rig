using Rig.Core.Desktop;

namespace Rig.Core.Tests.Desktop;

/// <summary>
/// Choosing an open window to reuse for a launch entry. Replaces <c>tests/desktop_manager/test_reusable_window_matching.py</c>
/// (apart from the Win32 move test, which belongs to the s08–s12 port) and the describe_launch half of
/// <c>test_restore_pure_helpers.py</c>. Identity scores were checked against the Python with <c>PureWindowsPath</c>, as it runs on Windows.
/// </summary>
public sealed class WindowReuseTests
{
    private const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string Terminal = "WindowsTerminal.exe";

    private static readonly Dictionary<string, string> Subs = new() { ["workspace"] = "/work", ["personal-workspace"] = "/personal" };
    private static readonly Dictionary<string, string> NoSubs = [];

    private static PlacementSpec Thirds(int slot, PlacementLayout? subdivide = null) => new(1, new("thirds", slot), subdivide);

    private static PlacementLayout Halves(int slot, string orientation) => new("halves", slot, orientation);

    private static LaunchEntry TerminalEntry(string name, string command, PlacementSpec placement, string cwd = "{workspace}") =>
        new(name) { Type = LaunchEntry.Terminal, Command = command, Cwd = cwd, Shell = "wt-pwsh", WindowTitle = name, Placement = placement, WindowProcessName = Terminal };

    private static LiveWindow TerminalWindow(long handle, string title, int? desktop, int? screen = 1, PlacementSpec? inferred = null) =>
        new(handle, title, ProcessName: Terminal, DesktopNumber: desktop, ScreenNumber: screen, InferredPlacement: inferred);

    private static LaunchEntry VsCode(string name, string title, int slot) =>
        new(name) { Path = @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", WindowTitle = title, WindowProcessName = "Code.exe", Placement = new(1, Halves(slot, "vertical")) };

    [Fact]
    public void An_already_placed_window_outscores_one_in_the_wrong_place()
    {
        var placement = new PlacementSpec(2, Halves(2, "vertical"));
        var entry = new LaunchEntry("Personal Browser") { Path = ChromePath, Args = ["--profile-directory=Profile 1"], Placement = placement, WindowProcessName = "chrome.exe" };
        var placed = new LiveWindow(10, "Inbox - Chrome", ProcessName: "chrome.exe", DesktopNumber: 3, ScreenNumber: 2, InferredPlacement: placement);
        var elsewhere = new LiveWindow(20, "Inbox - Chrome", ProcessName: "chrome.exe", DesktopNumber: 1, ScreenNumber: 1, InferredPlacement: new(1, Halves(1, "vertical")));

        Assert.True(WindowReuse.ReusableWindowMatchScore(entry, placed, Subs, 3) > WindowReuse.ReusableWindowMatchScore(entry, elsewhere, Subs, 3));
    }

    [Fact]
    public void A_renamed_terminal_on_another_desktop_scores_nothing()
    {
        var entry = TerminalEntry("Workspace Terminal 1", "opencode", Thirds(1));
        var candidate = TerminalWindow(30, "opencode - shared-utilities", 3, 2, new(2, Halves(1, "vertical"), Halves(1, "horizontal")));

        Assert.Equal(0, WindowReuse.ReusableWindowMatchScore(entry, candidate, Subs, 1));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(null)]
    public void A_renamed_terminal_on_the_target_or_an_unknown_desktop_still_scores(int? desktop)
    {
        var placement = new PlacementSpec(2, Halves(1, "vertical"), Halves(1, "horizontal"));
        var entry = TerminalEntry("Personal Terminal 1", "opencode", placement, "{personal-workspace}");

        Assert.True(WindowReuse.ReusableWindowMatchScore(entry, TerminalWindow(40, "opencode - shared-utilities", desktop, 2, placement), Subs, 3) > 0);
    }

    [Fact]
    public void Terminal_candidates_rank_by_where_they_are()
    {
        var placement = new PlacementSpec(2, new("full"), Halves(1, "vertical"));
        var entry = TerminalEntry("Personal Terminal 1", "opencode", placement, "{personal-workspace}");

        var same = WindowReuse.ReusableWindowMatchScore(entry, TerminalWindow(60, "opencode - shared-utilities", 3, 2, placement), Subs, 3);
        var unknown = WindowReuse.ReusableWindowMatchScore(entry, TerminalWindow(61, "opencode - shared-utilities", null, 2, placement), Subs, 3);
        var cross = WindowReuse.ReusableWindowMatchScore(entry, TerminalWindow(62, "opencode - shared-utilities", 1), Subs, 3);

        Assert.True(same > unknown);
        Assert.True(unknown > 0);
        Assert.Equal(0, cross);
    }

    [Fact]
    public void A_window_known_to_be_on_another_desktop_is_not_a_candidate()
    {
        var entry = TerminalEntry("Workspace Terminal 1", "opencode", Thirds(1));
        var same = TerminalWindow(10, "Workspace Terminal 1", 1, null);
        var cross = TerminalWindow(20, "Workspace Terminal 1", 3, null);

        Assert.Same(same, WindowReuse.MatchReusableWindow(entry, [cross, same], Subs, 1));
    }

    [Fact]
    public void Each_work_terminal_takes_the_window_in_its_own_slot()
    {
        PlacementSpec[] slots = [Thirds(1), Thirds(2, Halves(1, "horizontal")), Thirds(2, Halves(2, "horizontal")), Thirds(3)];
        LaunchEntry[] entries =
        [
            TerminalEntry("Workspace Terminal 1", "opencode", slots[0]),
            TerminalEntry("Workspace Terminal 2", "opencode", slots[1]),
            TerminalEntry("Workspace Terminal 3", "opencode", slots[2]),
            TerminalEntry("Workspace Terminal 4", "copilot", slots[3]),
        ];
        IReadOnlyList<LiveWindow> remaining =
        [
            TerminalWindow(11, "OpenCode", 1, 1, slots[0]),
            TerminalWindow(12, "OpenCode", 1, 1, slots[1]),
            TerminalWindow(13, "OpenCode", 1, 1, slots[2]),
            TerminalWindow(14, "GitHub Copilot", 1, 1, slots[3]),
        ];

        var matched = new List<long>();
        foreach (var entry in entries)
        {
            var claim = WindowReuse.ClaimReusableWindow(entry, remaining, Subs, 1);
            matched.Add(Assert.IsType<LiveWindow>(claim.Window).Handle);
            remaining = claim.Remaining;
        }

        Assert.Equal([11, 12, 13, 14], matched);
        Assert.Empty(remaining);
    }

    [Fact]
    public void A_copilot_terminal_takes_the_copilot_window()
    {
        var entry = TerminalEntry("Workspace Terminal 4", "copilot", Thirds(3));
        var opencode = TerminalWindow(10, "OpenCode", 1, 1, Thirds(1));
        var copilot = TerminalWindow(20, "GitHub Copilot", 1, 1, Thirds(3));

        Assert.Same(copilot, WindowReuse.MatchReusableWindow(entry, [opencode, copilot], Subs, 1));
    }

    [Fact]
    public void A_claim_removes_the_window_it_takes()
    {
        var entry = TerminalEntry("Workspace Terminal 1", "opencode", Thirds(1));
        var matched = TerminalWindow(10, "OpenCode", 1, 1, Thirds(1));
        var other = TerminalWindow(20, "GitHub Copilot", 1, 1, Thirds(3));

        var claim = WindowReuse.ClaimReusableWindow(entry, [matched, other], Subs, 1);

        Assert.Same(matched, claim.Window);
        Assert.Equal([other], claim.Remaining);
    }

    [Fact]
    public void A_claim_with_no_match_keeps_every_window()
    {
        var entry = new LaunchEntry("Fork") { WindowProcessName = "Fork.exe" };
        var window = new LiveWindow(1, "Notes", ProcessName: "notes.exe");

        var claim = WindowReuse.ClaimReusableWindow(entry, [window], NoSubs);

        Assert.Null(claim.Window);
        Assert.Equal([window], claim.Remaining);
    }

    [Fact]
    public void Competing_titles_leave_out_the_entry_itself()
    {
        var workspace = VsCode("VS Code Workspace", "(Workspace)", 1);
        var prs = VsCode("PRs To Review", "prs-to-review.md", 2);

        Assert.Equal(["prs-to-review.md"], WindowReuse.CompetingSiblingTitles([workspace, prs], workspace));
        Assert.Equal(["(workspace)"], WindowReuse.CompetingSiblingTitles([workspace, prs], prs));
    }

    [Fact]
    public void A_sibling_on_another_process_is_no_competition()
    {
        var code = VsCode("VS Code Workspace", "(Workspace)", 1);
        var notes = new LaunchEntry("Notes") { WindowTitle = "notes", WindowProcessName = "Notes.exe" };

        Assert.Empty(WindowReuse.CompetingSiblingTitles([code, notes], code));
    }

    [Fact]
    public void A_window_titled_for_a_sibling_is_left_for_it()
    {
        var workspace = VsCode("VS Code Workspace", "(Workspace)", 1);
        var prs = VsCode("PRs To Review", "prs-to-review.md", 2);
        LaunchEntry[] entries = [workspace, prs];
        var window = new LiveWindow(70, "prs-to-review.md - shared-utilities - Visual Studio Code", ProcessName: "Code.exe", DesktopNumber: 1, ScreenNumber: 1);

        Assert.True(WindowReuse.ReusableWindowMatchScore(workspace, window, Subs, 1) > 0);
        Assert.Equal(0, WindowReuse.ReusableWindowMatchScore(workspace, window, Subs, 1, WindowReuse.CompetingSiblingTitles(entries, workspace)));
        Assert.True(WindowReuse.ReusableWindowMatchScore(prs, window, Subs, 1, WindowReuse.CompetingSiblingTitles(entries, prs)) > 0);

        IReadOnlyList<LiveWindow> remaining = [window];
        var claimed = new Dictionary<string, LiveWindow?>();
        foreach (var entry in entries)
        {
            var claim = WindowReuse.ClaimReusableWindow(entry, remaining, Subs, 1, WindowReuse.CompetingSiblingTitles(entries, entry));
            remaining = claim.Remaining;
            claimed[entry.Name] = claim.Window;
        }

        Assert.Null(claimed["VS Code Workspace"]);
        Assert.Same(window, claimed["PRs To Review"]);
    }

    [Fact]
    public void Matching_bounds_add_to_the_score_only_when_the_screens_are_known()
    {
        var entry = new LaunchEntry("Notes") { Path = @"C:\Apps\Notes.exe", Placement = Thirds(1) };
        var window = new LiveWindow(1, "Notes", ProcessName: "notes.exe", DesktopNumber: 1, Bounds: new Rect(0, 0, 100, 100));

        var without = WindowReuse.ReusableWindowMatchScore(entry, window, NoSubs, 1);
        var with = WindowReuse.ReusableWindowMatchScore(entry, window, NoSubs, 1, placementBounds: _ => new Rect(0, 0, 100, 100));

        Assert.True(with > without);
    }

    /// <summary>An entry, the window's title and process, and the identity score Python gives them.</summary>
    public static TheoryData<string, LaunchEntry, string, string?, int> Identities => new()
    {
        { "meet hint", Chrome("Meet", "https://meet.google.com/abc"), "Meet - abc", "chrome.exe", 270 },
        { "meet exact", Chrome("Meet", "https://meet.google.com/abc"), "Google Meet", "chrome.exe", 320 },
        { "new tab", Chrome("Mail", "https://mail.google.com/"), "New Tab", "chrome.exe", 1 },
        { "new tab on another process", Chrome("Mail", "https://mail.google.com/"), "New Tab", "msedge.exe", 0 },
        { "site path", Chrome("Site", "https://www.example.com/docs/page"), "Example docs", "chrome.exe", 270 },
        { "localhost", Chrome("Local", "http://localhost:3000/app;x?q#f"), "app - Chrome", "chrome.exe", 270 },
        { "params after the last slash", Chrome("P", "https://x.org/a;b/c;d"), "x org", "chrome.exe", 320 },
        { "params inside the path", Chrome("P", "https://x.org/a;b/c;d"), "a;b", "chrome.exe", 320 },
        { "calendar", Chrome("Cal", "https://calendar.google.com/r"), "Google Calendar - Week", "chrome.exe", 270 },
        { "docs", Chrome("Doc", "https://docs.google.com/d/1"), "Untitled - Google Docs", "chrome.exe", 270 },
        { "gmail", Chrome("G", "https://gmail.com/"), "Inbox - Gmail", "chrome.exe", 270 },
        { "claude busy", Term("claude --resume", Terminal), "\u2810 working", Terminal, 260 },
        { "claude title", Term("claude", Terminal), "Claude Code", Terminal, 280 },
        { "claude no hint", Term("claude", null), "pwsh", null, 0 },
        { "opencode prefix", Term("opencode", null), "oc | x", null, 160 },
        { "opencode no hint", Term("opencode", null), "pwsh", null, 0 },
        { "copilot no hint", Term("copilot", Terminal), "pwsh", Terminal, 100 },
        { "other command", Term("vim", null), "vim", null, 0 },
        { "declared process mismatch", new("Code") { WindowTitle = "Code", WindowProcessName = "Code.exe" }, "Code", "warp.exe", 0 },
        { "entry name in title", new("Notes") { Path = @"C:\Apps\Notes.exe" }, "My Notes", "notes.exe", 125 },
        { "entry name in a chrome title", new("Notes") { Path = @"C:\Apps\Notes.exe" }, "My Notes", "chrome.exe", 0 },
        { "casefold", new("x") { WindowTitle = "STRASSE" }, "Stra\u00dfe", null, 200 },
        { "title substring", new("x") { WindowTitle = "  Inbox " }, "Inbox - Mail", null, 175 },
        { "url on another browser", new("Edge") { Path = @"C:\Edge\msedge.exe", Args = ["https://example.com"] }, "Example", "msedge.exe", 1 },
    };

    [Theory]
    [MemberData(nameof(Identities))]
    public void Identity_scores_match_python(string label, LaunchEntry entry, string title, string? process, int score)
    {
        Assert.NotEmpty(label);
        Assert.Equal(score, WindowReuse.WindowIdentityMatchScore(entry, title, process, NoSubs));
    }

    [Fact]
    public void A_captured_window_scores_its_screen_and_place()
    {
        var entry = new LaunchEntry("Notes") { Path = @"C:\Apps\Notes.exe", Placement = Thirds(1) };
        var placed = new WindowSnapshot("Notes", ProcessName: "notes.exe", ScreenNumber: 1, InferredPlacement: Thirds(1));
        var unplaced = new WindowSnapshot("Notes", ProcessName: "notes.exe", ScreenNumber: 2, InferredPlacement: Thirds(2));
        var unplanned = new LaunchEntry("Notes") { Path = @"C:\Apps\Notes.exe" };

        Assert.Equal(325, WindowReuse.CapturedWindowMatchScore(entry, placed, NoSubs));
        Assert.Equal(125, WindowReuse.CapturedWindowMatchScore(entry, unplaced, NoSubs));
        Assert.Equal(125, WindowReuse.CapturedWindowMatchScore(unplanned, placed, NoSubs));
        Assert.Equal(0, WindowReuse.CapturedWindowMatchScore(entry, placed with { ProcessName = "other.exe", Title = "x" }, NoSubs));
    }

    [Fact]
    public void Placements_compare_as_a_plan_file_writes_them()
    {
        var plain = new PlacementSpec(1, new("columns", 2, MinWidth: 640));
        var counted = new PlacementSpec(1, new("columns", 2, MinWidth: 640, Count: 3));

        Assert.True(WindowReuse.SamePlacement(plain, counted));
        Assert.False(WindowReuse.SamePlacement(plain, Thirds(2)));
    }

    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe", "app.exe")]
    [InlineData("C:/Apps/tool.exe/", "tool.exe")]
    [InlineData("C:app.exe", "app.exe")]
    [InlineData("app.exe", "app.exe")]
    [InlineData(@"C:\", "")]
    public void File_names_split_as_windows_paths(string path, string name) => Assert.Equal(name, WindowReuse.WindowsFileName(path));

    [Fact]
    public void The_expected_process_is_declared_else_taken_from_the_path()
    {
        Assert.Equal("code.exe", WindowReuse.ExpectedProcessName(new("x") { WindowProcessName = "Code.exe", Path = @"C:\a.exe" }, NoSubs));
        Assert.Equal("a.exe", WindowReuse.ExpectedProcessName(new("x") { Path = @"C:\A.exe" }, NoSubs));
        Assert.Null(WindowReuse.ExpectedProcessName(new("x"), NoSubs));
    }

    /// <summary>An entry, the placeholders, and how Python describes launching it.</summary>
    public static TheoryData<LaunchEntry, string> Launches => new()
    {
        { new("Chrome") { Path = @"C:\chrome.exe" }, @"app `Chrome` -> C:\chrome.exe" },
        { new("Chrome") { Path = @"C:\chrome.exe", Args = ["--incognito", "--new-window"] }, @"app `Chrome` -> C:\chrome.exe --incognito --new-window" },
        { new("Editor") { Path = @"{workspace}\editor.exe" }, @"app `Editor` -> /work\editor.exe" },
        { new("Broken"), "app `Broken` -> <missing path>" },
        { new("A") { Path = @"C:\a.exe", Args = ["", "x"] }, @"app `A` -> C:\a.exe x" },
        { new("Dev Terminal") { Type = LaunchEntry.Terminal, Command = "opencode", Cwd = @"C:\code\project", Shell = "wt-pwsh" }, @"terminal `Dev Terminal` -> opencode (cwd: C:\code\project)" },
        { new("Workspace Terminal") { Type = LaunchEntry.Terminal, Command = "opencode", Cwd = "{workspace}" }, "terminal `Workspace Terminal` -> opencode (cwd: /work)" },
        { new("T") { Type = LaunchEntry.Terminal }, "terminal `T` ->  (cwd: None)" },
        { new("Build") { Type = "command", Command = "make all", Cwd = @"C:\project" }, @"command `Build` -> make all (cwd: C:\project)" },
        { new("Script") { Type = "command", Command = "run.sh" }, "command `Script` -> run.sh" },
        { new("Empty") { Type = "command" }, "command `Empty` -> <missing command>" },
        { new("VSCode") { Path = @"C:\code.exe", Args = [@"{workspace}\project.code-workspace"] }, @"app `VSCode` -> C:\code.exe /work\project.code-workspace" },
    };

    [Theory]
    [MemberData(nameof(Launches))]
    public void A_launch_is_described_as_python_does(LaunchEntry entry, string description) =>
        Assert.Equal(description, WindowReuse.DescribeLaunch(entry, Subs));

    private static LaunchEntry Chrome(string name, string url) => new(name) { Path = ChromePath, Args = [url] };

    private static LaunchEntry Term(string command, string? process) =>
        new("T") { Type = LaunchEntry.Terminal, Command = command, WindowProcessName = process };
}

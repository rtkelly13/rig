using System.Text.Json.Nodes;
using Rig.Core.Screens;

namespace Rig.Core.Desktop;

/// <summary>Where a window sits on its screen.</summary>
/// <param name="Mode"><c>full</c>, <c>halves</c>, <c>thirds</c>, <c>two-thirds</c>, <c>quarters</c> or <c>columns</c>.</param>
/// <param name="Slot">Which region of the mode, from 1.</param>
/// <param name="Orientation"><c>halves</c> only: <c>vertical</c> or <c>horizontal</c>.</param>
/// <param name="MinWidth"><c>columns</c> only: the narrowest a column may be, which sets how many fit across the screen.</param>
/// <param name="Count"><c>columns</c> only: how many columns the screen holds this run, set when the plan loads. Never saved.</param>
public sealed record PlacementLayout(string Mode, int Slot = 1, string? Orientation = null, int? MinWidth = null, int? Count = null)
{
    /// <summary>The layout as a plan file holds it; <see cref="Count"/> is runtime-only and left out.</summary>
    public JsonObject ToJson() => new JsonObject { ["mode"] = Mode, ["slot"] = Slot }.AddText("orientation", Orientation).AddIf(MinWidth is not null, "min_width", MinWidth);
}

/// <summary>A window's planned screen and region.</summary>
/// <param name="Screen">The screen, numbered from 1.</param>
/// <param name="Layout">The region of that screen.</param>
/// <param name="Subdivide">An optional split of that region, with the same shape.</param>
public sealed record PlacementSpec(int Screen, PlacementLayout Layout, PlacementLayout? Subdivide = null)
{
    /// <summary>The placement as a plan file holds it.</summary>
    public JsonObject ToJson() => new JsonObject { ["screen"] = Screen, ["layout"] = Layout.ToJson() }.AddIf(Subdivide is not null, "subdivide", Subdivide?.ToJson());
}

/// <summary>A window as a status capture or exported plan records it.</summary>
/// <param name="Title">The window title.</param>
/// <param name="Pid">The owning process id.</param>
/// <param name="ProcessName">The owning process's image name.</param>
/// <param name="ExePath">The owning process's executable.</param>
/// <param name="ClassName">The window class.</param>
/// <param name="ScreenNumber">The screen it is mostly on.</param>
/// <param name="InferredPlacement">The plan placement its bounds match, if any.</param>
/// <param name="Bounds">Its rectangle.</param>
public sealed record WindowSnapshot(
    string Title,
    int? Pid = null,
    string? ProcessName = null,
    string? ExePath = null,
    string? ClassName = null,
    int? ScreenNumber = null,
    PlacementSpec? InferredPlacement = null,
    Rect? Bounds = null)
{
    /// <summary>The snapshot as a plan file holds it; empty fields are left out.</summary>
    public JsonObject ToJson() => new JsonObject { ["title"] = Title }
        .AddIf(Pid is not null, "pid", Pid)
        .AddText("process_name", ProcessName)
        .AddText("exe_path", ExePath)
        .AddText("class_name", ClassName)
        .AddIf(ScreenNumber is not null, "screen_number", ScreenNumber)
        .AddIf(InferredPlacement is not null, "inferred_placement", InferredPlacement?.ToJson())
        .AddIf(Bounds is not null, "bounds", Bounds?.ToJson());
}

/// <summary>One thing a desktop launches: an app or a terminal.</summary>
/// <param name="Name">The entry's name, unique within the plan.</param>
public sealed record LaunchEntry(string Name)
{
    /// <summary>The <see cref="CommandAtLogon"/> value that leaves a logon restore's terminal as a bare shell.</summary>
    public const string CommandAtLogonDeferred = "deferred";

    /// <summary>The <see cref="Type"/> of a terminal entry.</summary>
    public const string Terminal = "terminal";

    /// <summary>The shell a terminal entry with none named opens: Windows Terminal running pwsh.</summary>
    public const string DefaultTerminalShell = "wt-pwsh";

    /// <summary><c>app</c> or <c>terminal</c>.</summary>
    public string Type { get; init; } = "app";

    /// <summary>The executable an app entry starts.</summary>
    public string? Path { get; init; }

    /// <summary>The app's arguments, placeholders unresolved.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>The command a terminal entry runs.</summary>
    public string? Command { get; init; }

    /// <summary>
    /// <c>deferred</c>: a logon restore (<c>workspace restore --defer-logon-commands</c>) opens a bare shell and leaves
    /// <see cref="Command"/> to <c>claude-sessions startup</c>. Any other restore runs the command as usual. Unset (or
    /// <c>run</c>) always runs it.
    /// </summary>
    public string? CommandAtLogon { get; init; }

    /// <summary>The terminal's starting directory.</summary>
    public string? Cwd { get; init; }

    /// <summary>The terminal's shell, such as <c>wt-pwsh</c>.</summary>
    public string? Shell { get; init; }

    /// <summary>How long to wait after launching, in milliseconds.</summary>
    public int DelayMs { get; init; } = 900;

    /// <summary>Where the window goes once it shows.</summary>
    public PlacementSpec? Placement { get; init; }

    /// <summary>The title that identifies the entry's window.</summary>
    public string? WindowTitle { get; init; }

    /// <summary>The process that owns the entry's window, when it is not the launched executable.</summary>
    public string? WindowProcessName { get; init; }

    /// <summary>How long to wait for the window, in milliseconds, when the default will not do.</summary>
    public int? WindowWaitTimeoutMs { get; init; }

    /// <summary>A JSON file holding the <c>pid</c> of the process that owns this entry's window; only that process's window is reused for it.</summary>
    public string? WindowPidFile { get; init; }

    /// <summary>Whether the app allows one window only, so a running one is reused rather than launched again.</summary>
    public bool SingleInstance { get; init; }

    /// <summary>A one-line description.</summary>
    public string? Summary { get; init; }

    /// <summary>Free-form notes.</summary>
    public string? Notes { get; init; }

    /// <summary>Kept in the plan but skipped.</summary>
    public bool Disabled { get; init; }

    /// <summary>The feature flag that switches this entry on.</summary>
    public string? Feature { get; init; }

    /// <summary>Whether a logon restore leaves this terminal's command to <c>claude-sessions startup</c>.</summary>
    public bool DefersCommandAtLogon => CommandAtLogon == CommandAtLogonDeferred;

    /// <summary>The entry as a plan file holds it; empty and default-off fields are left out.</summary>
    public JsonObject ToJson() => new JsonObject { ["name"] = Name, ["type"] = Type, ["delay_ms"] = DelayMs }
        .AddIf(Disabled, "disabled", true)
        .AddText("feature", Feature)
        .AddText("shell", Shell)
        .AddText("path", Path)
        .AddIf(Args.Count > 0, "args", new JsonArray([.. Args.Select(arg => JsonValue.Create(arg))]))
        .AddText("command", Command)
        .AddText("command_at_logon", CommandAtLogon)
        .AddText("cwd", Cwd)
        .AddText("summary", Summary)
        .AddText("notes", Notes)
        .AddIf(Placement is not null, "placement", Placement?.ToJson())
        .AddText("window_title", WindowTitle)
        .AddText("window_process_name", WindowProcessName)
        .AddIf(WindowWaitTimeoutMs is not null, "window_wait_timeout_ms", WindowWaitTimeoutMs)
        .AddText("window_pid_file", WindowPidFile)
        .AddIf(SingleInstance, "single_instance", true);
}

/// <summary>One virtual desktop: its name and what it launches.</summary>
/// <param name="Name">The desktop's name.</param>
public sealed record DesktopPlan(string Name)
{
    /// <summary>What the desktop launches, in order.</summary>
    public IReadOnlyList<LaunchEntry> Launch { get; init; } = [];

    /// <summary>The windows a status capture found on it.</summary>
    public IReadOnlyList<WindowSnapshot> CapturedWindows { get; init; } = [];

    /// <summary>Kept in the plan but left out of a run.</summary>
    public bool Disabled { get; init; }

    /// <summary>The feature flag that switches this desktop on.</summary>
    public string? Feature { get; init; }

    /// <summary>The desktop as a plan file holds it.</summary>
    public JsonObject ToJson() => new JsonObject { ["name"] = Name, ["launch"] = new JsonArray([.. Launch.Select(JsonNode (entry) => entry.ToJson())]) }
        .AddIf(Disabled, "disabled", true)
        .AddText("feature", Feature)
        .AddIf(CapturedWindows.Count > 0, "captured_windows", new JsonArray([.. CapturedWindows.Select(JsonNode (window) => window.ToJson())]));

    /// <summary>The entry a plan bundle lists for this desktop, whose own file sits at <paramref name="path"/>.</summary>
    public JsonObject BundleRef(string path) => new JsonObject { ["path"] = path }.AddText("feature", Feature).AddIf(Disabled, "disabled", true);
}

/// <summary>
/// An app that starts itself at sign-in and belongs in the tray, not on a desktop. Restore puts away the window it
/// opens anyway, such as Docker Desktop's dashboard, after placing the planned windows.
/// </summary>
/// <param name="Name">What the plan calls the app.</param>
/// <param name="ProcessName">The image name of the process whose windows are put away, e.g. <c>Docker Desktop.exe</c>.</param>
public sealed record BackgroundApp(string Name, string ProcessName)
{
    /// <summary>Ask the window to close, as its close button would: right for an app that closes to the tray.</summary>
    public const string Close = "close";

    /// <summary>Minimize the window: for an app that would quit if its window closed.</summary>
    public const string Minimize = "minimize";

    /// <summary>Every <see cref="Action"/> a plan may name.</summary>
    public static readonly IReadOnlyList<string> Actions = [Close, Minimize];

    /// <summary><see cref="Close"/> or <see cref="Minimize"/>.</summary>
    public string Action { get; init; } = Close;

    /// <summary>
    /// How long to wait, in milliseconds, for the window of an app that is running but has none open yet. Unset is no
    /// wait: only the windows open when restore reaches the app are put away, so an app already sitting in the tray
    /// costs a restore nothing.
    /// </summary>
    public int? WindowWaitTimeoutMs { get; init; }

    /// <summary>Why the app is here.</summary>
    public string? Summary { get; init; }

    /// <summary>Kept in the plan but left alone.</summary>
    public bool Disabled { get; init; }

    /// <summary>The wait <see cref="WindowWaitTimeoutMs"/> sets.</summary>
    public TimeSpan WindowWait => WindowWaitTimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;

    /// <summary>The app as a plan file holds it.</summary>
    public JsonObject ToJson() => new JsonObject { ["name"] = Name, ["window_process_name"] = ProcessName }
        .AddIf(Action != Close, "action", Action)
        .AddIf(WindowWaitTimeoutMs is not null, "window_wait_timeout_ms", WindowWaitTimeoutMs)
        .AddText("summary", Summary)
        .AddIf(Disabled, "disabled", true);
}

/// <summary>The whole workspace: every desktop, in order, and the plan's settings.</summary>
public sealed record WorkspacePlan
{
    /// <summary>The plan format version this code writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The plan format version.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>The primary working directory, the <c>{workspace}</c> placeholder.</summary>
    public string? Workspace { get; init; }

    /// <summary>The personal project directory, the <c>{personal-workspace}</c> placeholder.</summary>
    public string? PersonalWorkspace { get; init; }

    /// <summary>How long to wait after switching desktop, in milliseconds.</summary>
    public int SwitchDelayMs { get; init; } = 650;

    /// <summary>The desktops, in position order.</summary>
    public IReadOnlyList<DesktopPlan> Desktops { get; init; } = [];

    /// <summary>Apps that start themselves and whose windows a full restore puts away.</summary>
    public IReadOnlyList<BackgroundApp> Background { get; init; } = [];

    /// <summary>Free-form notes.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The plan's own placeholders.</summary>
    public IReadOnlyDictionary<string, string> Aliases { get; init; } = new OrderedDictionary<string, string>();

    /// <summary>Each declared feature flag and whether it is on.</summary>
    public IReadOnlyDictionary<string, bool> Features { get; init; } = new OrderedDictionary<string, bool>();

    /// <summary>
    /// Names of desktops a switched-off feature dropped from <see cref="Desktops"/> this run. Runtime-only, never
    /// saved: restore uses it to remove a leftover virtual desktop of that name, so switching a desktop's feature off
    /// retires it.
    /// </summary>
    public IReadOnlyList<string> RetiredDesktopNames { get; init; } = [];

    /// <summary>The plan as a single plan file holds it.</summary>
    public JsonObject ToJson() => WithNotesAndAliases(new JsonObject
    {
        ["version"] = Version,
        ["switch_delay_ms"] = SwitchDelayMs,
        ["desktops"] = new JsonArray([.. Desktops.Select(JsonNode (desktop) => desktop.ToJson())]),
    }.AddIf(Features.Count > 0, "features", PlanJsonWriter.Map(Features, flag => JsonValue.Create(flag)))
        .AddText("workspace", Workspace)
        .AddText("personal-workspace", PersonalWorkspace));

    /// <summary>The root file of a plan bundle: this plan's settings, listing desktops by <see cref="DesktopPlan.BundleRef"/>.</summary>
    public JsonObject BundleRoot(IEnumerable<JsonNode> desktopRefs) => WithNotesAndAliases(new JsonObject
    {
        ["version"] = Version,
        ["switch_delay_ms"] = SwitchDelayMs,
        ["bundle"] = new JsonObject { ["desktops"] = new JsonArray([.. desktopRefs]) },
    }.AddText("workspace", Workspace).AddText("personal-workspace", PersonalWorkspace))
        .AddIf(Features.Count > 0, "features", PlanJsonWriter.Map(Features, flag => JsonValue.Create(flag)));

    /// <summary>
    /// Every placeholder name and its resolved value. <paramref name="environment"/> (see
    /// <see cref="Placeholders.EnvironmentAliases"/>) lets the tracked plan say <c>{localappdata}</c> rather than one
    /// user's profile path; a plan alias of the same name wins, and <paramref name="machine"/> (this machine's
    /// <c>tools.desktop.aliases</c>) wins over both. They apply here, at resolution, and never enter
    /// <see cref="Aliases"/>, so saving or exporting a plan cannot write them into it.
    /// </summary>
    public OrderedDictionary<string, string> Substitutions(IReadOnlyDictionary<string, string> environment, IReadOnlyDictionary<string, string> machine)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(machine);
        var values = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in new[] { environment, Aliases, machine })
        {
            foreach (var (name, value) in layer)
            {
                values[name] = value;
            }
        }

        if (!string.IsNullOrEmpty(Workspace))
        {
            values["workspace"] = Workspace;
        }

        if (!string.IsNullOrEmpty(PersonalWorkspace))
        {
            values["personal-workspace"] = PersonalWorkspace;
        }

        return Placeholders.ResolveMap(values);
    }

    private JsonObject WithNotesAndAliases(JsonObject data) => data
        .AddIf(Background.Count > 0, "background", new JsonArray([.. Background.Select(JsonNode (app) => app.ToJson())]))
        .AddIf(Notes.Count > 0, "notes", new JsonArray([.. Notes.Select(note => JsonValue.Create(note))]))
        .AddIf(Aliases.Count > 0, "aliases", PlanJsonWriter.Map(Aliases, value => JsonValue.Create(value)));
}

using System.Text.Json.Nodes;

namespace Rig.Core.Desktop;

/// <summary>A screen rectangle in pixels. Ported from <c>desktop_manager.models.Rect</c>.</summary>
/// <param name="Left">The left edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public sealed record Rect(int Left, int Top, int Width, int Height)
{
    /// <summary>The right edge, one past the last column.</summary>
    public int Right => Left + Width;

    /// <summary>The bottom edge, one past the last row.</summary>
    public int Bottom => Top + Height;

    /// <summary>The horizontal centre, rounded down.</summary>
    public int CenterX => Left + FloorHalf(Width);

    /// <summary>The vertical centre, rounded down.</summary>
    public int CenterY => Top + FloorHalf(Height);

    /// <summary>The rectangle as a plan file holds it.</summary>
    public JsonObject ToJson() => new() { ["left"] = Left, ["top"] = Top, ["width"] = Width, ["height"] = Height };

    private static int FloorHalf(int value) => (int)Math.Floor(value / 2.0);
}

/// <summary>Where a window sits on its screen. Ported from <c>desktop_manager.models.PlacementLayout</c>.</summary>
/// <param name="Mode"><c>full</c>, <c>halves</c>, <c>thirds</c>, <c>two-thirds</c>, <c>quarters</c> or <c>columns</c>.</param>
/// <param name="Slot">Which region of the mode, from 1.</param>
/// <param name="Orientation"><c>halves</c> only: <c>vertical</c> or <c>horizontal</c>.</param>
/// <param name="MinWidth"><c>columns</c> only: the narrowest a column may be, which sets how many fit across the screen.</param>
/// <param name="Count"><c>columns</c> only: how many columns the screen holds this run, set when the plan loads. Never saved.</param>
public sealed record PlacementLayout(string Mode, int Slot = 1, string? Orientation = null, int? MinWidth = null, int? Count = null)
{
    /// <summary>The layout as a plan file holds it; <see cref="Count"/> is runtime-only and left out.</summary>
    public JsonObject ToJson()
    {
        var data = new JsonObject { ["mode"] = Mode, ["slot"] = Slot };
        if (!string.IsNullOrEmpty(Orientation))
        {
            data["orientation"] = Orientation;
        }

        if (MinWidth is { } minWidth)
        {
            data["min_width"] = minWidth;
        }

        return data;
    }
}

/// <summary>A window's planned screen and region. Ported from <c>desktop_manager.models.PlacementSpec</c>.</summary>
/// <param name="Screen">The screen, numbered from 1.</param>
/// <param name="Layout">The region of that screen.</param>
/// <param name="Subdivide">An optional split of that region, with the same shape.</param>
public sealed record PlacementSpec(int Screen, PlacementLayout Layout, PlacementLayout? Subdivide = null)
{
    /// <summary>The placement as a plan file holds it.</summary>
    public JsonObject ToJson()
    {
        var data = new JsonObject { ["screen"] = Screen, ["layout"] = Layout.ToJson() };
        if (Subdivide is not null)
        {
            data["subdivide"] = Subdivide.ToJson();
        }

        return data;
    }
}

/// <summary>A window as a status capture or exported plan records it. Ported from <c>desktop_manager.models.WindowSnapshot</c>.</summary>
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
    public JsonObject ToJson()
    {
        var data = new JsonObject { ["title"] = Title };
        if (Pid is { } pid)
        {
            data["pid"] = pid;
        }

        PlanJsonWriter.AddText(data, "process_name", ProcessName);
        PlanJsonWriter.AddText(data, "exe_path", ExePath);
        PlanJsonWriter.AddText(data, "class_name", ClassName);
        if (ScreenNumber is { } screen)
        {
            data["screen_number"] = screen;
        }

        if (InferredPlacement is not null)
        {
            data["inferred_placement"] = InferredPlacement.ToJson();
        }

        if (Bounds is not null)
        {
            data["bounds"] = Bounds.ToJson();
        }

        return data;
    }
}

/// <summary>One thing a desktop launches: an app or a terminal. Ported from <c>desktop_manager.models.LaunchEntry</c>.</summary>
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
    public JsonObject ToJson()
    {
        var data = new JsonObject { ["name"] = Name, ["type"] = Type, ["delay_ms"] = DelayMs };
        if (Disabled)
        {
            data["disabled"] = true;
        }

        PlanJsonWriter.AddText(data, "feature", Feature);
        PlanJsonWriter.AddText(data, "shell", Shell);
        PlanJsonWriter.AddText(data, "path", Path);
        if (Args.Count > 0)
        {
            data["args"] = new JsonArray([.. Args.Select(arg => JsonValue.Create(arg))]);
        }

        PlanJsonWriter.AddText(data, "command", Command);
        PlanJsonWriter.AddText(data, "command_at_logon", CommandAtLogon);
        PlanJsonWriter.AddText(data, "cwd", Cwd);
        PlanJsonWriter.AddText(data, "summary", Summary);
        PlanJsonWriter.AddText(data, "notes", Notes);
        if (Placement is not null)
        {
            data["placement"] = Placement.ToJson();
        }

        PlanJsonWriter.AddText(data, "window_title", WindowTitle);
        PlanJsonWriter.AddText(data, "window_process_name", WindowProcessName);
        if (WindowWaitTimeoutMs is { } timeout)
        {
            data["window_wait_timeout_ms"] = timeout;
        }

        PlanJsonWriter.AddText(data, "window_pid_file", WindowPidFile);
        if (SingleInstance)
        {
            data["single_instance"] = true;
        }

        return data;
    }
}

/// <summary>One virtual desktop: its name and what it launches. Ported from <c>desktop_manager.models.DesktopPlan</c>.</summary>
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
    public JsonObject ToJson()
    {
        var data = new JsonObject
        {
            ["name"] = Name,
            ["launch"] = new JsonArray([.. Launch.Select(JsonNode (entry) => entry.ToJson())]),
        };
        if (Disabled)
        {
            data["disabled"] = true;
        }

        PlanJsonWriter.AddText(data, "feature", Feature);
        if (CapturedWindows.Count > 0)
        {
            data["captured_windows"] = new JsonArray([.. CapturedWindows.Select(JsonNode (window) => window.ToJson())]);
        }

        return data;
    }

    /// <summary>The entry a plan bundle lists for this desktop, whose own file sits at <paramref name="path"/>.</summary>
    public JsonObject BundleRef(string path)
    {
        var data = new JsonObject { ["path"] = path };
        PlanJsonWriter.AddText(data, "feature", Feature);
        if (Disabled)
        {
            data["disabled"] = true;
        }

        return data;
    }
}

/// <summary>The whole workspace: every desktop, in order, and the plan's settings. Ported from <c>desktop_manager.models.WorkspacePlan</c>.</summary>
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
    public JsonObject ToJson()
    {
        var data = new JsonObject
        {
            ["version"] = Version,
            ["switch_delay_ms"] = SwitchDelayMs,
            ["desktops"] = new JsonArray([.. Desktops.Select(JsonNode (desktop) => desktop.ToJson())]),
        };
        if (Features.Count > 0)
        {
            data["features"] = PlanJsonWriter.Map(Features, flag => JsonValue.Create(flag));
        }

        PlanJsonWriter.AddText(data, "workspace", Workspace);
        PlanJsonWriter.AddText(data, "personal-workspace", PersonalWorkspace);
        AddNotesAndAliases(data);
        return data;
    }

    /// <summary>The root file of a plan bundle: this plan's settings, listing desktops by <see cref="DesktopPlan.BundleRef"/>.</summary>
    public JsonObject BundleRoot(IEnumerable<JsonNode> desktopRefs)
    {
        var root = new JsonObject
        {
            ["version"] = Version,
            ["switch_delay_ms"] = SwitchDelayMs,
            ["bundle"] = new JsonObject { ["desktops"] = new JsonArray([.. desktopRefs]) },
        };
        PlanJsonWriter.AddText(root, "workspace", Workspace);
        PlanJsonWriter.AddText(root, "personal-workspace", PersonalWorkspace);
        AddNotesAndAliases(root);
        if (Features.Count > 0)
        {
            root["features"] = PlanJsonWriter.Map(Features, flag => JsonValue.Create(flag));
        }

        return root;
    }

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

    private void AddNotesAndAliases(JsonObject data)
    {
        if (Notes.Count > 0)
        {
            data["notes"] = new JsonArray([.. Notes.Select(note => JsonValue.Create(note))]);
        }

        if (Aliases.Count > 0)
        {
            data["aliases"] = PlanJsonWriter.Map(Aliases, value => JsonValue.Create(value));
        }
    }
}

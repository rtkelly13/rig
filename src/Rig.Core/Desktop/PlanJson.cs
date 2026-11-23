using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Screens;
using Rig.Core.Text;

namespace Rig.Core.Desktop;

/// <summary>A plan file, or a value in one, that cannot be read as a plan.</summary>
public sealed class InvalidPlanException : Exception
{
    /// <summary>An empty error.</summary>
    public InvalidPlanException()
    {
    }

    /// <summary>An error saying <paramref name="message"/>.</summary>
    public InvalidPlanException(string message)
        : base(message)
    {
    }

    /// <summary>An error saying <paramref name="message"/>, caused by <paramref name="inner"/>.</summary>
    public InvalidPlanException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Building the JSON a plan file holds.</summary>
internal static class PlanJsonWriter
{
    // Text fields are written only when they hold something.
    public static JsonObject AddText(this JsonObject data, string key, string? value) => data.AddIf(!string.IsNullOrEmpty(value), key, value);

    public static JsonObject AddIf(this JsonObject data, bool condition, string key, JsonNode? value)
    {
        if (condition)
        {
            data[key] = value;
        }

        return data;
    }

    public static JsonObject Map<T>(IReadOnlyDictionary<string, T> values, Func<T, JsonNode?> write) =>
        new(values.Select(pair => KeyValuePair.Create(pair.Key, write(pair.Value))));
}

/// <summary>
/// Reading a plan strictly: text is a JSON string, a number a JSON integer, a flag a JSON boolean, a list a JSON array.
/// A value of any other type, a missing required field or a field the plan does not define throws a
/// <see cref="JsonInputException"/> naming the JSON path, what was expected and what was found.
/// </summary>
internal static class PlanJsonReader
{
    public static readonly string[] RootFields = ["version", "workspace", "personal-workspace", "switch_delay_ms", "desktops", "background", "notes", "aliases", "features", "bundle"];
    private static readonly string[] BackgroundFields = ["name", "window_process_name", "action", "window_wait_timeout_ms", "summary", "disabled"];
    public static readonly string[] DesktopFields = ["name", "launch", "captured_windows", "disabled", "feature"];
    private static readonly string[] LaunchFields =
    [
        "name", "type", "path", "args", "command", "command_at_logon", "cwd", "shell", "delay_ms", "placement", "window_title",
        "window_process_name", "window_wait_timeout_ms", "window_pid_file", "single_instance", "summary", "notes", "disabled", "feature",
    ];

    private static readonly string[] SnapshotFields = ["title", "pid", "process_name", "exe_path", "class_name", "screen_number", "inferred_placement", "bounds"];

    public static JsonObject Object(JsonNode? node, string path = "$") =>
        node as JsonObject ?? throw JsonProblem.Expected(path, "an object", JsonProblem.Describe(node));

    public static WorkspacePlan WorkspacePlan(JsonObject data)
    {
        Only(data, RootFields);
        return PlanSettings(data, List(data, "desktops", DesktopPlan));
    }

    // The root settings a plan and a bundle root share.
    public static WorkspacePlan PlanSettings(JsonObject data, IReadOnlyList<DesktopPlan> desktops) => new()
    {
        Version = Int(data, "version", Desktop.WorkspacePlan.CurrentVersion),
        Workspace = OptionalText(data, "workspace"),
        PersonalWorkspace = OptionalText(data, "personal-workspace"),
        SwitchDelayMs = Int(data, "switch_delay_ms", 650),
        Desktops = desktops,
        Background = List(data, "background", BackgroundItem),
        Notes = List(data, "notes", Text),
        Aliases = TextMap(data, "aliases"),
        Features = FeatureMap(data),
    };

    public static BackgroundApp BackgroundItem(JsonNode? node, string path)
    {
        var data = Object(node, path);
        Only(data, BackgroundFields);
        var action = OptionalText(data, "action") ?? BackgroundApp.Close;
        if (!BackgroundApp.Actions.Contains(action))
        {
            throw JsonProblem.Expected(PathOf(data, "action"), $"one of {string.Join(", ", BackgroundApp.Actions)}", $"\"{action}\"");
        }

        return new(RequiredText(data, "name"), RequiredText(data, "window_process_name"))
        {
            Action = action,
            WindowWaitTimeoutMs = OptionalInt(data, "window_wait_timeout_ms"),
            Summary = OptionalText(data, "summary"),
            Disabled = Bool(data, "disabled"),
        };
    }

    public static DesktopPlan DesktopPlan(JsonNode? node, string path)
    {
        var data = Object(node, path);
        Only(data, DesktopFields);
        return DesktopContents(data, RequiredText(data, "name"));
    }

    // A desktop's launch entries, windows, switch and feature; the name is the caller's, since a bundle may name it in the ref.
    public static DesktopPlan DesktopContents(JsonObject data, string name) =>
        new(name) { Launch = Launches(data), CapturedWindows = CapturedWindows(data), Disabled = Bool(data, "disabled"), Feature = OptionalText(data, "feature") };

    public static IReadOnlyList<LaunchEntry> Launches(JsonObject data) => List(data, "launch", LaunchItem);

    public static IReadOnlyList<WindowSnapshot> CapturedWindows(JsonObject data) => List(data, "captured_windows", WindowSnapshot);

    // A terminal entry with no shell opens in Windows Terminal running pwsh.
    public static LaunchEntry LaunchItem(JsonNode? node, string path)
    {
        var data = Object(node, path);
        Only(data, LaunchFields);
        var entry = LaunchEntry(data);
        return entry.Type == Desktop.LaunchEntry.Terminal && string.IsNullOrEmpty(entry.Shell)
            ? entry with { Shell = Desktop.LaunchEntry.DefaultTerminalShell }
            : entry;
    }

    private static LaunchEntry LaunchEntry(JsonObject data) => new(RequiredText(data, "name"))
    {
        Type = OptionalText(data, "type") ?? "app",
        Path = OptionalText(data, "path"),
        Args = List(data, "args", Text),
        Command = OptionalText(data, "command"),
        CommandAtLogon = OptionalText(data, "command_at_logon"),
        Cwd = OptionalText(data, "cwd"),
        Shell = OptionalText(data, "shell"),
        DelayMs = Int(data, "delay_ms", 900),
        Placement = Has(data, "placement") ? PlacementSpec(data, "placement") : null,
        WindowTitle = OptionalText(data, "window_title"),
        WindowProcessName = OptionalText(data, "window_process_name"),
        WindowWaitTimeoutMs = OptionalInt(data, "window_wait_timeout_ms"),
        WindowPidFile = OptionalText(data, "window_pid_file"),
        SingleInstance = Bool(data, "single_instance"),
        Summary = OptionalText(data, "summary"),
        Notes = OptionalText(data, "notes"),
        Disabled = Bool(data, "disabled"),
        Feature = OptionalText(data, "feature"),
    };

    // The placement under the key of the parent object.
    private static PlacementSpec PlacementSpec(JsonObject parent, string key)
    {
        var data = Object(parent[key], PathOf(parent, key));
        Only(data, ["screen", "layout", "subdivide"]);
        return new(RequiredInt(data, "screen"), PlacementLayout(data, "layout", required: true)!, PlacementLayout(data, "subdivide", required: false));
    }

    private static PlacementLayout? PlacementLayout(JsonObject parent, string key, bool required)
    {
        if (!Has(parent, key))
        {
            return required ? throw JsonProblem.Missing(parent, key, "an object") : null;
        }

        var data = Object(parent[key], PathOf(parent, key));
        Only(data, ["mode", "slot", "orientation", "min_width", "count"]);
        return new(RequiredText(data, "mode"), Int(data, "slot", 1), OptionalText(data, "orientation"), OptionalInt(data, "min_width"), OptionalInt(data, "count"));
    }

    public static WindowSnapshot WindowSnapshot(JsonNode? node, string path)
    {
        var data = Object(node, path);
        Only(data, SnapshotFields);
        return new WindowSnapshot(
            RequiredText(data, "title"),
            OptionalInt(data, "pid"),
            OptionalText(data, "process_name"),
            OptionalText(data, "exe_path"),
            OptionalText(data, "class_name"),
            OptionalInt(data, "screen_number"),
            Has(data, "inferred_placement") ? PlacementSpec(data, "inferred_placement") : null,
            Has(data, "bounds") ? Rect(data, "bounds") : null);
    }

    private static Rect Rect(JsonObject parent, string key)
    {
        var data = Object(parent[key], PathOf(parent, key));
        Only(data, ["left", "top", "width", "height"]);
        return new(RequiredInt(data, "left"), RequiredInt(data, "top"), RequiredInt(data, "width"), RequiredInt(data, "height"));
    }

    // A list field read item by item; absent is empty.
    public static IReadOnlyList<T> List<T>(JsonObject data, string key, Func<JsonNode?, string, T> read)
    {
        if (!Has(data, key))
        {
            return [];
        }

        var path = PathOf(data, key);
        var items = data[key] as JsonArray ?? throw JsonProblem.Expected(path, "a list", JsonProblem.Describe(data[key]));
        return [.. items.Select((item, index) => read(item, JsonProblem.Item(path, index)))];
    }

    public static OrderedDictionary<string, string> TextMap(JsonObject data, string key)
    {
        var map = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in Members(data, key))
        {
            map[name] = Text(value, JsonProblem.Child(PathOf(data, key), name));
        }

        return map;
    }

    public static OrderedDictionary<string, bool> FeatureMap(JsonObject data)
    {
        var map = new OrderedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (name, value) in Members(data, "features"))
        {
            map[name] = Flag(value, JsonProblem.Child(PathOf(data, "features"), name));
        }

        return map;
    }

    public static string RequiredText(JsonObject data, string key) =>
        Has(data, key) ? Text(data[key], PathOf(data, key)) : throw JsonProblem.Missing(data, key, "a string");

    public static string? OptionalText(JsonObject data, string key) => Has(data, key) ? Text(data[key], PathOf(data, key)) : null;

    public static int Int(JsonObject data, string key, int fallback) => Has(data, key) ? Int(data[key], PathOf(data, key)) : fallback;

    public static int RequiredInt(JsonObject data, string key) =>
        Has(data, key) ? Int(data[key], PathOf(data, key)) : throw JsonProblem.Missing(data, key, "a whole number");

    public static int? OptionalInt(JsonObject data, string key) => Has(data, key) ? Int(data[key], PathOf(data, key)) : null;

    public static bool Bool(JsonObject data, string key) => Has(data, key) && Flag(data[key], PathOf(data, key));

    /// <summary>The string at <paramref name="path"/>; any other JSON type, null included, is rejected.</summary>
    public static string Text(JsonNode? node, string path) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw JsonProblem.Expected(path, "a string", JsonProblem.Describe(node));

    private static int Int(JsonNode? node, string path) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var whole)
            ? whole
            : throw JsonProblem.Expected(path, "a whole number", JsonProblem.Describe(node));

    private static bool Flag(JsonNode? node, string path) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : throw JsonProblem.Expected(path, "true or false", JsonProblem.Describe(node));

    // A field of an object, absent or an object; the key's own members.
    private static JsonObject Members(JsonObject data, string key) =>
        Has(data, key) ? Object(data[key], PathOf(data, key)) : new JsonObject();

    // A key that is present, even when its value is JSON null (which every reader then rejects by type).
    public static bool Has(JsonObject data, string key) => data.ContainsKey(key);

    public static string PathOf(JsonObject data, string key) => JsonProblem.Child(data.GetPath(), key);

    public static void Only(JsonObject data, IReadOnlyCollection<string> known)
    {
        foreach (var key in data.Select(pair => pair.Key).Where(key => !known.Contains(key)))
        {
            throw JsonProblem.Expected(PathOf(data, key), $"a known field ({string.Join(", ", known)})", "an unknown field");
        }
    }
}

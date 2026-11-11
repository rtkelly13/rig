using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Policy;

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
    // Text fields are written only when they hold something, as Python's `if self.x:` did.
    public static void AddText(JsonObject data, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            data[key] = value;
        }
    }

    public static JsonObject Map<T>(IReadOnlyDictionary<string, T> values, Func<T, JsonNode?> write)
    {
        var data = new JsonObject();
        foreach (var (key, value) in values)
        {
            data[key] = write(value);
        }

        return data;
    }
}

/// <summary>
/// Reading a hand-edited plan leniently, as the Python loader's pydantic lax mode did: a number where text belongs
/// reads as its digits, <c>"900"</c> and <c>900.0</c> read as 900, <c>"yes"</c> and <c>1</c> read as true, an optional
/// field of the wrong shape reads as absent, and a list field holding anything but a list reads as empty. Anything that
/// still does not fit throws <see cref="InvalidPlanException"/>.
/// </summary>
internal static class PlanJsonReader
{
    public const string EntryNotObject = "Workspace plan entries must be JSON objects.";

    private static readonly HashSet<string> TrueWords = new(["1", "on", "t", "true", "y", "yes"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FalseWords = new(["0", "off", "f", "false", "n", "no"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FlagOnWords = new(["1", "on", "true", "yes"], StringComparer.OrdinalIgnoreCase);

    public static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw new InvalidPlanException(EntryNotObject);

    public static WorkspacePlan WorkspacePlan(JsonObject data) => new()
    {
        Version = Int(data, "version", Desktop.WorkspacePlan.CurrentVersion),
        Workspace = OptionalText(data, "workspace"),
        PersonalWorkspace = OptionalText(data, "personal-workspace"),
        SwitchDelayMs = Int(data, "switch_delay_ms", 650),
        Desktops = List(data, "desktops", DesktopPlan),
        Notes = List(data, "notes", node => Text(node, "notes")),
        Aliases = TextMap(data["aliases"]),
        Features = FeatureMap(data["features"]),
    };

    public static DesktopPlan DesktopPlan(JsonNode? node)
    {
        var data = Object(node);
        return new DesktopPlan(RequiredText(data, "name")) { Launch = Launches(data), CapturedWindows = CapturedWindows(data), Disabled = Bool(data, "disabled"), Feature = OptionalText(data, "feature") };
    }

    public static IReadOnlyList<LaunchEntry> Launches(JsonObject data) => List(data, "launch", LaunchItem);

    public static IReadOnlyList<WindowSnapshot> CapturedWindows(JsonObject data) => List(data, "captured_windows", WindowSnapshot);

    // A terminal entry with no shell opens in Windows Terminal running pwsh.
    public static LaunchEntry LaunchItem(JsonNode? node)
    {
        var data = Object(node);
        var entry = LaunchEntry(data);
        return IsString(data["type"], Desktop.LaunchEntry.Terminal) && !Truthy(data["shell"])
            ? entry with { Shell = Desktop.LaunchEntry.DefaultTerminalShell }
            : entry;
    }

    public static LaunchEntry LaunchEntry(JsonObject data) => new(RequiredText(data, "name"))
    {
        Type = data.ContainsKey("type") ? Text(data["type"], "type") : "app",
        Path = OptionalText(data, "path"),
        Args = List(data, "args", node => Text(node, "args")),
        Command = OptionalText(data, "command"),
        CommandAtLogon = OptionalText(data, "command_at_logon"),
        Cwd = OptionalText(data, "cwd"),
        Shell = OptionalText(data, "shell"),
        DelayMs = Int(data, "delay_ms", 900),
        Placement = data["placement"] is JsonObject placement ? PlacementSpec(placement) : null,
        WindowTitle = OptionalText(data, "window_title"),
        WindowProcessName = OptionalText(data, "window_process_name"),
        WindowWaitTimeoutMs = LenientInt(data["window_wait_timeout_ms"]),
        WindowPidFile = OptionalText(data, "window_pid_file"),
        SingleInstance = Bool(data, "single_instance"),
        Summary = OptionalText(data, "summary"),
        Notes = OptionalText(data, "notes"),
        Disabled = Bool(data, "disabled"),
        Feature = OptionalText(data, "feature"),
    };

    public static PlacementSpec PlacementSpec(JsonObject data) => new(
        RequiredInt(data, "screen"),
        PlacementLayout(data["layout"] as JsonObject ?? throw Missing("layout")),
        data["subdivide"] is JsonObject subdivide ? PlacementLayout(subdivide) : null);

    public static PlacementLayout PlacementLayout(JsonObject data) => new(
        RequiredText(data, "mode"),
        Int(data, "slot", 1),
        OptionalText(data, "orientation"),
        LenientInt(data["min_width"]),
        LenientInt(data["count"]));

    public static WindowSnapshot WindowSnapshot(JsonNode? node)
    {
        var data = Object(node);
        return new WindowSnapshot(
            RequiredText(data, "title"),
            LenientInt(data["pid"]),
            OptionalText(data, "process_name"),
            OptionalText(data, "exe_path"),
            OptionalText(data, "class_name"),
            LenientInt(data["screen_number"]),
            data["inferred_placement"] is JsonObject placement ? PlacementSpec(placement) : null,
            Rect(data["bounds"]));
    }

    // Bounds may be an object or a [left, top, width, height] list; anything unreadable is absent.
    public static Rect? Rect(JsonNode? node)
    {
        if (node is JsonArray { Count: 4 } list)
        {
            node = new JsonObject { ["left"] = list[0]?.DeepClone(), ["top"] = list[1]?.DeepClone(), ["width"] = list[2]?.DeepClone(), ["height"] = list[3]?.DeepClone() };
        }

        return node is JsonObject data
            && LenientInt(data["left"]) is { } left
            && LenientInt(data["top"]) is { } top
            && LenientInt(data["width"]) is { } width
            && LenientInt(data["height"]) is { } height
                ? new Rect(left, top, width, height)
                : null;
    }

    public static IReadOnlyList<T> List<T>(JsonObject data, string key, Func<JsonNode?, T> read) =>
        data[key] is JsonArray items ? [.. items.Select(read)] : [];

    public static OrderedDictionary<string, string> TextMap(JsonNode? node)
    {
        var map = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        if (node is JsonObject data)
        {
            foreach (var (key, value) in data)
            {
                map[key] = Text(value, key);
            }
        }

        return map;
    }

    // JSON booleans are the intended form; strings are accepted so a hand-edited plan saying "off" does not silently
    // read as enabled. Anything else counts by its truthiness.
    public static OrderedDictionary<string, bool> FeatureMap(JsonNode? node)
    {
        var map = new OrderedDictionary<string, bool>(StringComparer.Ordinal);
        if (node is JsonObject data)
        {
            foreach (var (key, value) in data)
            {
                map[key] = value is JsonValue text && text.GetValueKind() == JsonValueKind.String
                    ? FlagOnWords.Contains(text.GetValue<string>().Trim())
                    : Truthy(value);
            }
        }

        return map;
    }

    public static string RequiredText(JsonObject data, string key) =>
        data.ContainsKey(key) ? Text(data[key], key) : throw Missing(key);

    public static string? OptionalText(JsonObject data, string key) => data[key] is null ? null : Text(data[key], key);

    // Plans are hand-edited, so a number or boolean where text belongs is read as Python's str() of it.
    public static string Text(JsonNode? node, string key) => node is JsonValue value
        ? value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Number => NumberText(value),
            _ => throw Invalid(key, "text"),
        }
        : throw Invalid(key, "text");

    // Python's str() of a parsed JSON number: integers as written, fractions as the float's repr.
    public static string NumberText(JsonValue value)
    {
        var raw = value.ToJsonString();
        return raw.IndexOfAny(['.', 'e', 'E']) >= 0
            ? PyJson.FloatRepr(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture))
            : raw;
    }

    public static int Int(JsonObject data, string key, int fallback) =>
        !data.ContainsKey(key) ? fallback : LaxInt(data[key]) ?? throw Invalid(key, "a whole number");

    public static int RequiredInt(JsonObject data, string key) =>
        data.ContainsKey(key) ? LaxInt(data[key]) ?? throw Invalid(key, "a whole number") : throw Missing(key);

    // An optional number of the wrong shape reads as absent.
    public static int? LenientInt(JsonNode? node) => LaxInt(node);

    public static bool Bool(JsonObject data, string key) =>
        data.ContainsKey(key) ? LaxBool(data[key]) ?? throw Invalid(key, "true or false") : false;

    // A truthy value, as Python's bool() reads it: null, false, zero, "" and empty containers are false.
    public static bool Truthy(JsonNode? node) => node switch
    {
        JsonArray list => list.Count > 0,
        JsonObject data => data.Count > 0,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => value.GetValue<double>() != 0,
            _ => false,
        },
        _ => false,
    };

    public static bool IsString(JsonNode? node, string expected) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() == expected;

    // pydantic's lax int: a whole number, a whole float, a boolean, or a string holding one of the numbers.
    private static int? LaxInt(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        switch (value.GetValueKind())
        {
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            case JsonValueKind.Number:
                return WholeNumber(value.GetValue<double>());
            case JsonValueKind.String:
                var text = value.GetValue<string>().Trim();
                if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                {
                    return whole;
                }

                return text.Contains('.', StringComparison.Ordinal)
                    && double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var fraction)
                        ? WholeNumber(fraction)
                        : null;
            default:
                return null;
        }
    }

    private static int? WholeNumber(double number) =>
        number == Math.Floor(number) && number is >= int.MinValue and <= int.MaxValue ? (int)number : null;

    // pydantic's lax bool: true/false, 0/1, and the strings yes/no, on/off, true/false, t/f, y/n, 1/0 in any case.
    private static bool? LaxBool(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetValue<double>() switch
            {
                0 => false,
                1 => true,
                _ => null,
            },
            JsonValueKind.String when TrueWords.Contains(value.GetValue<string>()) => true,
            JsonValueKind.String when FalseWords.Contains(value.GetValue<string>()) => false,
            _ => null,
        };
    }

    private static InvalidPlanException Missing(string key) => new($"Workspace plan field '{key}' is required.");

    private static InvalidPlanException Invalid(string key, string expected) => new($"Workspace plan field '{key}' must be {expected}.");
}

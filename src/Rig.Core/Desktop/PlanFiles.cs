using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Policy;

namespace Rig.Core.Desktop;

/// <summary>
/// Reading and writing workspace plan files: a single JSON plan, or a bundle root that lists one file per desktop.
/// Ported from <c>desktop_manager.plan</c> (<c>parse_plan_json</c>, <c>save_plan</c>, <c>save_plan_bundle</c>).
/// </summary>
public static partial class PlanFiles
{
    /// <summary>The plan in the file at <paramref name="path"/>, with a bundle's desktop files read from beside it.</summary>
    public static WorkspacePlan Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllText(path, Encoding.UTF8), path, Path.GetDirectoryName(Path.GetFullPath(path)));
    }

    /// <summary>
    /// The plan <paramref name="text"/> holds. A bundle root (one with a <c>bundle</c> key) reads its desktop files
    /// relative to <paramref name="baseDirectory"/>; <paramref name="source"/> names the plan in errors. Each
    /// <c>columns</c> layout gets the count the plan as written asks for (<see cref="Placement.FitColumns"/> with no
    /// screen widths).
    /// </summary>
    public static WorkspacePlan Parse(string text, string source = "<memory>", string? baseDirectory = null)
    {
        var data = PlanJsonReader.Object(ParseJson(text, source));
        var plan = data.ContainsKey("bundle") ? ResolveBundle(data, source, baseDirectory) : PlanJsonReader.WorkspacePlan(data);
        return Placement.FitColumns(plan, new Dictionary<int, int>());
    }

    /// <summary>The desktop a single desktop file holds, as a bundle lists them.</summary>
    public static DesktopPlan ParseDesktop(string text, string source = "<memory>") =>
        PlanJsonReader.DesktopPlan(ParseJson(text, source));

    /// <summary>Writes <paramref name="plan"/> to <paramref name="path"/> as one JSON file.</summary>
    public static void Save(WorkspacePlan plan, string path)
    {
        ArgumentNullException.ThrowIfNull(plan);
        WriteJson(path, plan.ToJson());
    }

    /// <summary>
    /// Writes <paramref name="plan"/> as a bundle: a root file at <paramref name="path"/> and one file per desktop in
    /// <paramref name="desktopDirectoryName"/> beside it, named from the desktop's name.
    /// </summary>
    public static void SaveBundle(WorkspacePlan plan, string path, string desktopDirectoryName = "desktops")
    {
        ArgumentNullException.ThrowIfNull(plan);
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var desktopDirectory = Path.Join(root, desktopDirectoryName);
        _ = Directory.CreateDirectory(desktopDirectory);

        var refs = new List<JsonNode>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < plan.Desktops.Count; index++)
        {
            var desktop = plan.Desktops[index];
            var slug = Slugify(desktop.Name);
            var fileName = Dedupe(slug.Length > 0 ? slug : $"desktop-{index + 1}", used);
            var desktopPath = Path.Join(desktopDirectory, fileName + ".json");
            WriteJson(desktopPath, desktop.ToJson());
            refs.Add(desktop.BundleRef(Path.GetRelativePath(root, desktopPath).Replace('\\', '/')));
        }

        WriteJson(path, plan.BundleRoot(refs));
    }

    /// <summary>The text <see cref="Save"/> writes for <paramref name="json"/>: two-space indented, ASCII-escaped, with a closing newline.</summary>
    public static string Format(JsonNode json) => PyJson.Dumps(json) + "\n";

    /// <summary>A desktop name as a file name: runs of anything but letters and digits become one hyphen, lowercased.</summary>
    public static string Slugify(string value) => NotAlphanumeric().Replace(value, "-").Trim('-').ToLowerInvariant();

    private static string Dedupe(string baseName, HashSet<string> used)
    {
        var candidate = baseName;
        for (var suffix = 2; !used.Add(candidate); suffix++)
        {
            candidate = $"{baseName}-{suffix}";
        }

        return candidate;
    }

    private static void WriteJson(string path, JsonNode json)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Format(json), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static JsonNode? ParseJson(string text, string source)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException e)
        {
            throw new InvalidPlanException($"Workspace plan {source} is not valid JSON: {e.Message}", e);
        }
    }

    // A plan whose desktops live in separate files: {"bundle": {"desktops": [{"path": ...}], "aliases": {...}}, ...}.
    private static WorkspacePlan ResolveBundle(JsonObject data, string source, string? baseDirectory)
    {
        if (data["bundle"] is not JsonObject body
            || (body.ContainsKey("aliases") && body["aliases"] is not JsonObject))
        {
            throw new InvalidPlanException(PlanJsonReader.EntryNotObject);
        }

        if (body.ContainsKey("desktops") && body["desktops"] is not JsonArray)
        {
            throw new InvalidPlanException($"Bundle desktops must be a list in {source}");
        }

        var loaded = new List<(JsonObject Ref, string Path, JsonObject Data)>();
        foreach (var item in body["desktops"] as JsonArray ?? [])
        {
            var reference = PlanJsonReader.Object(item);
            var relative = reference["path"] is JsonValue text && text.GetValueKind() == JsonValueKind.String ? text.GetValue<string>() : null;
            if (string.IsNullOrWhiteSpace(relative))
            {
                throw new InvalidPlanException($"Bundle desktop entries must define a relative path in {source}");
            }

            if (baseDirectory is null)
            {
                throw new InvalidPlanException($"Bundle desktop entries in {source} require a filesystem base directory");
            }

            var desktopPath = Path.GetFullPath(Path.Join(baseDirectory, relative));
            var desktopData = PlanJsonReader.Object(ParseJson(File.ReadAllText(desktopPath, Encoding.UTF8), desktopPath));
            loaded.Add((reference, desktopPath, desktopData));
        }

        var desktops = loaded.Select(desktop => BundledDesktop(desktop.Ref, desktop.Path, desktop.Data, source)).ToList();
        var aliases = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in new[] { data["aliases"] as JsonObject, body["aliases"] as JsonObject })
        {
            foreach (var (key, value) in layer ?? [])
            {
                aliases[key] = PyStr(value);
            }
        }

        // The bundle root's own settings; its inline desktops count only when the bundle lists none.
        return new WorkspacePlan
        {
            Version = PlanJsonReader.Int(data, "version", WorkspacePlan.CurrentVersion),
            Workspace = PlanJsonReader.OptionalText(data, "workspace"),
            PersonalWorkspace = PlanJsonReader.OptionalText(data, "personal-workspace"),
            SwitchDelayMs = PlanJsonReader.Int(data, "switch_delay_ms", 650),
            Desktops = desktops.Count > 0 ? desktops : PlanJsonReader.List(data, "desktops", PlanJsonReader.DesktopPlan),
            Notes = PlanJsonReader.List(data, "notes", node => PlanJsonReader.Text(node, "notes")),
            Aliases = aliases,
            Features = PlanJsonReader.FeatureMap(data["features"]),
        };
    }

    // The desktop file as the plan sees it. The ref is where a desktop is switched on or off, so the bundle file alone
    // shows the shape of the workspace without opening every desktop file.
    private static DesktopPlan BundledDesktop(JsonObject reference, string path, JsonObject data, string source)
    {
        var fileName = PlanJsonReader.OptionalText(data, "name");
        var file = new DesktopPlan(fileName ?? string.Empty)
        {
            Launch = PlanJsonReader.Launches(data),
            CapturedWindows = PlanJsonReader.CapturedWindows(data),
            Disabled = PlanJsonReader.Bool(data, "disabled"),
            Feature = PlanJsonReader.OptionalText(data, "feature"),
        };
        var name = PlanJsonReader.OptionalText(reference, "name") ?? fileName
            ?? throw new InvalidPlanException($"Bundle desktop {path.Replace('\\', '/')} in {source} must define a name");
        return file with
        {
            Name = name,
            Disabled = reference["disabled"] is { } disabled ? PlanJsonReader.Truthy(disabled) : file.Disabled,
            Feature = PlanJsonReader.OptionalText(reference, "feature") ?? file.Feature,
        };
    }

    // Python's str() of a bundle alias value: text as itself, null as "None", numbers and booleans as Python prints them.
    private static string PyStr(JsonNode? node) => node switch
    {
        null => "None",
        JsonValue value => PlanJsonReader.Text(value, "aliases"),
        _ => node.ToJsonString(),
    };

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NotAlphanumeric();
}

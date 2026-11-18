using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Text;

namespace Rig.Core.Desktop;

/// <summary>
/// Reading and writing workspace plan files: a single JSON plan, or a bundle root that lists one file per desktop.
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
    public static WorkspacePlan Parse(string text, string source = "<memory>", string? baseDirectory = null) =>
        JsonProblem.Run(source, () =>
        {
            var data = PlanJsonReader.Object(JsonProblem.Parse(source, text));
            var plan = data.ContainsKey("bundle") ? ResolveBundle(data, source, baseDirectory) : PlanJsonReader.WorkspacePlan(data);
            return Placement.FitColumns(plan, new Dictionary<int, int>());
        });

    /// <summary>The desktop a single desktop file holds, as a bundle lists them.</summary>
    public static DesktopPlan ParseDesktop(string text, string source = "<memory>") =>
        JsonProblem.Run(source, () => PlanJsonReader.DesktopPlan(JsonProblem.Parse(source, text), "$"));

    /// <summary>One launch entry from a standalone JSON file, as <c>workspace launch-file</c> reads it.</summary>
    /// <exception cref="JsonInputException">The text is not a JSON object that holds a launch entry.</exception>
    public static LaunchEntry ParseLaunchEntry(string text, string source = "<memory>") =>
        JsonProblem.Run(source, () => PlanJsonReader.LaunchItem(JsonProblem.Parse(source, text), "$"));

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
    public static string Format(JsonNode json) => JsonOutput.Format(json) + "\n";

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

    // A plan whose desktops live in separate files: {"bundle": {"desktops": [{"path": ...}], "aliases": {...}}, ...}.
    private static WorkspacePlan ResolveBundle(JsonObject data, string source, string? baseDirectory)
    {
        PlanJsonReader.Only(data, PlanJsonReader.RootFields);
        var body = PlanJsonReader.Object(data["bundle"], PlanJsonReader.PathOf(data, "bundle"));
        PlanJsonReader.Only(body, ["desktops", "aliases"]);
        var refs = PlanJsonReader.List(body, "desktops", (node, path) => PlanJsonReader.Object(node, path));
        var desktops = refs.Select(reference => BundledDesktop(reference, source, baseDirectory)).ToList();
        var aliases = PlanJsonReader.TextMap(data, "aliases");
        foreach (var (key, value) in PlanJsonReader.TextMap(body, "aliases"))
        {
            aliases[key] = value;
        }

        // The bundle root's own settings; its inline desktops count only when the bundle lists none.
        return PlanJsonReader.PlanSettings(data, desktops.Count > 0 ? desktops : PlanJsonReader.List(data, "desktops", PlanJsonReader.DesktopPlan)) with { Aliases = aliases };
    }

    // The desktop file a bundle ref points at, as the plan sees it. The ref is where a desktop is switched on or off, so the
    // bundle file alone shows the shape of the workspace without opening every desktop file.
    private static DesktopPlan BundledDesktop(JsonObject reference, string source, string? baseDirectory)
    {
        PlanJsonReader.Only(reference, ["path", "name", "feature", "disabled"]);
        var relative = PlanJsonReader.RequiredText(reference, "path");
        if (string.IsNullOrWhiteSpace(relative))
        {
            throw JsonProblem.Expected(PlanJsonReader.PathOf(reference, "path"), "a relative path", $"the string \"{relative}\"");
        }

        if (baseDirectory is null)
        {
            throw new InvalidPlanException($"Bundle desktop entries in {source} require a filesystem base directory");
        }

        var refName = PlanJsonReader.OptionalText(reference, "name");
        var refFeature = PlanJsonReader.OptionalText(reference, "feature");
        bool? refDisabled = reference.ContainsKey("disabled") ? PlanJsonReader.Bool(reference, "disabled") : null;
        var desktopPath = Path.GetFullPath(Path.Join(baseDirectory, relative));
        var file = JsonProblem.Run(desktopPath, () =>
        {
            var data = PlanJsonReader.Object(JsonProblem.Parse(desktopPath, File.ReadAllText(desktopPath, Encoding.UTF8)));
            PlanJsonReader.Only(data, PlanJsonReader.DesktopFields);
            return (Plan: PlanJsonReader.DesktopContents(data, string.Empty), Name: PlanJsonReader.OptionalText(data, "name"));
        });
        var name = refName ?? file.Name
            ?? throw new InvalidPlanException($"Bundle desktop {desktopPath.Replace('\\', '/')} in {source} must define a name");
        return file.Plan with
        {
            Name = name,
            Disabled = refDisabled ?? file.Plan.Disabled,
            Feature = refFeature ?? file.Plan.Feature,
        };
    }

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NotAlphanumeric();
}

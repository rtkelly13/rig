using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rig.Core.Policy;

/// <summary>
/// What <c>policy audit</c> reads: the commands an estate's manifests declare, and how often known command families
/// appear in shell history, counted without returning a line of it. Ported from <c>rig.policy.engine</c>.
/// </summary>
public static partial class ProjectAudit
{
    private static readonly string[] ExternalWords = ["publish", "deploy", "destroy", "push", "rm ", "reset", "clean"];

    private static readonly string[] LocalWords =
        ["format", "--write", "--fix", "update", "prepare", "build", "coverage", "snapshot", "watch", "e2e", "live", " dev", " start", " serve"];

    private static readonly string[] SafeWords = ["test", "lint", "check", "typecheck", "status", "help", "validate"];

    private static readonly (string Family, Regex Pattern)[] Families =
    [
        ("uv validation", UvValidation()),
        ("rig validation", RigValidation()),
        ("pnpm validation", PnpmValidation()),
        ("git inspection", GitInspection()),
        ("workspace inspection", WorkspaceInspection()),
    ];

    /// <summary>
    /// The <c>package.json</c> scripts (run as <c>pnpm name</c>) and <c>[project.scripts]</c> entry points (run as
    /// <c>uv run name</c>) of <paramref name="root"/> and each of its non-hidden child folders, by project then command.
    /// </summary>
    public static IReadOnlyList<ProjectCommand> Commands(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var projects = new[] { root }.Concat(
            new DirectoryInfo(root).EnumerateDirectories().Where(d => !d.Name.StartsWith('.')).Select(d => d.FullName).Order(StringComparer.Ordinal));
        var results = new List<ProjectCommand>();
        foreach (var project in projects)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(project));
            var package = Path.Join(project, "package.json");
            if (File.Exists(package))
            {
                results.AddRange(PackageScripts(File.ReadAllText(package))
                    .Select(s => new ProjectCommand(name, package, s.Name, $"pnpm {s.Name}", Risk(s.Name, s.Source), s.Source)));
            }

            var pyproject = Path.Join(project, "pyproject.toml");
            if (File.Exists(pyproject))
            {
                results.AddRange(ProjectScripts(File.ReadAllText(pyproject))
                    .Select(s => new ProjectCommand(name, pyproject, s.Name, $"uv run {s.Name}", Risk(s.Name, s.Source), s.Source)));
            }
        }

        return [.. results.OrderBy(c => c.Project, StringComparer.Ordinal).ThenBy(c => c.Invocation, StringComparer.Ordinal)];
    }

    /// <summary>The tier a script's name and command suggest: anything that publishes or deletes is external.</summary>
    public static CommandRisk Risk(string name, string source)
    {
        var text = $"{name} {source}".ToLowerInvariant();
        bool Any(string[] words) => words.Any(w => text.Contains(w, StringComparison.Ordinal));
        return Any(ExternalWords) ? CommandRisk.External
            : Any(LocalWords) ? CommandRisk.Local
            : Any(SafeWords) ? CommandRisk.Safe
            : CommandRisk.Local;
    }

    /// <summary>
    /// How many lines of the history at <paramref name="path"/> fall in each known family (a line counts once, in the
    /// first family it matches), most common first. Empty when there is no history.
    /// </summary>
    public static IReadOnlyList<(string Family, int Count)> History(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var counts = new List<(string Family, int Count)>();
        foreach (var line in LineBreak().Split(File.ReadAllText(path, Encoding.UTF8)))
        {
            if (Families.FirstOrDefault(f => f.Pattern.IsMatch(line)).Family is { } family)
            {
                var at = counts.FindIndex(c => c.Family == family);
                if (at < 0)
                {
                    counts.Add((family, 1));
                }
                else
                {
                    counts[at] = (family, counts[at].Count + 1);
                }
            }
        }

        return [.. counts.OrderByDescending(c => c.Count)];
    }

    // The scripts whose command is text; a malformed file throws, as Python's json.loads did.
    private static List<(string Name, string Source)> PackageScripts(string json) =>
        JsonNode.Parse(json) is JsonObject { } package && package["scripts"] is JsonObject scripts
            ? scripts.Where(s => s.Value is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                .Select(s => (s.Key, s.Value!.GetValue<string>())).ToList()
            : [];

    /// <summary>
    /// The text-valued keys of a <c>pyproject.toml</c>'s <c>[project.scripts]</c> table. Only that table's
    /// <c>name = "target"</c> lines are read; rig has no TOML parser, and entry points need nothing more.
    /// </summary>
    public static IReadOnlyList<(string Name, string Source)> ProjectScripts(string toml)
    {
        ArgumentNullException.ThrowIfNull(toml);
        var scripts = new List<(string, string)>();
        var inTable = false;
        foreach (var raw in toml.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inTable = TableHeader().Match(line) is { Success: true } header
                    && header.Groups["name"].Value.Replace(" ", string.Empty, StringComparison.Ordinal) == "project.scripts";
                continue;
            }

            if (inTable && ScriptLine().Match(line) is { Success: true } entry)
            {
                var key = entry.Groups["bare"].Success ? entry.Groups["bare"].Value : Unquote(entry.Groups["key"].Value);
                scripts.Add((key, Unquote(entry.Groups["value"].Value)));
            }
        }

        return scripts;
    }

    // A TOML basic ("...") or literal ('...') string's value.
    private static string Unquote(string quoted) =>
        quoted[0] == '\'' ? quoted[1..^1] : Regex.Unescape(quoted[1..^1]);

    [GeneratedRegex(@"^\[\s*(?<name>[A-Za-z0-9_.\- ]+?)\s*\](\s*#.*)?$")]
    private static partial Regex TableHeader();

    [GeneratedRegex("""^(?:(?<bare>[A-Za-z0-9_\-]+)|(?<key>"(?:[^"\\]|\\.)*"|'[^']*'))\s*=\s*(?<value>"(?:[^"\\]|\\.)*"|'[^']*')\s*(#.*)?$""")]
    private static partial Regex ScriptLine();

    // str.splitlines: every line boundary Python recognises.
    [GeneratedRegex(@"\r\n|[\n\r\v\f\x1c\x1d\x1e\x85\u2028\u2029]")]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"\buv run (help|status|pytest|ruff|pyright|check)\b")]
    private static partial Regex UvValidation();

    [GeneratedRegex(@"(?<![\w-])rig (help|status)\b")]
    private static partial Regex RigValidation();

    [GeneratedRegex(@"\bpnpm (test|typecheck|lint|build|check|ci)\b")]
    private static partial Regex PnpmValidation();

    [GeneratedRegex(@"\bgit (status|diff|log|show|branch)\b")]
    private static partial Regex GitInspection();

    [GeneratedRegex(@"\bworkspace (skills|status|audit|projects)\b")]
    private static partial Regex WorkspaceInspection();
}

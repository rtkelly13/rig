using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Workspace;

/// <summary>
/// This machine's <c>~/.workspace/config.json</c>: settings that differ between machines. A missing or malformed
/// file, or a setting of the wrong type, reads as unset. Ported from <c>rig.workspace_config</c>.
/// </summary>
public static class MachineConfig
{
    /// <summary>
    /// <c>tools.estate.skip_repos</c> in <paramref name="path"/>: the managed-project names this machine never clones or
    /// registers, while other machines still get them. Blank names are dropped.
    /// </summary>
    public static IReadOnlySet<string> SkipRepos(string path) => new HashSet<string>(EstateNames(path, "skip_repos"), StringComparer.Ordinal);

    /// <summary>
    /// <c>tools.estate.work_dirs</c> in <paramref name="path"/>: this machine's work folders. A name resolves under
    /// <paramref name="codeRoot"/>; an absolute path, or one starting <c>~</c>, stands as written.
    /// </summary>
    /// <param name="path">The machine config file.</param>
    /// <param name="codeRoot">The code root a bare name sits in.</param>
    /// <param name="home">What a leading <c>~</c> stands for.</param>
    public static IReadOnlyList<string> WorkDirs(string path, string codeRoot, string home) =>
        [.. EstateNames(path, "work_dirs").Select(entry => Path.Combine(codeRoot, ExpandHome(entry, home)))];

    private static string ExpandHome(string entry, string home) =>
        entry == "~" ? home
        : entry.StartsWith("~/", StringComparison.Ordinal) || entry.StartsWith("~\\", StringComparison.Ordinal) ? Path.Join(home, entry[2..])
        : entry;

    // A string list under tools.estate, trimmed with blanks dropped; empty when the file, the key or any item is not as expected.
    private static IEnumerable<string> EstateNames(string path, string key)
    {
        ArgumentNullException.ThrowIfNull(path);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }

        return (root as JsonObject)?["tools"] is JsonObject tools
            && tools["estate"] is JsonObject estate
            && estate[key] is JsonArray list
            && list.All(item => item?.GetValueKind() == JsonValueKind.String)
                ? [.. list.Select(item => item!.GetValue<string>().Trim()).Where(name => name.Length > 0)]
                : [];
    }
}

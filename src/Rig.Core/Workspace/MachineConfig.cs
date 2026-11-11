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
    public static IReadOnlySet<string> SkipRepos(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var names = (root as JsonObject)?["tools"] is JsonObject tools
            && tools["estate"] is JsonObject estate
            && estate["skip_repos"] is JsonArray list
            && list.All(item => item?.GetValueKind() == JsonValueKind.String)
                ? list.Select(item => item!.GetValue<string>().Trim()).Where(name => name.Length > 0)
                : [];
        return new HashSet<string>(names, StringComparer.Ordinal);
    }
}

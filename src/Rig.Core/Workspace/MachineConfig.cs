using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Workspace;

/// <summary>What this machine is for, from <c>tools.machine.role</c> in <c>~/.workspace/config.json</c>.</summary>
public enum MachineRole
{
    /// <summary>A machine for the personal estate; the default when no role is set.</summary>
    Personal,

    /// <summary>A machine for work: scheduled tooling such as the daily orchestrator looks after the work orgs.</summary>
    Work,
}

/// <summary>
/// This machine's <c>~/.workspace/config.json</c>: settings that differ between machines. A missing or malformed
/// file, or a setting of the wrong type, reads as unset. Ported from <c>rig.workspace_config</c>.
/// </summary>
public static class MachineConfig
{
    /// <summary>Where the role is set, as shown in messages.</summary>
    public const string RoleKey = "tools.machine.role";

    /// <summary>Where the work orgs are listed, as shown in messages.</summary>
    public const string WorkOrgsKey = "tools.estate.work_orgs";

    /// <summary>The key mapping Orca instance names to user-data folders.</summary>
    public const string OrcaInstancesKey = "tools.orca.instances";

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

    /// <summary>
    /// <c>tools.estate.work_orgs</c> in <paramref name="path"/>: the GitHub owners whose repositories are this machine's
    /// work, as written. They are kept here, not in the tracked config, because rig is public. Blank names are dropped.
    /// </summary>
    public static IReadOnlyList<string> WorkOrgs(string path) => [.. EstateNames(path, "work_orgs")];

    /// <summary>
    /// <c>tools.machine.role</c> in <paramref name="path"/> (<c>personal</c> or <c>work</c>, any case); null when it is
    /// unset or names no role, so a caller can say the default is in use.
    /// </summary>
    public static MachineRole? Role(string path) =>
        Section(path, "machine")?["role"] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? ParseRole(value.GetValue<string>())
            : null;

    /// <summary>
    /// The user-data folder <c>tools.orca.instances.&lt;name&gt;</c> maps <paramref name="name"/> to in <paramref name="path"/>,
    /// as written (unexpanded); null when unmapped, which means the default Orca instance.
    /// </summary>
    public static string? OrcaInstance(string path, string name) =>
        Section(path, "orca")?["instances"] is JsonObject instances
        && instances[name] is JsonValue value
        && value.GetValueKind() == JsonValueKind.String
        && value.GetValue<string>() is { Length: > 0 } folder
            ? folder
            : null;

    /// <summary>The role <paramref name="name"/> names (<c>personal</c> or <c>work</c>, any case), or null.</summary>
    public static MachineRole? ParseRole(string name) =>
        (name ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "PERSONAL" => MachineRole.Personal,
            "WORK" => MachineRole.Work,
            _ => null,
        };

    /// <summary>How config files and reports write <paramref name="role"/>: <c>personal</c> or <c>work</c>.</summary>
    public static string RoleName(MachineRole role) => role == MachineRole.Work ? "work" : "personal";

    private static JsonObject? Section(string path, string name) =>
        (Read(path) as JsonObject)?["tools"] is JsonObject tools ? tools[name] as JsonObject : null;

    /// <summary>
    /// <c>tools.desktop.aliases</c> in <paramref name="path"/>: this machine's desktop plan aliases, applied over the
    /// plan's own. Names are trimmed and blank ones dropped; empty when any value is not text.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DesktopAliases(string path)
    {
        var aliases = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        if (Read(path) is JsonObject root
            && root["tools"] is JsonObject tools
            && tools["desktop"] is JsonObject desktop
            && desktop["aliases"] is JsonObject map
            && map.All(item => item.Value?.GetValueKind() == JsonValueKind.String))
        {
            foreach (var (name, value) in map)
            {
                if (name.Trim() is { Length: > 0 } trimmed)
                {
                    aliases[trimmed] = value!.GetValue<string>();
                }
            }
        }

        return aliases;
    }

    private static JsonNode? Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string ExpandHome(string entry, string home) =>
        entry == "~" ? home
        : entry.StartsWith("~/", StringComparison.Ordinal) || entry.StartsWith("~\\", StringComparison.Ordinal) ? Path.Join(home, entry[2..])
        : entry;

    // A string list under tools.estate, trimmed with blanks dropped; empty when the file, the key or any item is not as expected.
    private static IEnumerable<string> EstateNames(string path, string key)
    {
        return (Read(path) as JsonObject)?["tools"] is JsonObject tools
            && tools["estate"] is JsonObject estate
            && estate[key] is JsonArray list
            && list.All(item => item?.GetValueKind() == JsonValueKind.String)
                ? [.. list.Select(item => item!.GetValue<string>().Trim()).Where(name => name.Length > 0)]
                : [];
    }
}

using System.Text.Json.Nodes;

namespace Rig.Core.Orca;

/// <summary>What <c>config/orca.json</c> declares for one repo. Fields left null are not checked.</summary>
/// <param name="Path">Where its clone is, written <c>~/code/&lt;group&gt;/&lt;name&gt;</c>; null for the estate root's.</param>
/// <param name="Setup">The setup script its <c>orca.yaml</c> should declare.</param>
/// <param name="Archive">The archive script its <c>orca.yaml</c> should declare.</param>
/// <param name="ExternalWorktreeVisibility"><c>show</c>, <c>hide</c> or <c>inherit</c>.</param>
/// <param name="BaseRef">The base ref for new worktrees.</param>
/// <param name="WorktreeBasePath">Orca's per-repo worktree location; <c>""</c> is the global workspace folder.</param>
/// <param name="Color">The hex badge colour.</param>
/// <param name="Icon">A lucide name from Orca's picker, <c>emoji:…</c>, or an image URL.</param>
public sealed record RepoPolicy(
    string? Path = null,
    string? Setup = null,
    string? Archive = null,
    string? ExternalWorktreeVisibility = null,
    string? BaseRef = null,
    string? WorktreeBasePath = null,
    string? Color = null,
    string? Icon = null);

/// <summary>Orca's experimental Agent sleep: stop idle agent terminals, resume them when opened.</summary>
/// <param name="Enabled">Whether it is on.</param>
/// <param name="IdleMinutes">The idle window; Orca accepts 1 minute to 24 hours.</param>
public sealed record AgentSleepPolicy(bool Enabled, int IdleMinutes = AgentSleepPolicy.DefaultMinutes)
{
    /// <summary>Orca's default idle window, used for a missing or out-of-range value.</summary>
    public const int DefaultMinutes = 30;
}

/// <summary>The permission mode Orca should launch each agent with; agents not named take <see cref="Default"/>.</summary>
/// <param name="Default">The mode for agents <see cref="Agents"/> leaves out.</param>
/// <param name="Agents">Each named agent's mode.</param>
public sealed record AgentPermissionsPolicy(string Default, IReadOnlyDictionary<string, string> Agents)
{
    /// <summary>The mode <paramref name="agent"/> should launch in.</summary>
    public string Wanted(string agent) => Agents.GetValueOrDefault(agent, Default);
}

/// <summary>What <c>config/orca.json</c> declares. Agents and repos it leaves out are not checked.</summary>
/// <param name="AgentArgs">Each agent's custom launch arguments.</param>
/// <param name="AgentPermissions">Each agent's permission mode, or null to check none.</param>
/// <param name="WorkspaceDir">Where Orca creates worktrees (<c>~</c> expands).</param>
/// <param name="AgentSleep">Agent sleep, or null to leave it unchecked.</param>
/// <param name="Repos">Each repo's settings, by Orca display name.</param>
public sealed record OrcaPolicy(
    IReadOnlyDictionary<string, string> AgentArgs,
    AgentPermissionsPolicy? AgentPermissions,
    string? WorkspaceDir,
    AgentSleepPolicy? AgentSleep,
    IReadOnlyDictionary<string, RepoPolicy> Repos)
{
    /// <summary>The policy file's path under a rig checkout.</summary>
    public static readonly string RelativePath = System.IO.Path.Join("config", "orca.json");

    /// <summary>A policy that declares nothing.</summary>
    public static readonly OrcaPolicy Empty = new(new Dictionary<string, string>(), null, null, null, new Dictionary<string, RepoPolicy>());

    private static readonly string[] Visibilities = ["show", "hide", "inherit"];

    /// <summary>Reads the policy file at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">The file is malformed.</exception>
    public static OrcaPolicy Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Decodes a policy document.</summary>
    /// <exception cref="InvalidDataException">It is malformed: not JSON, a field of the wrong type, or an unknown mode.</exception>
    public static OrcaPolicy Parse(string text)
    {
        var root = OrcaJson.Object(text);
        var repos = new SortedDictionary<string, RepoPolicy>(StringComparer.Ordinal);
        foreach (var (name, _) in OrcaJson.Object(root, "repos") ?? [])
        {
            repos[name] = Repo(OrcaJson.Object(OrcaJson.Object(root, "repos")!, name) ?? throw new InvalidDataException($"repos.{name}: expected an object"));
        }

        return new(
            OrcaJson.StringMap(root, "agent_args") ?? [],
            OrcaJson.Object(root, "agent_permissions") is { } permissions ? Permissions(permissions) : null,
            OrcaJson.String(root, "workspace_dir"),
            OrcaJson.Object(root, "agent_sleep") is { } sleep ? Sleep(sleep) : null,
            repos);
    }

    private static RepoPolicy Repo(JsonObject repo) => new(
        OrcaJson.String(repo, "path"),
        OrcaJson.String(repo, "setup"),
        OrcaJson.String(repo, "archive"),
        OrcaJson.OneOf(repo, "external_worktree_visibility", Visibilities),
        OrcaJson.String(repo, "base_ref"),
        OrcaJson.String(repo, "worktree_base_path"),
        OrcaJson.String(repo, "color"),
        OrcaJson.String(repo, "icon"));

    private static AgentPermissionsPolicy Permissions(JsonObject permissions)
    {
        var agents = OrcaJson.StringMap(permissions, "agents") ?? [];
        foreach (var (agent, mode) in agents)
        {
            Mode($"agents.{agent}", mode);
        }

        return new(Mode("default", OrcaJson.String(permissions, "default") ?? "own"), agents);
    }

    private static string Mode(string key, string mode) =>
        Orca.AgentPermissions.Modes.Contains(mode) ? mode : throw new InvalidDataException($"agent_permissions.{key}: \"{mode}\" is not a permission mode");

    private static AgentSleepPolicy Sleep(JsonObject sleep)
    {
        var enabled = OrcaJson.Bool(sleep, "enabled") ?? throw new InvalidDataException("agent_sleep.enabled: required");
        var minutes = OrcaJson.Integer(sleep, "idle_minutes") ?? AgentSleepPolicy.DefaultMinutes;
        return minutes is >= 1 and <= 24 * 60
            ? new(enabled, (int)minutes)
            : throw new InvalidDataException("agent_sleep.idle_minutes: must be 1 to 1440");
    }
}

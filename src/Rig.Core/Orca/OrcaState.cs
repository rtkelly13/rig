using System.Text.Json.Nodes;

namespace Rig.Core.Orca;

/// <summary>The settings document of Orca's profile-state store, as far as rig checks it.</summary>
/// <param name="AgentDefaultArgs">Each agent's stored custom arguments (<c>agentDefaultArgs</c>).</param>
/// <param name="WorkspaceDir">Where Orca creates worktrees (<c>workspaceDir</c>).</param>
/// <param name="AgentSleep">Whether Agent sleep is on (<c>experimentalAgentHibernation</c>).</param>
/// <param name="AgentSleepIdleMs">Agent sleep's idle window (<c>agentHibernationIdleMs</c>).</param>
/// <param name="PermissionDefault">The default permission mode (<c>agentPermissionDefault</c>).</param>
/// <param name="PermissionOverrides">Each agent's permission mode (<c>agentPermissionOverrides</c>).</param>
public sealed record OrcaSettings(
    IReadOnlyDictionary<string, string>? AgentDefaultArgs = null,
    string? WorkspaceDir = null,
    bool? AgentSleep = null,
    long? AgentSleepIdleMs = null,
    string? PermissionDefault = null,
    IReadOnlyDictionary<string, string>? PermissionOverrides = null)
{
    /// <summary>The settings in a stored payload, or null when it is not a settings document.</summary>
    public static OrcaSettings? Parse(string payload)
    {
        try
        {
            var json = OrcaJson.Object(payload);
            return new(
                OrcaJson.StringMap(json, "agentDefaultArgs"),
                OrcaJson.String(json, "workspaceDir"),
                OrcaJson.Bool(json, "experimentalAgentHibernation"),
                OrcaJson.Integer(json, "agentHibernationIdleMs"),
                OrcaJson.String(json, "agentPermissionDefault"),
                OrcaJson.StringMap(json, "agentPermissionOverrides"));
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}

/// <summary>A repo's icon as Orca stores it, or as <c>repo.update</c> takes it.</summary>
/// <param name="Type"><c>lucide</c>, <c>emoji</c> or <c>image</c>.</param>
/// <param name="Name">A lucide icon's name.</param>
/// <param name="Emoji">An emoji icon's emoji.</param>
/// <param name="Src">An image icon's URL.</param>
/// <param name="Source">An image icon's origin: <c>github</c> or <c>upload</c>.</param>
/// <param name="Label">Orca's label for it.</param>
public sealed record OrcaRepoIcon(
    string Type = "",
    string? Name = null,
    string? Emoji = null,
    string? Src = null,
    string? Source = null,
    string? Label = null)
{
    /// <summary>The lucide icons Orca's picker offers; its renderer draws a folder for any other name.</summary>
    public static readonly IReadOnlyCollection<string> Lucide = new SortedSet<string>(StringComparer.Ordinal)
    {
        "Folder", "Code2", "SquareTerminal", "Bot", "Package", "Database", "Globe", "Server", "Cpu", "Layers",
        "Braces", "Rocket", "Wrench", "Briefcase", "Building2", "Palette", "Gauge", "Sparkles", "Shapes", "Box",
    };

    /// <summary>
    /// The icon a policy names: <c>emoji:…</c> is an emoji, an http(s) URL an image, and anything else a lucide name
    /// (with any <c>lucide:</c> prefix dropped).
    /// </summary>
    public static OrcaRepoIcon For(string icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        var trimmed = icon.Trim();
        if (trimmed.StartsWith("emoji:", StringComparison.Ordinal))
        {
            return new("emoji", Emoji: trimmed["emoji:".Length..].Trim());
        }

        if (trimmed.StartsWith("http://", StringComparison.Ordinal) || trimmed.StartsWith("https://", StringComparison.Ordinal))
        {
            return new("image", Src: trimmed, Source: trimmed.Contains("github.com", StringComparison.Ordinal) ? "github" : "upload");
        }

        return new("lucide", Name: (trimmed.StartsWith("lucide:", StringComparison.Ordinal) ? trimmed["lucide:".Length..] : trimmed).Trim());
    }

    /// <summary>Whether Orca draws <paramref name="icon"/> as given: an emoji, an image URL, or a lucide name from its picker.</summary>
    public static bool Renders(string icon) => For(icon) is not { Type: "lucide" } payload || Lucide.Contains(payload.Name ?? "");

    /// <summary>The icon as <c>repo.update</c> takes it, null fields left out.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["type"] = Type };
        foreach (var (key, value) in new[] { ("name", Name), ("emoji", Emoji), ("src", Src), ("source", Source), ("label", Label) })
        {
            if (value is not null)
            {
                json[key] = value;
            }
        }

        return json;
    }

    internal static OrcaRepoIcon FromJson(JsonObject json) => new(
        OrcaJson.String(json, "type") ?? "",
        OrcaJson.String(json, "name") ?? "",
        OrcaJson.String(json, "emoji") ?? "",
        OrcaJson.String(json, "src") ?? "",
        OrcaJson.String(json, "source") ?? "",
        OrcaJson.String(json, "label") ?? "");
}

/// <summary>One repo as <c>orca repo list --json</c> reports it.</summary>
/// <param name="Path">Its clone.</param>
/// <param name="DisplayName">The name Orca shows, which the policy keys repos by.</param>
/// <param name="HookSettings">Its local setup and archive settings.</param>
/// <param name="ExternalWorktreeVisibility">Whether worktrees made outside Orca show.</param>
/// <param name="WorktreeBaseRef">The base ref for new worktrees.</param>
/// <param name="WorktreeBasePath">Where its worktrees go.</param>
/// <param name="BadgeColor">Its badge colour.</param>
/// <param name="RepoIcon">Its icon.</param>
public sealed record OrcaRepo(
    string Path,
    string DisplayName = "",
    HookSettings? HookSettings = null,
    string? ExternalWorktreeVisibility = null,
    string? WorktreeBaseRef = null,
    string? WorktreeBasePath = null,
    string? BadgeColor = null,
    OrcaRepoIcon? RepoIcon = null)
{
    /// <summary>The base ref, or <c>""</c>.</summary>
    public string BaseRef => WorktreeBaseRef ?? "";

    /// <summary>The badge colour, or <c>""</c>.</summary>
    public string Color => BadgeColor ?? "";

    /// <summary>What identifies its icon: the lucide name, the emoji or the image URL; <c>""</c> when it has none.</summary>
    public string IconIdentifier => RepoIcon?.Type switch
    {
        "lucide" => RepoIcon.Name ?? "",
        "emoji" => RepoIcon.Emoji ?? "",
        "image" => RepoIcon.Src ?? "",
        _ => "",
    };

    /// <summary>Whether its icon is the one <paramref name="want"/> names.</summary>
    public bool IconMatches(string want)
    {
        if (RepoIcon is null)
        {
            return false;
        }

        var payload = OrcaRepoIcon.For(want);
        return payload.Type == RepoIcon.Type && payload.Type switch
        {
            "lucide" => payload.Name == RepoIcon.Name,
            "emoji" => payload.Emoji == RepoIcon.Emoji,
            "image" => payload.Src == RepoIcon.Src,
            _ => false,
        };
    }

    /// <summary>The repos in <c>orca repo list --json</c>'s output, or null when it is not a repo list.</summary>
    public static IReadOnlyList<OrcaRepo>? ParseList(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        try
        {
            if (OrcaJson.Object(OrcaJson.Object(output), "result") is not { } result)
            {
                return null;
            }

            return [.. (OrcaJson.Array(result, "repos") ?? []).Select(node => node is JsonObject repo ? FromJson(repo) : throw new InvalidDataException("repos: expected objects"))];
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static OrcaRepo FromJson(JsonObject json) => new(
        OrcaJson.String(json, "path") ?? throw new InvalidDataException("path: required"),
        OrcaJson.String(json, "displayName") ?? "",
        OrcaJson.Object(json, "hookSettings") is { } hooks ? HookSettings.FromJson(hooks) : null,
        OrcaJson.String(json, "externalWorktreeVisibility"),
        OrcaJson.String(json, "worktreeBaseRef"),
        OrcaJson.String(json, "worktreeBasePath"),
        OrcaJson.String(json, "badgeColor"),
        OrcaJson.Object(json, "repoIcon") is { } icon ? OrcaRepoIcon.FromJson(icon) : null);
}

/// <summary>Orca's settings as read from one copy of its store.</summary>
/// <param name="Source">The database file they came from.</param>
/// <param name="Saved">When that copy was last written.</param>
/// <param name="Settings">The settings.</param>
public sealed record SettingsCopy(string Source, DateTimeOffset Saved, OrcaSettings Settings);

/// <summary>Orca as last saved. Null fields mean that half could not be read.</summary>
/// <param name="Settings">Its stored settings.</param>
/// <param name="Repos">Its registered repos.</param>
/// <param name="ProfileDir">Where the settings were looked for.</param>
/// <param name="LaunchArgs">
/// What the running Orca launches each agent with (permission flags plus custom arguments), or null when it could not be asked.
/// </param>
public sealed record OrcaSnapshot(
    SettingsCopy? Settings,
    IReadOnlyList<OrcaRepo>? Repos,
    string ProfileDir,
    IReadOnlyDictionary<string, string>? LaunchArgs = null);

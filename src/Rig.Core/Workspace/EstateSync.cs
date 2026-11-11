using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Policy;
using Rig.Core.Roots;

namespace Rig.Core.Workspace;

/// <summary>
/// Writes the files the personal folder is generated with: its <c>AGENTS.md</c> router, which maps every active
/// managed project, and <c>personal.code-workspace</c>, which opens every one that is cloned. Ported from
/// <c>rig.workspace.engine</c>'s <c>sync_estate_router</c> and <c>sync_estate_code_workspace</c>.
/// </summary>
/// <param name="scopes">The estate's org folders, where org-owned projects are cloned.</param>
public sealed class EstateSync(EstateScopes scopes)
{
    /// <summary>Opens the projects table in the router template.</summary>
    public const string TableStart = "<!-- MANAGED_PROJECTS_START -->";

    /// <summary>Closes the projects table in the router template.</summary>
    public const string TableEnd = "<!-- MANAGED_PROJECTS_END -->";

    /// <summary>The code workspace's file name in the personal folder.</summary>
    public const string CodeWorkspaceName = "personal.code-workspace";

    /// <summary>The router template, embedded from <c>root-agents-md.md</c>.</summary>
    public static string Template
    {
        get
        {
            using var stream = typeof(EstateSync).Assembly.GetManifestResourceStream("Rig.Core.Workspace.root-agents-md.md")
                ?? throw new InvalidOperationException("The estate router template is not embedded.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>The router's Markdown table: one row per enabled project, linking its folder and its AGENTS.md.</summary>
    public string ProjectsTable(IEnumerable<ManagedRepoSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(specs);
        List<string> lines =
        [
            "| Repository | Domain | Tech Stack | Description | Scope Guide |",
            "|---|---|---|---|---|",
        ];
        foreach (var spec in specs.Where(spec => spec.Enabled))
        {
            var rel = scopes.ProjectRelPath(spec.Name, spec.Repo);
            var link = rel.StartsWith("../", StringComparison.Ordinal) ? rel : $"./{rel}";
            lines.Add($"| [`{spec.Name}`]({link}) | {Or(spec.Domain, "General")} | {Or(spec.Stack, "-")} | {Or(spec.Description, "-")} | [{rel}/AGENTS.md]({link}/AGENTS.md) |");
        }

        return string.Join('\n', lines);
    }

    /// <summary><paramref name="template"/> with <see cref="ProjectsTable"/> between its markers.</summary>
    public string RootAgentsMd(IEnumerable<ManagedRepoSpec> specs, string? template = null)
    {
        var text = template ?? Template;
        var start = text.IndexOf(TableStart, StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf(TableEnd, start + TableStart.Length, StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            throw new InvalidDataException($"The root AGENTS.md template is missing its slot markers: '{TableStart}' and '{TableEnd}'");
        }

        var before = text[..start].TrimEnd();
        var after = text[(end + TableEnd.Length)..].TrimStart();
        return $"{before}\n\n{TableStart}\n{ProjectsTable(specs)}\n{TableEnd}\n\n{after}";
    }

    /// <summary>
    /// Writes <paramref name="estateDir"/>'s <c>AGENTS.md</c>, and removes a <c>CLAUDE.md</c> link beside it: Claude
    /// Code reads AGENTS.md itself, and prefers a CLAUDE.md when both exist, so a stale one would shadow it. Only a
    /// link is removed, never someone's real file. Returns the file written.
    /// </summary>
    public async Task<string> SyncRouterAsync(string estateDir, IEnumerable<ManagedRepoSpec> specs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(estateDir);
        var agents = Path.Join(estateDir, "AGENTS.md");
        await File.WriteAllTextAsync(agents, RootAgentsMd(specs), cancellationToken).ConfigureAwait(false);
        var claude = new FileInfo(Path.Join(estateDir, "CLAUDE.md"));
        if (claude.LinkTarget is not null)
        {
            claude.Delete();
        }

        return agents;
    }

    /// <summary>
    /// Writes <paramref name="estateDir"/>'s code workspace: a folder per enabled project that is cloned, by name
    /// ignoring case, keeping the settings the file already has. Returns the file written.
    /// </summary>
    public async Task<string> SyncCodeWorkspaceAsync(string estateDir, IEnumerable<ManagedRepoSpec> specs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(estateDir);
        ArgumentNullException.ThrowIfNull(specs);
        var file = Path.Join(estateDir, CodeWorkspaceName);
        var folders = new JsonArray();
        foreach (var spec in specs.Where(spec => spec.Enabled).OrderBy(spec => spec.Name.ToLowerInvariant(), StringComparer.Ordinal))
        {
            if (Path.Exists(scopes.ProjectDir(estateDir, spec.Name, spec.Repo)))
            {
                folders.Add(new JsonObject { ["name"] = spec.Name, ["path"] = scopes.ProjectRelPath(spec.Name, spec.Repo) });
            }
        }

        var payload = new JsonObject { ["folders"] = folders, ["settings"] = await SettingsAsync(file, cancellationToken).ConfigureAwait(false) };
        await File.WriteAllTextAsync(file, PyJson.Dumps(payload) + "\n", cancellationToken).ConfigureAwait(false);
        return file;
    }

    // The settings object the workspace file holds now; none when it is missing, unreadable or not that shape.
    private static async Task<JsonObject> SettingsAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
            return root is JsonObject workspace && workspace["settings"] is JsonObject settings ? (JsonObject)settings.DeepClone() : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static string Or(string text, string fallback) => text.Length > 0 ? text : fallback;
}
